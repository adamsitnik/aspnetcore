// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.Internal;

// Owns yielded receive buffers until consumption. The runtime owns cancellation,
// native draining, and rearming after provided-buffer exhaustion.
internal sealed class IoUringPipeReader : PipeReader, IValueTaskSource<ReadResult>
{
    private readonly Lock _lock = new();
    private readonly Func<CancellationToken, IAsyncEnumerable<IMemoryOwner<byte>>> _receive;
    private readonly PipeOptions _options;
    private readonly Func<Exception?, Exception?>? _onCompleted;
    private readonly Action<bool>? _onPause;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ManualResetValueTaskSourceCore<ReadResult> _readSource;
    private CancellationTokenRegistration _readCancellation;
    private IoUringBufferSegment? _head;
    private IoUringBufferSegment? _tail;
    private IoUringBufferSegment? _cachedSegments;
    private int _cachedSegmentCount;
    private int _headOffset;
    private long _written;
    private long _consumed;
    private long _examined;
    private ReadOnlySequence<byte> _readBuffer;
    private TaskCompletionSource? _resume;
    private Exception? _error;
    private bool _started;
    private bool _readerCompleted;
    private bool _writerCompleted;
    private bool _readOutstanding;
    private bool _awaiterPending;
    private bool _readActive;
    private bool _readCanceled;
    private bool _cancelNextRead;

    public IoUringPipeReader(Socket socket, PipeOptions options,
        Func<Exception?, Exception?>? onCompleted = null, Action<bool>? onPause = null)
        : this(socket.ReceiveMultishotAsync, options, onCompleted, onPause)
    {
    }

    internal IoUringPipeReader(Func<CancellationToken, IAsyncEnumerable<IMemoryOwner<byte>>> receive,
        PipeOptions options, Func<Exception?, Exception?>? onCompleted = null, Action<bool>? onPause = null)
    {
        _receive = receive;
        _options = options;
        _onCompleted = onCompleted;
        _onPause = onPause;
    }

    public Task Closed => _closed.Task;

    public void Start()
    {
        lock (_lock)
        {
            if (_started || _readerCompleted || _writerCompleted)
            {
                return;
            }

            _started = true;
        }

        _ = ReceiveAsync();
    }

    private async Task ReceiveAsync()
    {
        Exception? error = null;
        try
        {
            await foreach (IMemoryOwner<byte> owner in _receive(_stop.Token).ConfigureAwait(false))
            {
                Task? resume;
                ReadCompletion completion;
                lock (_lock)
                {
                    if (_readerCompleted || _writerCompleted)
                    {
                        owner.Dispose();
                        break;
                    }

                    IoUringBufferSegment segment;
                    try
                    {
                        if (_cachedSegments is null)
                        {
                            segment = new IoUringBufferSegment(owner, _written);
                        }
                        else
                        {
                            segment = _cachedSegments;
                            _cachedSegments = (IoUringBufferSegment?)segment.Next;
                            _cachedSegmentCount--;
                            segment.Initialize(owner, _written);
                        }
                    }
                    catch
                    {
                        owner.Dispose();
                        throw;
                    }

                    if (segment.Memory.IsEmpty)
                    {
                        owner.Dispose();
                        continue;
                    }

                    _written += segment.Memory.Length;
                    if (_tail is null)
                    {
                        _head = segment;
                    }
                    else
                    {
                        _tail.SetNext(segment);
                    }

                    _tail = segment;
                    completion = CompletePendingRead();

                    if (_options.PauseWriterThreshold > 0 && _written - _examined >= _options.PauseWriterThreshold)
                    {
                        _resume ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    resume = _resume?.Task;
                }

                completion.Publish(this);
                if (resume is not null)
                {
                    _onPause?.Invoke(true);
                    // Cancel the enumeration's token as well as this wait on shutdown: the
                    // native receive can still be active while its consumer is paused here.
                    await resume.WaitAsync(_stop.Token).ConfigureAwait(false);
                    await new SchedulerAwaitable(_options.WriterScheduler);
                    _onPause?.Invoke(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            try
            {
                error = _onCompleted is null ? error : _onCompleted(error);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            ReadCompletion completion;
            lock (_lock)
            {
                _error ??= error;
                _writerCompleted = true;
                completion = CompletePendingRead();
            }

            completion.Publish(this);
            // await foreach has disposed and drained the native enumerator before this point.
            _closed.TrySetResult();
        }
    }

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            ValidateRead();
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<ReadResult>(cancellationToken);
            }

            if (TryReadCore(out ReadResult result))
            {
                return new ValueTask<ReadResult>(result);
            }

            _readSource.Reset();
            _readOutstanding = _awaiterPending = true;
            short version = _readSource.Version;
            if (cancellationToken.CanBeCanceled)
            {
                _readCancellation = cancellationToken.UnsafeRegister(static state =>
                {
                    (IoUringPipeReader reader, short version, CancellationToken token) =
                        ((IoUringPipeReader, short, CancellationToken))state!;
                    reader.CancelRead(version, token);
                }, (this, version, cancellationToken));
            }

            return new ValueTask<ReadResult>(this, version);
        }
    }

    public override bool TryRead(out ReadResult result)
    {
        lock (_lock)
        {
            ValidateRead();
            return TryReadCore(out result);
        }
    }

    private void ValidateRead()
    {
        if (_readerCompleted)
        {
            throw new InvalidOperationException("Reading is not allowed after the reader was completed.");
        }

        if ((_readActive && !_readCanceled) || _readOutstanding)
        {
            throw new InvalidOperationException("The previous read must be advanced before reading again.");
        }

        _readActive = false;
        _readBuffer = default;
    }

    private bool TryReadCore(out ReadResult result)
    {
        if (_error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(_error);
        }

        if (_cancelNextRead || _writerCompleted || _written > _examined)
        {
            _readBuffer = _head is null ? ReadOnlySequence<byte>.Empty :
                new ReadOnlySequence<byte>(_head, _headOffset, _tail!, _tail!.Memory.Length);
            result = new ReadResult(_readBuffer, _cancelNextRead, _writerCompleted);
            _readCanceled = _cancelNextRead;
            _cancelNextRead = false;
            _readActive = true;
            return true;
        }

        result = default;
        return false;
    }

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        lock (_lock)
        {
            if (!_readActive)
            {
                throw new InvalidOperationException("There is no read result to advance.");
            }

            long consumedLength = _readBuffer.Slice(0, consumed).Length;
            long examinedLength = _readBuffer.Slice(0, examined).Length;
            if (consumedLength > examinedLength)
            {
                throw new InvalidOperationException("The examined position must not precede consumed bytes.");
            }

            _examined = _consumed + examinedLength;
            _consumed += consumedLength;
            _readActive = false;
            _readBuffer = default;

            while (_head is not null && consumedLength >= _head.Memory.Length - _headOffset)
            {
                consumedLength -= _head.Memory.Length - _headOffset;
                IoUringBufferSegment segment = _head;
                _head = (IoUringBufferSegment?)segment.Next;
                ReturnSegment(segment);
                _headOffset = 0;
            }

            if (_head is null)
            {
                _tail = null;
            }
            else
            {
                _headOffset += (int)consumedLength;

                // A parser may retain an arbitrarily large partial message while requesting
                // more data. Evacuate examined leases only after the ReadResult is retired;
                // otherwise it can exhaust the ring's shared pool and never receive the rest.
                for (IoUringBufferSegment? segment = _head;
                    _examined > _consumed && segment is not null && segment.Start < _examined;
                    segment = (IoUringBufferSegment?)segment.Next)
                {
                    segment.ReleaseProvidedBuffer(_options.Pool);
                }
            }

            if (_resume is not null && (_written - _examined < _options.ResumeWriterThreshold || _written == _examined))
            {
                TaskCompletionSource resume = _resume;
                _resume = null;
                resume.TrySetResult();
            }
        }
    }

    public override void CancelPendingRead()
    {
        ReadCompletion completion;
        lock (_lock)
        {
            if (_readerCompleted)
            {
                return;
            }

            _cancelNextRead = true;
            completion = CompletePendingRead();
        }

        completion.Publish(this);
    }

    private void ReturnSegment(IoUringBufferSegment segment)
    {
        segment.Reset();
        if (_cachedSegmentCount < 16)
        {
            segment.SetNext(_cachedSegments);
            _cachedSegments = segment;
            _cachedSegmentCount++;
        }
    }

    private void CancelRead(short version, CancellationToken token)
    {
        bool canceled = false;
        lock (_lock)
        {
            if (_awaiterPending && _readSource.Version == version)
            {
                _awaiterPending = false;
                canceled = true;
            }
        }

        if (canceled)
        {
            new ReadCompletion(default, new OperationCanceledException(token)).Publish(this);
        }
    }

    private ReadCompletion CompletePendingRead()
    {
        if (!_awaiterPending)
        {
            return default;
        }

        try
        {
            if (_readerCompleted)
            {
                throw new InvalidOperationException("Reading is not allowed after the reader was completed.");
            }

            if (!TryReadCore(out ReadResult result))
            {
                return default;
            }

            _awaiterPending = false;
            _readCancellation.Unregister();
            return new ReadCompletion(result, null);
        }
        catch (Exception ex)
        {
            _awaiterPending = false;
            _readCancellation.Unregister();
            return new ReadCompletion(default, ex);
        }
    }

    public override void Complete(Exception? exception = null)
    {
        ReadCompletion completion;
        lock (_lock)
        {
            if (_readerCompleted)
            {
                return;
            }

            _readerCompleted = true;
            _readActive = false;
            _readBuffer = default;
            completion = CompletePendingRead();
            while (_head is not null)
            {
                IoUringBufferSegment segment = _head;
                _head = (IoUringBufferSegment?)segment.Next;
                ReturnSegment(segment);
            }

            _tail = null;
            if (!_started)
            {
                _closed.TrySetResult();
            }
        }

        completion.Publish(this);
        _stop.Cancel();
    }

    public override async ValueTask CompleteAsync(Exception? exception = null)
    {
        Complete(exception);
        await Closed.ConfigureAwait(false);
    }

    internal void Abort(Exception reason)
    {
        ReadCompletion completion = default;
        lock (_lock)
        {
            if (!_writerCompleted)
            {
                _error = reason;
                _writerCompleted = true;
                completion = CompletePendingRead();
            }

            if (!_started)
            {
                _closed.TrySetResult();
            }
        }

        completion.Publish(this);
        _stop.Cancel();
    }

    ReadResult IValueTaskSource<ReadResult>.GetResult(short token)
    {
        lock (_lock)
        {
            if (_readSource.GetStatus(token) == ValueTaskSourceStatus.Pending || !_readOutstanding)
            {
                throw new InvalidOperationException("The pending read has not completed or has already been retrieved.");
            }

            try
            {
                return _readSource.GetResult(token);
            }
            finally
            {
                _readCancellation.Unregister();
                _readOutstanding = false;
            }
        }
    }

    ValueTaskSourceStatus IValueTaskSource<ReadResult>.GetStatus(short token) => _readSource.GetStatus(token);

    void IValueTaskSource<ReadResult>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        if (!_options.UseSynchronizationContext)
        {
            flags &= ~ValueTaskSourceOnCompletedFlags.UseSchedulingContext;
        }

        bool useCapturedScheduler = (flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) != 0 &&
            ((SynchronizationContext.Current is SynchronizationContext context && context.GetType() != typeof(SynchronizationContext)) ||
            TaskScheduler.Current != TaskScheduler.Default);
        if (useCapturedScheduler || _options.ReaderScheduler == PipeScheduler.ThreadPool)
        {
            _readSource.OnCompleted(continuation, state, token, flags);
            return;
        }

        ExecutionContext? executionContext = (flags & ValueTaskSourceOnCompletedFlags.FlowExecutionContext) != 0
            ? ExecutionContext.Capture() : null;
        SchedulerContinuation scheduled = new SchedulerContinuation(_options.ReaderScheduler, continuation, state, executionContext);
        _readSource.OnCompleted(static state => ((SchedulerContinuation)state!).Schedule(), scheduled, token, ValueTaskSourceOnCompletedFlags.None);
    }

    private readonly struct ReadCompletion(ReadResult result, Exception? error)
    {
        private readonly bool _ready = true;

        public void Publish(IoUringPipeReader reader)
        {
            if (_ready)
            {
                // io_uring already dispatches onto workers. Only external completion/cancellation
                // needs a hop to honor the ThreadPool scheduler.
                if (reader._options.ReaderScheduler == PipeScheduler.ThreadPool && !Thread.CurrentThread.IsThreadPoolThread)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(static state => state.Completion.Publish(state.Reader),
                        (Reader: reader, Completion: this), preferLocal: false);
                    return;
                }

                if (error is null)
                {
                    reader._readSource.SetResult(result);
                }
                else
                {
                    reader._readSource.SetException(error);
                }
            }
        }
    }

    private sealed class SchedulerContinuation(PipeScheduler scheduler, Action<object?> continuation,
        object? state, ExecutionContext? executionContext)
    {
        public void Schedule() => scheduler.Schedule(static value => ((SchedulerContinuation)value!).Invoke(), this);

        private void Invoke()
        {
            if (executionContext is null)
            {
                continuation(state);
            }
            else
            {
                ExecutionContext.Run(executionContext, static value =>
                {
                    SchedulerContinuation scheduled = (SchedulerContinuation)value!;
                    scheduled.InvokeWithoutContext();
                }, this);
            }
        }

        private void InvokeWithoutContext() => continuation(state);
    }

    private readonly struct SchedulerAwaitable(PipeScheduler scheduler) : ICriticalNotifyCompletion
    {
        public SchedulerAwaitable GetAwaiter() => this;
        public bool IsCompleted => scheduler == PipeScheduler.Inline;
        public void GetResult() { }
        public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);
        public void UnsafeOnCompleted(Action continuation) =>
            scheduler.Schedule(static state => ((Action)state!).Invoke(), continuation);
    }
}
