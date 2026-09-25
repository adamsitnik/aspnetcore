// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.Internal;

// Receives owned multishot buffers without copying on the common, fully-consumed path.
// Only receives are multishot; accepts and sends use ordinary socket operations.
// Output uses an ordinary Pipe and Socket.SendAsync.
internal sealed partial class IoUringMultishotConnection : TransportConnection
{
    private readonly Socket _socket;
    private readonly ILogger _logger;
    private readonly IoUringMultishotPipeReader _receiveReader;
    private readonly IDuplexPipe _originalTransport;
    private readonly Pipe _sendPipe;
    private readonly CancellationTokenSource _connectionClosedTokenSource = new();

    private readonly Lock _shutdownLock = new();
    private volatile Exception? _shutdownReason;
    private Task? _sendingTask;
    private Task? _closingTask;
    private Task? _disposeTask;
    private readonly TaskCompletionSource _waitForConnectionClosedTcs = new();
    private bool _connectionClosed;
    private readonly bool _finOnError;

    public static bool IsSupported => System.Threading.IoUring.IsSupported;

    internal IoUringMultishotConnection(Socket socket,
                              MemoryPool<byte> memoryPool,
                              ILogger logger,
                              PipeOptions inputOptions,
                              PipeOptions outputOptions,
                              bool finOnError = false)
    {
        Debug.Assert(socket is not null);
        Debug.Assert(memoryPool is not null);
        Debug.Assert(logger is not null);

        _socket = socket;
        MemoryPool = memoryPool;
        _logger = logger;
        _finOnError = finOnError;

        LocalEndPoint = _socket.LocalEndPoint;
        RemoteEndPoint = _socket.RemoteEndPoint;

        ConnectionClosed = _connectionClosedTokenSource.Token;

        _receiveReader = new IoUringMultishotPipeReader(socket, inputOptions, OnReceiveCompleted, OnReceivePaused);
        if (outputOptions.ReaderScheduler is IOQueue)
        {
            // IOQueue serializes send-loop continuations across connections sharing that queue.
            // Scheduling directly on the ThreadPool removes that shared bottleneck and queue hop,
            // while each connection's single send loop still preserves its write order.
            outputOptions = new PipeOptions(outputOptions.Pool, PipeScheduler.ThreadPool, outputOptions.WriterScheduler,
                outputOptions.PauseWriterThreshold, outputOptions.ResumeWriterThreshold,
                outputOptions.MinimumSegmentSize, outputOptions.UseSynchronizationContext);
        }
        _sendPipe = new Pipe(outputOptions);

        Transport = _originalTransport = new DuplexPipe(_receiveReader, _sendPipe.Writer);

        InitializeFeatures();
    }

    public PipeReader Output => _sendPipe.Reader;

    public override MemoryPool<byte> MemoryPool { get; }

    public void Start()
    {
        _receiveReader.Start();
        _closingTask = AwaitReceiveClosedAsync();
        _sendingTask = DoSend();
    }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        // Try to gracefully close the socket to match libuv/SocketConnection behavior.
        Shutdown(abortReason);

        // Cancel DoSend's pending read after calling shutdown to ensure the correct
        // _shutdownReason gets set, same as SocketConnection.
        Output.CancelPendingRead();

    }

    // Only called after connection middleware is complete which means the ConnectionClosed token has fired.
    public override ValueTask DisposeAsync()
    {
        lock (_shutdownLock)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        _originalTransport.Input.Complete();
        _originalTransport.Output.Complete();

        try
        {
            if (_closingTask is not null)
            {
                await _closingTask;
            }

            if (_sendingTask is not null)
            {
                await _sendingTask;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(0, ex, $"Unexpected exception in {nameof(IoUringMultishotConnection)}.{nameof(DisposeAsync)}.");
        }
        _connectionClosedTokenSource.Dispose();
    }

    private Exception? OnReceiveCompleted(Exception? error)
    {
        if (_shutdownReason is not null)
        {
            return _shutdownReason;
        }

        if (error is SocketException socketError && IsConnectionResetError(socketError.SocketErrorCode))
        {
            SocketsLog.ConnectionReset(_logger, this);
            return new ConnectionResetException(socketError.Message, socketError);
        }

        if (error is not null)
        {
            SocketsLog.ConnectionError(_logger, this, error);
        }
        else
        {
            SocketsLog.ConnectionReadFin(_logger, this);
        }

        return error;
    }

    private void OnReceivePaused(bool paused)
    {
        if (paused)
        {
            SocketsLog.ConnectionPause(_logger, this);
        }
        else
        {
            SocketsLog.ConnectionResume(_logger, this);
        }
    }

    private async Task AwaitReceiveClosedAsync()
    {
        try
        {
            await _receiveReader.Closed;
        }
        finally
        {
            FireConnectionClosed();

            await _waitForConnectionClosedTcs.Task;
        }
    }

    // This long-running method currently performs better with AggressiveOptimization, see https://github.com/dotnet/runtime/issues/133672.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private async Task DoSend()
    {
        Exception? shutdownReason = null;
        Exception? unexpectedError = null;
        List<ArraySegment<byte>>? bufferList = null;

        try
        {
            while (true)
            {
                ReadResult result = await Output.ReadAsync();

                if (result.IsCanceled)
                {
                    break;
                }
                ReadOnlySequence<byte> buffer = result.Buffer;

                if (!buffer.IsEmpty)
                {
                    try
                    {
                        // Match SocketConnection's batch boundary even when io_uring completes a partial send.
                        ReadOnlySequence<byte> remaining = buffer;
                        while (!remaining.IsEmpty)
                        {
                            int bytesTransferred;
                            if (remaining.IsSingleSegment)
                            {
                                bytesTransferred = await _socket.SendAsync(remaining.First, SocketFlags.None);
                            }
                            else
                            {
                                bufferList ??= new List<ArraySegment<byte>>();
                                foreach (ReadOnlyMemory<byte> segment in remaining)
                                {
                                    bufferList.Add(segment.GetArray());
                                }

                                try
                                {
                                    bytesTransferred = await _socket.SendAsync(bufferList, SocketFlags.None);
                                }
                                finally
                                {
                                    bufferList.Clear();
                                }
                            }

                            remaining = remaining.Slice(bytesTransferred);
                        }
                    }
                    catch (SocketException ex)
                    {
                        if (IsConnectionResetError(ex.SocketErrorCode))
                        {
                            shutdownReason = new ConnectionResetException(ex.Message, ex);
                            SocketsLog.ConnectionReset(_logger, this);

                            break;
                        }

                        if (IsConnectionAbortError(ex.SocketErrorCode))
                        {
                            shutdownReason = ex;

                            break;
                        }

                        unexpectedError = shutdownReason = ex;
                    }
                }

                Output.AdvanceTo(buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (ObjectDisposedException ex)
        {
            // This should always be ignored since Shutdown() must have already been called by Abort().
            shutdownReason = ex;
        }
        catch (Exception ex)
        {
            shutdownReason = ex;
            unexpectedError = ex;
            SocketsLog.ConnectionError(_logger, this, unexpectedError);
        }
        finally
        {
            Shutdown(shutdownReason);

            // Complete the output after disposing the socket
            Output.Complete(unexpectedError);
        }
    }

    private void FireConnectionClosed()
    {
        // Guard against scheduling this multiple times
        if (_connectionClosed)
        {
            return;
        }

        _connectionClosed = true;

        ThreadPool.UnsafeQueueUserWorkItem(state =>
        {
            state.CancelConnectionClosedToken();

            state._waitForConnectionClosedTcs.TrySetResult();
        },
        this,
        preferLocal: false);
    }

    private void Shutdown(Exception? shutdownReason)
    {
        lock (_shutdownLock)
        {
            if (_shutdownReason is not null)
            {
                return;
            }

            // Make sure to dispose the socket after the volatile _shutdownReason is set, same
            // rationale as SocketConnection.Shutdown.
            _shutdownReason = shutdownReason ?? new ConnectionAbortedException("The io_uring transport's send loop completed gracefully.");

            // Ask the kernel to stop the multishot recv op *before* touching the socket below -
            // the op holds a reference to the underlying file descriptor for as long as it is
            // live, so disposing/closing the socket first could block waiting for that reference
            // to be released instead of the other way around.
            _receiveReader.Abort(_shutdownReason);

            // NB: not _shutdownReason since we don't want to do this on graceful completion
            if (!_finOnError && shutdownReason is not null)
            {
                SocketsLog.ConnectionWriteRst(_logger, this, shutdownReason.Message);

                // This forces an abortive close with linger time 0 (and implies Dispose)
                _socket.Close(timeout: 0);
                return;
            }

            SocketsLog.ConnectionWriteFin(_logger, this, _shutdownReason.Message);

            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // Ignore any errors from Socket.Shutdown() since we're tearing down the connection anyway.
            }

            _socket.Dispose();
        }
    }

    private void CancelConnectionClosedToken()
    {
        try
        {
            _connectionClosedTokenSource.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogError(0, ex, $"Unexpected exception in {nameof(IoUringMultishotConnection)}.{nameof(CancelConnectionClosedToken)}.");
        }
    }

    private static bool IsConnectionResetError(SocketError errorCode)
    {
        return errorCode == SocketError.ConnectionReset ||
               errorCode == SocketError.Shutdown ||
               (errorCode == SocketError.ConnectionAborted && OperatingSystem.IsWindows());
    }

    private static bool IsConnectionAbortError(SocketError errorCode)
    {
        // Calling Dispose after ReceiveAsync can cause an "InvalidArgument" error on *nix.
        return errorCode == SocketError.OperationAborted ||
               errorCode == SocketError.Interrupted ||
               (errorCode == SocketError.InvalidArgument && !OperatingSystem.IsWindows());
    }
}
