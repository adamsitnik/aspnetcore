// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.Internal;

internal sealed class IoUringAcceptQueue
{
    private readonly Socket _listenSocket;
    private readonly SocketTransportOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Socket> _channel = Channel.CreateBounded<Socket>(
        new BoundedChannelOptions(1) { SingleReader = false, SingleWriter = true });

    public Task Closed { get; private set; } = Task.CompletedTask;

    public IoUringAcceptQueue(Socket listenSocket, SocketTransportOptions options)
    {
        _listenSocket = listenSocket;
        _options = options;
    }

    public void Start() => Closed = AcceptLoopAsync();

    public async ValueTask<Socket?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Socket socket = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (_stop.IsCancellationRequested)
            {
                socket.Dispose();
                return null;
            }

            return socket;
        }
        catch (ChannelClosedException) when (_stop.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task AcceptLoopAsync()
    {
        Exception? error = null;
        try
        {
            await foreach (Socket socket in _listenSocket.AcceptMultishotAsync(_stop.Token).ConfigureAwait(false))
            {
                bool transferred = false;
                try
                {
                    if (socket.LocalEndPoint is IPEndPoint)
                    {
                        socket.NoDelay = _options.NoDelay;
                    }

                    await _channel.Writer.WriteAsync(socket, _stop.Token).ConfigureAwait(false);
                    transferred = true;
                }
                finally
                {
                    if (!transferred)
                    {
                        socket.Dispose();
                    }
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
            _channel.Writer.TryComplete(error);
            while (_channel.Reader.TryRead(out Socket? socket))
            {
                socket.Dispose();
            }
        }
    }

    public void Stop()
    {
        _stop.Cancel();
        _channel.Writer.TryComplete();
    }
}
