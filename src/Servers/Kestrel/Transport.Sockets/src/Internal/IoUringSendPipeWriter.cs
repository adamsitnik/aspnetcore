// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.Internal;

internal sealed class IoUringSendPipeWriter : PipeWriter, IThreadPoolWorkItem
{
    private readonly Socket _socket;
    private readonly Pipe _pipe;
    private readonly Func<Exception?, Exception?> _onCompleted;
    private readonly Lock _sync = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SendOperation _first;
    private readonly SendOperation _second;
    private SendOperation? _active;
    private ReadOnlySequence<byte> _buffer;
    private ReadOnlySequence<byte> _remaining;
    private bool _readCompleted;
    private bool _started;
    private bool _aborted;
    private bool _finishing;
    private Exception? _error;

    internal IoUringSendPipeWriter(Socket socket, Pipe pipe, Func<Exception?, Exception?> onCompleted)
    {
        _socket = socket;
        _pipe = pipe;
        _onCompleted = onCompleted;
        _first = new SendOperation(this);
        _second = new SendOperation(this);
    }

    internal Task Completion => _completion.Task;
    public override bool CanGetUnflushedBytes => _pipe.Writer.CanGetUnflushedBytes;
    public override long UnflushedBytes => _pipe.Writer.UnflushedBytes;
    public override void Advance(int bytes) => _pipe.Writer.Advance(bytes);
    public override Memory<byte> GetMemory(int sizeHint = 0) => _pipe.Writer.GetMemory(sizeHint);
    public override Span<byte> GetSpan(int sizeHint = 0) => _pipe.Writer.GetSpan(sizeHint);
    public override void CancelPendingFlush() => _pipe.Writer.CancelPendingFlush();

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        ValueTask<FlushResult> flush = _pipe.Writer.FlushAsync(cancellationToken);
        TryStart();
        return flush;
    }

    public override ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        ValueTask<FlushResult> flush = _pipe.Writer.WriteAsync(source, cancellationToken);
        TryStart();
        return flush;
    }

    public override void Complete(Exception? exception = null)
    {
        _pipe.Writer.Complete(exception);
        TryStart();
    }

    internal void Start()
    {
        lock (_sync)
        {
            _started = true;
        }
        TryStart();
    }

    internal void Abort()
    {
        lock (_sync)
        {
            if (_finishing)
            {
                return;
            }
            _aborted = true;
            if (_active is not null)
            {
                _active.RequestCancellation();
            }
            else
            {
                Finish(null);
            }
        }
    }

    private void TryStart()
    {
        lock (_sync)
        {
            if (!_started || _finishing || _active is not null)
            {
                return;
            }

            // The previous terminal callback can still be returning to the runtime, which
            // clears IsPending afterward. A second identity avoids spinning or losing this flush.
            SendOperation operation = _first.IsPending ? _second : _first;
            Debug.Assert(!operation.IsPending);
            try
            {
                if (TryRead(operation))
                {
                    if (!IoUring.TrySubmit(_socket.SafeHandle, operation))
                    {
                        throw new InvalidOperationException("The io_uring send could not be submitted.");
                    }
                }
            }
            catch (Exception error)
            {
                operation.ReleasePins();
                _active = null;
                Finish(error);
            }
        }
    }

    private bool TryRead(SendOperation operation)
    {
        if (!_pipe.Reader.TryRead(out ReadResult result))
        {
            return false;
        }

        _buffer = result.Buffer;
        _remaining = _buffer;
        _readCompleted = result.IsCompleted;
        if (_buffer.IsEmpty || result.IsCanceled)
        {
            _pipe.Reader.AdvanceTo(_buffer.End);
            _buffer = default;
            _remaining = default;
            if (result.IsCompleted || result.IsCanceled)
            {
                Finish(null);
            }
            return false;
        }

        operation.Prepare(_remaining);
        _active = operation;
        return true;
    }

    private IoUringOperationStatus OnCompleted(SendOperation operation, int result)
    {
        lock (_sync)
        {
            Debug.Assert(_active == operation);
            operation.ReleasePins();
            try
            {
                if (_aborted)
                {
                    _active = null;
                    Finish(null);
                    return IoUringOperationStatus.Done;
                }
                if (result <= 0)
                {
                    if (result == 0)
                    {
                        throw new IOException("The io_uring send completed without making progress.");
                    }
                    Marshal.SetLastPInvokeError(-result);
                    throw new SocketException();
                }

                _remaining = _remaining.Slice(result);
                if (!_remaining.IsEmpty)
                {
                    operation.Prepare(_remaining);
                    return IoUringOperationStatus.ReSubmit;
                }

                _pipe.Reader.AdvanceTo(_buffer.End);
                _buffer = default;
                _remaining = default;
                _active = null;
                if (_readCompleted)
                {
                    Finish(null);
                }
                else if (TryRead(operation))
                {
                    return IoUringOperationStatus.ReSubmit;
                }
            }
            catch (Exception error)
            {
                operation.ReleasePins();
                _active = null;
                Finish(error);
            }
            return IoUringOperationStatus.Done;
        }
    }

    private void Finish(Exception? error)
    {
        Debug.Assert(_active is null);
        if (!_finishing)
        {
            _finishing = true;
            _error = error;
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }
    }

    void IThreadPoolWorkItem.Execute()
    {
        try
        {
            _pipe.Reader.Complete(_onCompleted(_error));
            _buffer = default;
            _remaining = default;
            _completion.TrySetResult();
        }
        catch (Exception error)
        {
            _completion.TrySetException(error);
        }
    }

    private sealed unsafe class SendOperation(IoUringSendPipeWriter owner) : IoUringOperation
    {
        private const int MsgNoSignal = 0x4000;
        private const int MaxVectors = 1024;
        private MemoryHandle _singlePin;
        private MemoryHandle _vectorsPin;
        private MemoryHandle _messagePin;
        private MemoryHandle[]? _pins;
        private NativeVector[]? _vectors;
        private NativeMessage[]? _message;
        private int _pinCount;
        private IoUringRequest _request;

        public override IoUringRequest Request => _request;

        internal void Prepare(ReadOnlySequence<byte> buffer)
        {
            Debug.Assert(!buffer.IsEmpty);
            if (buffer.IsSingleSegment)
            {
                _singlePin = buffer.First.Pin();
                _request = IoUringRequest.Send((nint)_singlePin.Pointer, buffer.First.Length, MsgNoSignal);
                return;
            }

            int count = 0;
            foreach (ReadOnlyMemory<byte> segment in buffer)
            {
                if (!segment.IsEmpty && ++count == MaxVectors)
                {
                    break;
                }
            }

            if (_vectors is null || _vectors.Length < count)
            {
                _vectors = new NativeVector[count];
                _pins = new MemoryHandle[count];
            }
            _message ??= new NativeMessage[1];
            int available = int.MaxValue;
            foreach (ReadOnlyMemory<byte> segment in buffer)
            {
                if (segment.IsEmpty)
                {
                    continue;
                }
                int length = Math.Min(segment.Length, available);
                MemoryHandle pin = segment.Pin();
                _pins![_pinCount] = pin;
                _vectors[_pinCount++] = new NativeVector { Buffer = pin.Pointer, Length = (nuint)length };
                available -= length;
                if (_pinCount == MaxVectors || available == 0)
                {
                    break;
                }
            }

            _vectorsPin = _vectors.AsMemory().Pin();
            _message[0] = new NativeMessage { Vectors = _vectorsPin.Pointer, VectorCount = (nuint)_pinCount };
            _messagePin = _message.AsMemory().Pin();
            _request = IoUringRequest.SendMessage((nint)_messagePin.Pointer, MsgNoSignal);
        }

        internal void ReleasePins()
        {
            _singlePin.Dispose();
            _singlePin = default;
            for (int index = 0; index < _pinCount; index++)
            {
                _pins![index].Dispose();
                _pins[index] = default;
            }
            _pinCount = 0;
            _vectorsPin.Dispose();
            _vectorsPin = default;
            _messagePin.Dispose();
            _messagePin = default;
        }

        public override IoUringOperationStatus IssuerThread(int result, uint flags, long sequence) =>
            owner.OnCompleted(this, result);

        public override void Execute() => throw new UnreachableException();

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeVector
        {
            public void* Buffer;
            public nuint Length;
        }

        // Native Linux msghdr; pointer-sized fields retain the ABI's natural alignment.
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public void* Name;
            public uint NameLength;
            public void* Vectors;
            public nuint VectorCount;
            public void* Control;
            public nuint ControlLength;
            public int Flags;
        }
    }
}
