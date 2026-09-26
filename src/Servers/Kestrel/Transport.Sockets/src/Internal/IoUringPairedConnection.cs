// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.Internal;

// Deliberately limited to sequential, single-buffer request/response traffic.
internal sealed class IoUringPairedConnection : TransportConnection, IConnectionSocketFeature, IValueTaskSource<ReadResult>
{
    internal const int BufferSize = 16 * 1024;
    private readonly Lock _sync = new();
    private readonly Socket _socket;
    private readonly ILogger _logger;
    private readonly byte[] _input = new byte[BufferSize];
    private readonly byte[] _output = new byte[BufferSize];
    private GCHandle _inputPin;
    private GCHandle _outputPin;
    private readonly ReadOperation _receive;
    private readonly SendOperation _send;
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource _closedSignaled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _signalClosed;
    private readonly Reader _reader;
    private readonly Writer _writer;
    private ManualResetValueTaskSourceCore<ReadResult> _readSource;
    private ReadOnlySequence<byte> _readBuffer;
    private CancellationTokenRegistration _readCancellation;
    private int _inputStart;
    private int _inputLength;
    private int _outputLength;
    private bool _readActive;
    private bool _receiving;
    private bool _deferredRead;
    private bool _awaitingRead;
    private bool _examinedAll;
    private bool _outputQueued;
    private bool _eof;
    private bool _inputCompleted;
    private bool _closing;
    private bool _cancelNextRead;
    private Exception? _error;
    private Task? _disposeTask;

    internal IoUringPairedConnection(Socket socket, MemoryPool<byte> memoryPool, ILogger logger)
    {
        _socket = socket;
        _logger = logger;
        MemoryPool = memoryPool;
        LocalEndPoint = socket.LocalEndPoint;
        RemoteEndPoint = socket.RemoteEndPoint;
        _inputPin = GCHandle.Alloc(_input, GCHandleType.Pinned);
        try
        {
            _outputPin = GCHandle.Alloc(_output, GCHandleType.Pinned);
        }
        catch
        {
            _inputPin.Free();
            throw;
        }
        _receive = new ReadOperation(this, _inputPin.AddrOfPinnedObject());
        _send = new SendOperation(this, _outputPin.AddrOfPinnedObject());
        _reader = new Reader(this);
        _writer = new Writer(this);
        Transport = new DuplexPipe(_reader, _writer);
        ConnectionClosed = _closed.Token;
        _currentIConnectionSocketFeature = this;
    }

    public Socket Socket => _socket;
    public override MemoryPool<byte> MemoryPool { get; }

    public override void Abort(ConnectionAbortedException abortReason) => Close(abortReason);

    private void Close(Exception? error)
    {
        bool reset = error is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.Shutdown };
        if (reset)
        {
            error = new ConnectionResetException(error!.Message, error);
        }
        bool completeRead;
        lock (_sync)
        {
            if (_closing)
            {
                return;
            }
            _closing = true;
            _error = error;
            completeRead = _awaitingRead;
            _awaitingRead = false;
            _receive.RequestCancellation();
            _send.RequestCancellation();
        }

        if (reset)
        {
            SocketsLog.ConnectionReset(_logger, this);
        }
        else if (error is not null)
        {
            SocketsLog.ConnectionError(_logger, this, error);
        }
        // Closing may be initiated by application code, never run its pending read inline.
        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            (IoUringPairedConnection connection, bool complete) = state;
            if (complete)
            {
                if (connection._error is Exception failure)
                {
                    connection._readSource.SetException(failure);
                }
                else
                {
                    connection._readSource.SetResult(new ReadResult(default, isCanceled: false, isCompleted: true));
                }
            }
            connection.SignalClosed();
        }, (this, completeRead), preferLocal: false);
    }

    private void SignalClosed()
    {
        if (Interlocked.Exchange(ref _signalClosed, 1) != 0)
        {
            return;
        }
        try
        {
            _closed.Cancel();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Exception from an io_uring connection-closed callback.");
        }
        finally
        {
            _closedSignaled.SetResult();
        }
    }

    public override ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Close(null);
        // Only teardown waits; no send waiter or polling exists on the request path.
        while (_receive.IsPending || _send.IsPending)
        {
            await Task.Delay(1).ConfigureAwait(false);
        }
        await _readCancellation.DisposeAsync().ConfigureAwait(false);
        _socket.Dispose();
        _inputPin.Free();
        _outputPin.Free();
        await _closedSignaled.Task.ConfigureAwait(false);
        _closed.Dispose();
    }

    private ReadResult CreateReadResult(bool canceled = false)
    {
        _readBuffer = new ReadOnlySequence<byte>(_input.AsMemory(_inputStart, _inputLength));
        _readActive = true;
        return new ReadResult(_readBuffer, canceled, _eof || _closing);
    }

    private ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_readActive || _awaitingRead)
            {
                throw new InvalidOperationException("The previous read must be consumed and advanced first.");
            }
            if (_error is not null)
            {
                return ValueTask.FromException<ReadResult>(_error);
            }
            if (_cancelNextRead || (_inputLength != 0 && !_examinedAll) || _eof || _closing)
            {
                bool canceled = _cancelNextRead;
                _cancelNextRead = false;
                return new ValueTask<ReadResult>(CreateReadResult(canceled));
            }
            _readSource.Reset();
            _awaitingRead = true;
            short version = _readSource.Version;
            try
            {
                if (!_receiving)
                {
                    if (_outputQueued)
                    {
                        SubmitOutput();
                    }
                    else
                    {
                        PrepareReceive();
                        _receiving = true;
                        if (!IoUring.TrySubmit(_socket.SafeHandle, _receive))
                        {
                            _receiving = false;
                            throw new InvalidOperationException("io_uring receive submission is unavailable.");
                        }
                    }
                }
                if (cancellationToken.CanBeCanceled)
                {
                    _readCancellation = cancellationToken.UnsafeRegister(static (state, token) =>
                        ((IoUringPairedConnection)state!).Close(new OperationCanceledException(token)), this);
                }
                return new ValueTask<ReadResult>(this, version);
            }
            catch
            {
                if (!_receive.IsPending)
                {
                    _receiving = false;
                }
                _awaitingRead = false;
                throw;
            }
        }
    }

    private void PrepareReceive()
    {
        if (_readActive || _receiving)
        {
            throw new InvalidOperationException("Cannot overwrite an active receive buffer.");
        }
        if (_inputLength == BufferSize)
        {
            throw new InvalidOperationException("The request exceeds the experiment's 16 KiB input buffer.");
        }
        if (_inputStart != 0)
        {
            _input.AsSpan(_inputStart, _inputLength).CopyTo(_input);
            _inputStart = 0;
        }
        _receive.Offset = _inputLength;
    }

    private void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        lock (_sync)
        {
            if (!_readActive)
            {
                throw new InvalidOperationException("There is no active read to advance.");
            }
            int consumedLength = checked((int)_readBuffer.Slice(0, consumed).Length);
            int examinedLength = checked((int)_readBuffer.Slice(0, examined).Length);
            if (examinedLength < consumedLength)
            {
                throw new ArgumentException("Examined data cannot precede consumed data.", nameof(examined));
            }
            _examinedAll = examinedLength == _inputLength;
            _inputStart += consumedLength;
            _inputLength -= consumedLength;
            _readActive = false;
            _readBuffer = default;
            if (_outputQueued && !_closing)
            {
                SubmitOutput();
            }
        }
    }

    private void SubmitOutput(bool final = false)
    {
        if (_readActive && !final)
        {
            return;
        }
        if (_send.IsPending || (!final && (_inputLength != 0 || _receiving)))
        {
            throw new InvalidOperationException("The paired experiment does not support pipelining or overlapping response writes.");
        }
        _send.Offset = 0;
        _send.Length = _outputLength;
        bool submitted;
        if (_eof || final)
        {
            submitted = IoUring.TrySubmit(_socket.SafeHandle, _send);
        }
        else
        {
            PrepareReceive();
            _receiving = true;
            try
            {
                submitted = IoUring.TrySubmit(_socket.SafeHandle, _send, _receive);
            }
            catch
            {
                _receiving = false;
                throw;
            }
        }
        if (!submitted)
        {
            _receiving = false;
            throw new InvalidOperationException("io_uring paired submission is unavailable.");
        }
        _outputLength = 0;
        _outputQueued = false;
    }

    private void ReceiveCompleted(int result)
    {
        ReadResult readResult = default;
        bool publish;
        lock (_sync)
        {
            // Native completion is not enough: the reusable operation remains owned until this worker runs.
            _receiving = false;
            if (_closing)
            {
                return;
            }
            if (_inputCompleted)
            {
                result = 0;
            }
            if (result < 0)
            {
                Close(GetSocketException(result));
                return;
            }
            _inputLength += result;
            _examinedAll = false;
            _eof = result == 0;
            publish = _awaitingRead;
            _awaitingRead = false;
            if (publish)
            {
                readResult = CreateReadResult();
            }
        }
        if (publish)
        {
            _readSource.SetResult(readResult);
        }
        if (result == 0)
        {
            // Let a response to already consumed input finish before output completion closes us.
            SignalClosed();
        }
    }

    private static SocketException GetSocketException(int result)
    {
        // The int constructor takes SocketError, not native errno; the default constructor translates it.
        int previous = Marshal.GetLastPInvokeError();
        Marshal.SetLastPInvokeError(-result);
        SocketException exception = new SocketException();
        Marshal.SetLastPInvokeError(previous);
        return exception;
    }

    ReadResult IValueTaskSource<ReadResult>.GetResult(short token)
    {
        _readCancellation.Dispose();
        return _readSource.GetResult(token);
    }

    ValueTaskSourceStatus IValueTaskSource<ReadResult>.GetStatus(short token) => _readSource.GetStatus(token);

    void IValueTaskSource<ReadResult>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _readSource.OnCompleted(continuation, state, token, flags);

    private sealed class ReadOperation(IoUringPairedConnection connection, IntPtr address) : IoUringOperation
    {
        internal int Offset;
        private int _result;
        public override IoUringRequest Request => IoUringRequest.Receive(address + Offset, BufferSize - Offset);

        public override IoUringOperationStatus IssuerThread(int result, uint flags, long sequence)
        {
            _result = result;
            if (result >= 0 && connection._send.IsPending)
            {
                // Both callbacks run on the same issuer. Keep input private until output is reusable.
                connection._deferredRead = true;
                return IoUringOperationStatus.Done;
            }
            return IoUringOperationStatus.Schedule;
        }

        public override void Execute() => connection.ReceiveCompleted(_result);
    }

    private sealed class SendOperation(IoUringPairedConnection connection, IntPtr address) : IoUringOperation
    {
        internal int Offset;
        internal int Length;
        private int _error;
        private bool _completeRead;
        public override IoUringRequest Request => IoUringRequest.Send(address + Offset, Length - Offset);

        public override IoUringOperationStatus IssuerThread(int result, uint flags, long sequence)
        {
            if (result > 0)
            {
                Offset += result;
                if (Offset != Length)
                {
                    return IoUringOperationStatus.ReSubmit;
                }
                _completeRead = connection._deferredRead;
                connection._deferredRead = false;
                return _completeRead ? IoUringOperationStatus.Schedule : IoUringOperationStatus.Done;
            }
            _completeRead = false;
            _error = result;
            return IoUringOperationStatus.Schedule;
        }

        public override void Execute()
        {
            if (_completeRead)
            {
                connection._receive.Execute();
            }
            else
            {
                connection.Close(_error == 0
                    ? new IOException("The io_uring send completed without making progress.")
                    : GetSocketException(_error));
            }
        }
    }

    private sealed class Reader(IoUringPairedConnection connection) : PipeReader
    {
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
            connection.ReadAsync(cancellationToken);

        public override bool TryRead(out ReadResult result)
        {
            lock (connection._sync)
            {
                if (!connection._readActive && !connection._awaitingRead &&
                    (connection._cancelNextRead || (connection._inputLength != 0 && !connection._examinedAll) || connection._eof || connection._closing))
                {
                    if (connection._error is Exception error)
                    {
                        throw error;
                    }
                    result = connection.CreateReadResult(connection._cancelNextRead);
                    connection._cancelNextRead = false;
                    return true;
                }
                result = default;
                return false;
            }
        }

        public override void AdvanceTo(SequencePosition consumed) => connection.AdvanceTo(consumed, consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => connection.AdvanceTo(consumed, examined);
        public override void Complete(Exception? exception = null)
        {
            if (exception is not null)
            {
                connection.Close(exception);
                return;
            }
            lock (connection._sync)
            {
                connection._inputCompleted = true;
                connection._receive.RequestCancellation();
            }
        }
        public override void CancelPendingRead()
        {
            bool publish;
            ReadResult result = default;
            lock (connection._sync)
            {
                publish = connection._awaitingRead;
                connection._awaitingRead = false;
                connection._cancelNextRead = !publish;
                if (publish)
                {
                    // Do not expose bytes still owned by a pending receive.
                    result = new ReadResult(default, isCanceled: true, isCompleted: false);
                    connection._readActive = true;
                    connection._readBuffer = default;
                }
            }
            if (publish)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static state =>
                    state.Connection._readSource.SetResult(state.Result),
                    (Connection: connection, Result: result), preferLocal: false);
            }
        }
    }

    private sealed class Writer(IoUringPairedConnection connection) : PipeWriter
    {
        public override bool CanGetUnflushedBytes => true;
        public override long UnflushedBytes => connection._outputLength;

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            lock (connection._sync)
            {
                ObjectDisposedException.ThrowIf(connection._closing, connection);
                if (connection._send.IsPending || connection._outputQueued)
                {
                    throw new InvalidOperationException("The send buffer is still owned by an earlier response.");
                }
                if ((uint)sizeHint > (uint)(BufferSize - connection._outputLength))
                {
                    throw new InvalidOperationException("The response exceeds the experiment's 16 KiB output buffer.");
                }
                return connection._output.AsMemory(connection._outputLength);
            }
        }

        public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public override void Advance(int bytes)
        {
            lock (connection._sync)
            {
                if (connection._send.IsPending || connection._outputQueued ||
                    (uint)bytes > (uint)(BufferSize - connection._outputLength))
                {
                    throw new InvalidOperationException("Cannot advance a pending or overflowing response.");
                }
                connection._outputLength += bytes;
            }
        }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (connection._sync)
            {
                if (!connection._closing && connection._outputLength != 0 && !connection._outputQueued)
                {
                    connection._outputQueued = true;
                    connection.SubmitOutput();
                }
                return new ValueTask<FlushResult>(new FlushResult(isCanceled: false, isCompleted: connection._closing));
            }
        }

        public override void CancelPendingFlush() => connection.Close(new OperationCanceledException());

        public override void Complete(Exception? exception = null)
        {
            if (exception is not null)
            {
                connection.Close(exception);
                return;
            }
            _ = ObserveCompletionAsync();
        }

        private async Task ObserveCompletionAsync()
        {
            try
            {
                await CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                connection.Close(exception);
            }
        }

        public override async ValueTask CompleteAsync(Exception? exception = null)
        {
            if (exception is null)
            {
                lock (connection._sync)
                {
                    if (!connection._closing && connection._outputLength != 0)
                    {
                        connection._outputQueued = true;
                        connection.SubmitOutput(final: true);
                    }
                }
                while (connection._send.IsPending)
                {
                    await Task.Delay(1).ConfigureAwait(false);
                }
            }
            connection.Close(exception);
        }
    }
}
