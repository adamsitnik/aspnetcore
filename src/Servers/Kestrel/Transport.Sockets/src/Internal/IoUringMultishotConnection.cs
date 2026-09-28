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
// Flushes submit output to io_uring without an intermediate send-loop continuation.
internal sealed partial class IoUringMultishotConnection : TransportConnection
{
    private readonly Socket _socket;
    private readonly ILogger _logger;
    private readonly IoUringMultishotPipeReader _receiveReader;
    private readonly IDuplexPipe _originalTransport;
    private readonly Pipe _sendPipe;
    private readonly IoUringSendPipeWriter _sendWriter;
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
        // AdvanceTo runs on an issuer. Only backpressured writers need a continuation,
        // and neither an inline scheduler nor a captured context may run it on that issuer.
        _sendPipe = new Pipe(new PipeOptions(outputOptions.Pool, PipeScheduler.ThreadPool, PipeScheduler.ThreadPool,
            outputOptions.PauseWriterThreshold, outputOptions.ResumeWriterThreshold,
            outputOptions.MinimumSegmentSize, useSynchronizationContext: false));
        _sendWriter = new IoUringSendPipeWriter(socket, _sendPipe, OnSendCompleted);

        Transport = _originalTransport = new DuplexPipe(_receiveReader, _sendWriter);

        InitializeFeatures();
    }

    public PipeReader Output => _sendPipe.Reader;

    public override MemoryPool<byte> MemoryPool { get; }

    public void Start()
    {
        _receiveReader.Start();
        _closingTask = AwaitReceiveClosedAsync();
        _sendingTask = _sendWriter.Completion;
        _sendWriter.Start();
    }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        // Try to gracefully close the socket to match libuv/SocketConnection behavior.
        Shutdown(abortReason);

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

    private Exception? OnSendCompleted(Exception? error)
    {
        Exception? shutdownReason = error;
        Exception? unexpectedError = null;
        if (error is SocketException socketError)
        {
            if (IsConnectionResetError(socketError.SocketErrorCode))
            {
                shutdownReason = new ConnectionResetException(socketError.Message, socketError);
                SocketsLog.ConnectionReset(_logger, this);
            }
            else if (!IsConnectionAbortError(socketError.SocketErrorCode))
            {
                unexpectedError = error;
            }
        }
        else if (error is not null and not ObjectDisposedException)
        {
            unexpectedError = error;
        }
        if (unexpectedError is not null)
        {
            SocketsLog.ConnectionError(_logger, this, unexpectedError);
        }

        Shutdown(shutdownReason);
        return unexpectedError;
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
            _shutdownReason = shutdownReason ?? new ConnectionAbortedException("The io_uring transport's output completed gracefully.");

            _sendWriter.Abort();
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
