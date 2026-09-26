// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.FunctionalTests;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.Internal;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using KestrelHttpMethod = Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.HttpMethod;
using KestrelHttpVersion = Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.HttpVersion;

namespace Microsoft.AspNetCore.Server.Kestrel.Sockets.FunctionalTests;

#nullable enable

public class SocketTransportTests : LoggedTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PairedTransportDefersReadUntilPendingSendFinishes(bool reset)
    {
        if (!System.Threading.IoUring.IsSupported)
        {
            return;
        }
        using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.ReceiveBufferSize = 1024;
        client.ReceiveTimeout = 3000;
        client.Connect(listener.LocalEndPoint!);
        using Socket server = listener.Accept();
        server.SendBufferSize = 1024;
        server.Blocking = false;
        await using IoUringPairedConnection connection = new IoUringPairedConnection(server, MemoryPool<byte>.Shared, NullLogger.Instance);
        Task<ReadResult> first = connection.Transport.Input.ReadAsync().AsTask();
        Assert.Equal(1, client.Send(new byte[] { 1 }));
        ReadResult input = await first.DefaultTimeout();
        connection.Transport.Input.AdvanceTo(input.Buffer.End);

        byte[] padding = new byte[64 * 1024];
        int primed = 0;
        try
        {
            while (true)
            {
                primed += server.Send(padding);
            }
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.WouldBlock)
        {
        }

        connection.Transport.Output.GetSpan(IoUringPairedConnection.BufferSize)[..IoUringPairedConnection.BufferSize].Fill(42);
        connection.Transport.Output.Advance(IoUringPairedConnection.BufferSize);
        await connection.Transport.Output.FlushAsync();
        Task<ReadResult> next = connection.Transport.Input.ReadAsync().AsTask();
        Type type = typeof(IoUringPairedConnection);
        System.Threading.IoUringOperation send = (System.Threading.IoUringOperation)type.GetField("_send", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connection)!;
        System.Threading.IoUringOperation receive = (System.Threading.IoUringOperation)type.GetField("_receive", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connection)!;
        Assert.True(send.IsPending);
        Assert.Equal(1, client.Send(new byte[] { 2 }));
        Assert.True(SpinWait.SpinUntil(() => !receive.IsPending, TimeSpan.FromSeconds(10)));
        Assert.True(send.IsPending);
        Assert.False(next.IsCompleted);

        if (reset)
        {
            client.LingerState = new LingerOption(true, 0);
            client.Dispose();
            await Assert.ThrowsAsync<ConnectionResetException>(() => next).DefaultTimeout();
            return;
        }

        Task drain = Task.Run(() =>
        {
            byte[] response = new byte[primed + IoUringPairedConnection.BufferSize];
            int count = 0;
            while (count < response.Length)
            {
                int received = client.Receive(response.AsSpan(count));
                Assert.True(received > 0);
                count += received;
            }
            Assert.All(response.AsSpan(primed).ToArray(), value => Assert.Equal(42, value));
        });
        input = await next.DefaultTimeout();
        Assert.False(send.IsPending);
        Assert.False(connection.Transport.Output.GetMemory(1).IsEmpty);
        await drain.DefaultTimeout();
        Assert.Equal(new byte[] { 2 }, input.Buffer.ToArray());
        connection.Transport.Input.AdvanceTo(input.Buffer.End);
    }

    [Fact]
    public async Task PairedTransportDisposeDoesNotBlockReadCancellation()
    {
        if (!System.Threading.IoUring.IsSupported)
        {
            return;
        }
        using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listener.LocalEndPoint!);
        using Socket server = listener.Accept();
        await using IoUringPairedConnection connection = new IoUringPairedConnection(server, MemoryPool<byte>.Shared, NullLogger.Instance);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        ValueTask<ReadResult> pending = connection.Transport.Input.ReadAsync(cancellation.Token);
        using ManualResetEventSlim cancellationStarted = new ManualResetEventSlim();
        using CancellationTokenRegistration marker = cancellation.Token.Register(cancellationStarted.Set);
        Type type = typeof(IoUringPairedConnection);
        Lock gate = (Lock)type.GetField("_sync", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connection)!;
        System.Threading.IoUringOperation operation = (System.Threading.IoUringOperation)type.GetField("_receive", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connection)!;
        Thread cancellationThread = new Thread(cancellation.Cancel) { IsBackground = true };
        Task disposal;
        lock (gate)
        {
            Assert.Equal(1, client.Send(new byte[] { 1 }));
            Assert.True(SpinWait.SpinUntil(() => !operation.IsPending, TimeSpan.FromSeconds(10)));
            cancellationThread.Start();
            Assert.True(cancellationStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(SpinWait.SpinUntil(
                () => (cancellationThread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(10)));
            disposal = connection.DisposeAsync().AsTask();
        }
        Assert.True(cancellationThread.Join(TimeSpan.FromSeconds(10)));
        await disposal.DefaultTimeout();
        Assert.True((await pending).IsCompleted);
    }

    [Fact]
    public async Task PairedTransportDoesNotReuseReceiveBeforeWorkerDelivery()
    {
        if (!System.Threading.IoUring.IsSupported)
        {
            return;
        }
        using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listener.LocalEndPoint!);
        using Socket server = listener.Accept();
        await using IoUringPairedConnection connection = new IoUringPairedConnection(server, MemoryPool<byte>.Shared, NullLogger.Instance);
        Task<ReadResult> first = connection.Transport.Input.ReadAsync().AsTask();
        Assert.Equal(1, client.Send(new byte[] { 1 }));
        ReadResult result = await first.DefaultTimeout();
        connection.Transport.Input.AdvanceTo(result.Buffer.End);
        connection.Transport.Output.GetSpan(1)[0] = 2;
        connection.Transport.Output.Advance(1);
        await connection.Transport.Output.FlushAsync();
        Assert.Equal(1, client.Receive(new byte[1]));

        Type type = typeof(IoUringPairedConnection);
        Lock gate = (Lock)type.GetField("_sync", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connection)!;
        System.Threading.IoUringOperation operation = (System.Threading.IoUringOperation)type.GetField("_receive", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connection)!;
        Task<ReadResult> second;
        lock (gate)
        {
            // Real native completion can proceed, but its worker cannot publish input while this gate is held.
            Assert.Equal(1, client.Send(new byte[] { 3 }));
            Assert.True(SpinWait.SpinUntil(() => !operation.IsPending, TimeSpan.FromSeconds(10)));
            second = connection.Transport.Input.ReadAsync().AsTask();
            Assert.False(operation.IsPending);
        }
        result = await second.DefaultTimeout();
        Assert.Equal(new byte[] { 3 }, result.Buffer.ToArray());
        connection.Transport.Input.AdvanceTo(result.Buffer.End);
        connection.Transport.Output.GetSpan(1)[0] = 4;
        connection.Transport.Output.Advance(1);
        await connection.Transport.Output.FlushAsync();
        byte[] response = new byte[1];
        Assert.Equal(1, client.Receive(response));
        Assert.Equal(4, response[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PairedTransportCompletesFinalOutput(bool completeInputFirst)
    {
        if (!System.Threading.IoUring.IsSupported)
        {
            return;
        }
        using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.ReceiveTimeout = 10000;
        client.Connect(listener.LocalEndPoint!);
        using Socket server = listener.Accept();
        await using IoUringPairedConnection connection = new IoUringPairedConnection(server, MemoryPool<byte>.Shared, NullLogger.Instance);
        Task<ReadResult> pending = connection.Transport.Input.ReadAsync().AsTask();
        Assert.Equal(1, client.Send(new byte[] { 42 }));
        ReadResult result = await pending.DefaultTimeout();
        connection.Transport.Output.GetSpan(1)[0] = 43;
        connection.Transport.Output.Advance(1);
        await connection.Transport.Output.FlushAsync();
        if (completeInputFirst)
        {
            connection.Transport.Input.Complete();
        }
        await connection.Transport.Output.CompleteAsync().AsTask().DefaultTimeout();
        byte[] response = new byte[1];
        Assert.Equal(1, client.Receive(response));
        Assert.Equal(43, response[0]);
        connection.Transport.Input.AdvanceTo(result.Buffer.End);
    }

    [Fact]
    public async Task PairedTransportHostedJson()
    {
        IHostBuilder builder = TransportSelector.GetHostBuilder().ConfigureWebHost(web =>
            web.UseKestrel().UseUrls("http://127.0.0.1:0").Configure(app =>
                app.Run(context =>
                {
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength = "{\"message\":\"Hello, World!\"}"u8.Length;
                    return context.Response.WriteAsync("{\"message\":\"Hello, World!\"}");
                })));
        using IHost host = builder.Build();
        await host.StartAsync().DefaultTimeout();
        try
        {
            using HttpClient client = new HttpClient();
            for (int index = 0; index < 100; index++)
            {
                string response = await client.GetStringAsync($"http://127.0.0.1:{host.GetPort()}/json").DefaultTimeout();
                Assert.Equal("{\"message\":\"Hello, World!\"}", response);
            }
        }
        finally
        {
            await host.StopAsync().DefaultTimeout();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PairedTransportPreservesFragmentedRequests(bool fragmented)
    {
        if (!System.Threading.IoUring.IsSupported)
        {
            return;
        }
        using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listener.LocalEndPoint!);
        using Socket server = listener.Accept();
        await using IoUringPairedConnection connection = new IoUringPairedConnection(server, MemoryPool<byte>.Shared, NullLogger.Instance);
        byte[] request = [1, 2, 3, 4];
        byte[] reply = new byte[4];
        for (int iteration = 0; iteration < 20; iteration++)
        {
            Task<ReadResult> pending = connection.Transport.Input.ReadAsync().AsTask();
            Assert.Equal(fragmented ? 2 : 4, client.Send(request.AsSpan(0, fragmented ? 2 : 4)));
            ReadResult result = await pending.DefaultTimeout();
            if (fragmented)
            {
                Assert.Equal(2, result.Buffer.Length);
                connection.Transport.Input.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                pending = connection.Transport.Input.ReadAsync().AsTask();
                Assert.Equal(2, client.Send(request.AsSpan(2)));
                result = await pending.DefaultTimeout();
            }
            Assert.Equal(request, result.Buffer.ToArray());
            connection.Transport.Input.AdvanceTo(result.Buffer.End);
            request.CopyTo(connection.Transport.Output.GetMemory(request.Length));
            connection.Transport.Output.Advance(request.Length);
            await connection.Transport.Output.FlushAsync();
            int received = 0;
            while (received < reply.Length)
            {
                int count = client.Receive(reply.AsSpan(received));
                Assert.True(count > 0);
                received += count;
            }
            Assert.Equal(request, reply);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PairedTransportRejectsOverflowAndDrainsCancellation(bool input)
    {
        if (!System.Threading.IoUring.IsSupported)
        {
            return;
        }
        using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listener.LocalEndPoint!);
        using Socket server = listener.Accept();
        await using IoUringPairedConnection connection = new IoUringPairedConnection(server, MemoryPool<byte>.Shared, NullLogger.Instance);
        if (input)
        {
            Task<ReadResult> pending = connection.Transport.Input.ReadAsync().AsTask();
            byte[] payload = new byte[IoUringPairedConnection.BufferSize];
            Assert.Equal(payload.Length, client.Send(payload));
            while (true)
            {
                ReadResult result = await pending.DefaultTimeout();
                connection.Transport.Input.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                if (result.Buffer.Length == payload.Length)
                {
                    break;
                }
                pending = connection.Transport.Input.ReadAsync().AsTask();
            }
            Assert.Throws<InvalidOperationException>(() => connection.Transport.Input.ReadAsync());
        }
        else
        {
            Assert.Equal(IoUringPairedConnection.BufferSize, connection.Transport.Output.GetMemory(IoUringPairedConnection.BufferSize).Length);
            Assert.Throws<InvalidOperationException>(() => connection.Transport.Output.GetMemory(IoUringPairedConnection.BufferSize + 1));
            Task<ReadResult> pending = connection.Transport.Input.ReadAsync().AsTask();
            connection.Abort(new ConnectionAbortedException("Test cancellation"));
            await Assert.ThrowsAsync<ConnectionAbortedException>(() => pending);
        }
        await connection.DisposeAsync().AsTask().DefaultTimeout();
        Assert.True(server.SafeHandle.IsClosed);
    }

    [Theory]
    [InlineData(nameof(SocketsLog.ConnectionReadFin), 6, false)]
    [InlineData(nameof(SocketsLog.ConnectionReadFin), 6, true)]
    [InlineData(nameof(SocketsLog.ConnectionWriteFin), 7, false)]
    [InlineData(nameof(SocketsLog.ConnectionWriteFin), 7, true)]
    [InlineData(nameof(SocketsLog.ConnectionWriteRst), 8, false)]
    [InlineData(nameof(SocketsLog.ConnectionWriteRst), 8, true)]
    [InlineData(nameof(SocketsLog.ConnectionError), 14, false)]
    [InlineData(nameof(SocketsLog.ConnectionError), 14, true)]
    [InlineData(nameof(SocketsLog.ConnectionReset), 19, false)]
    [InlineData(nameof(SocketsLog.ConnectionReset), 19, true)]
    [InlineData(nameof(SocketsLog.ConnectionPause), 4, false)]
    [InlineData(nameof(SocketsLog.ConnectionPause), 4, true)]
    [InlineData(nameof(SocketsLog.ConnectionResume), 5, false)]
    [InlineData(nameof(SocketsLog.ConnectionResume), 5, true)]
    public async Task SocketLogsOnlyAccessConnectionIdWhenEnabled(string eventName, int eventId, bool enabled)
    {
        Mock<ILogger> logger = new Mock<ILogger>();
        logger.Setup(value => value.IsEnabled(LogLevel.Debug)).Returns(enabled);
        using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using SocketSenderPool senders = new SocketSenderPool(PipeScheduler.Inline);
        await using SocketConnection connection = new SocketConnection(socket, MemoryPool<byte>.Shared,
            PipeScheduler.Inline, logger.Object, senders, PipeOptions.Default, PipeOptions.Default);
        FieldInfo connectionId = typeof(SocketConnection).BaseType!.GetField("_connectionId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Null(connectionId.GetValue(connection));
        IOException error = new IOException("Test error");

        switch (eventName)
        {
            case nameof(SocketsLog.ConnectionReadFin):
                SocketsLog.ConnectionReadFin(logger.Object, connection);
                break;
            case nameof(SocketsLog.ConnectionWriteFin):
                SocketsLog.ConnectionWriteFin(logger.Object, connection, "Test shutdown");
                break;
            case nameof(SocketsLog.ConnectionWriteRst):
                SocketsLog.ConnectionWriteRst(logger.Object, connection, "Test shutdown");
                break;
            case nameof(SocketsLog.ConnectionError):
                SocketsLog.ConnectionError(logger.Object, connection, error);
                break;
            case nameof(SocketsLog.ConnectionReset):
                SocketsLog.ConnectionReset(logger.Object, connection);
                break;
            case nameof(SocketsLog.ConnectionPause):
                SocketsLog.ConnectionPause(logger.Object, connection);
                break;
            case nameof(SocketsLog.ConnectionResume):
                SocketsLog.ConnectionResume(logger.Object, connection);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(eventName));
        }

        IEnumerable<Moq.IInvocation> writes = logger.Invocations.Where(value => value.Method.Name == nameof(ILogger.Log));
        if (!enabled)
        {
            Assert.Null(connectionId.GetValue(connection));
            Assert.Empty(writes);
            return;
        }

        Assert.NotNull(connectionId.GetValue(connection));
        Moq.IInvocation write = Assert.Single(writes);
        Assert.Equal(LogLevel.Debug, write.Arguments[0]);
        Assert.Equal(new EventId(eventId, eventName), write.Arguments[1]);
        Assert.Equal(eventName, ((EventId)write.Arguments[1]).Name);
        Assert.Same(eventName == nameof(SocketsLog.ConnectionError) ? error : null, write.Arguments[3]);
        IReadOnlyList<KeyValuePair<string, object?>> state = Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<string, object?>>>(write.Arguments[2]);
        Assert.Contains(state, value => value.Key == nameof(ConnectionContext.ConnectionId) && Equals(value.Value, connection.ConnectionId));
        if (eventName is nameof(SocketsLog.ConnectionWriteFin) or nameof(SocketsLog.ConnectionWriteRst))
        {
            Assert.Contains(state, value => value.Key == "Reason" && Equals(value.Value, "Test shutdown"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SocketResetLogChecksEnabledWithoutConnection(bool enabled)
    {
        Mock<ILogger> logger = new Mock<ILogger>();
        logger.Setup(value => value.IsEnabled(LogLevel.Debug)).Returns(enabled);

        SocketsLog.ConnectionReset(logger.Object, connectionId: "(null)");

        Assert.Equal(enabled ? 1 : 0, logger.Invocations.Count(value => value.Method.Name == nameof(ILogger.Log)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultishotReaderThreadPoolSchedulerReusesCompletionWorker(bool worker)
    {
        ReceiveSource source = new ReceiveSource();
        IoUringMultishotPipeReader reader = new(source.ReadAsync,
            new PipeOptions(useSynchronizationContext: false));
        reader.Start();
        TaskCompletionSource completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool inline = false;
        ValueTaskAwaiter<ReadResult> awaiter = reader.ReadAsync().GetAwaiter();
        awaiter.UnsafeOnCompleted(() =>
        {
            try
            {
                Assert.True(Thread.CurrentThread.IsThreadPoolThread);
                ReadResult result = awaiter.GetResult();
                Assert.True(result.IsCanceled);
                reader.AdvanceTo(result.Buffer.End);
                inline = true;
                completed.SetResult();
            }
            catch (Exception error)
            {
                completed.SetException(error);
            }
        });
        void Publish()
        {
            try
            {
                reader.CancelPendingRead();
                if (worker)
                {
                    Assert.True(inline);
                }
                published.SetResult();
            }
            catch (Exception error)
            {
                published.SetException(error);
            }
        }
        try
        {
            if (worker)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static action => action(), (Action)Publish, preferLocal: false);
            }
            else
            {
                new Thread(Publish) { IsBackground = true }.Start();
            }
            await Task.WhenAll(published.Task, completed.Task).DefaultTimeout();
        }
        finally
        {
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    [Fact]
    public async Task MultishotReaderPreservesOrderAndPartialConsumption()
    {
        ReceiveSource source = new ReceiveSource();
        IoUringMultishotPipeReader reader = new(source.ReadAsync, PipeOptions.Default);
        reader.Start();
        try
        {
            TestOwner first = new TestOwner([1, 2, 3]);
            source.Write(first);
            ReadResult result = await reader.ReadAsync().AsTask().DefaultTimeout();
            Assert.True(result.Buffer.First.Equals(first.Memory));
            Assert.False(first.Disposed);
            reader.AdvanceTo(result.Buffer.GetPosition(1));
            Assert.False(first.Disposed);

            result = await reader.ReadAsync().AsTask().DefaultTimeout();
            Assert.Equal(new byte[] { 2, 3 }, result.Buffer.ToArray());
            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            Assert.True(first.Disposed);

            source.Write(new TestOwner([4, 5]));
            result = await reader.ReadAsync().AsTask().DefaultTimeout();
            Assert.Equal(new byte[] { 2, 3, 4, 5 }, result.Buffer.ToArray());
            reader.AdvanceTo(result.Buffer.End);
            source.Complete();
            result = await reader.ReadAsync().AsTask().DefaultTimeout();
            Assert.True(result.IsCompleted);
            Assert.True(result.Buffer.IsEmpty);
            reader.AdvanceTo(result.Buffer.End);
        }
        finally
        {
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    [Fact]
    public async Task MultishotReaderReturnsExaminedLeasesBeyondNativePoolCapacity()
    {
        const int BufferCount = 1100;
        const int BufferSize = 4096;
        TestOwner? previous = null;
        int returned = 0;
        async IAsyncEnumerable<IMemoryOwner<byte>> Receive([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (int index = 0; index < BufferCount; index++)
            {
                if (previous is not null)
                {
                    Assert.True(previous.Disposed);
                    returned++;
                }

                byte[] bytes = new byte[BufferSize];
                Array.Fill(bytes, (byte)(index % 251));
                previous = new TestOwner(bytes);
                yield return previous;
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        IoUringMultishotPipeReader reader = new(Receive,
            new PipeOptions(pauseWriterThreshold: BufferSize, resumeWriterThreshold: BufferSize / 2));
        reader.Start();
        try
        {
            for (int index = 0; index < BufferCount; index++)
            {
                ReadResult result = await reader.ReadAsync().AsTask().DefaultTimeout();
                Assert.Equal((long)(index + 1) * BufferSize, result.Buffer.Length);
                Assert.Equal((byte)0, result.Buffer.FirstSpan[0]);
                Assert.Equal((byte)(index % 251), result.Buffer.Slice((long)index * BufferSize).FirstSpan[0]);
                TestOwner current = previous!;
                Assert.False(current.Disposed);
                reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                Assert.True(current.Disposed);
            }

            ReadResult completed = await reader.ReadAsync().AsTask().DefaultTimeout();
            Assert.True(completed.IsCompleted);
            reader.AdvanceTo(completed.Buffer.End);
            Assert.Equal(BufferCount - 1, returned);
        }
        finally
        {
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultishotReaderCancellationDoesNotCancelReceive(bool useToken)
    {
        ReceiveSource source = new ReceiveSource();
        IoUringMultishotPipeReader reader = new(source.ReadAsync, PipeOptions.Default);
        reader.Start();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        try
        {
            Task<ReadResult> pending = reader.ReadAsync(useToken ? cancellation.Token : default).AsTask();
            if (useToken)
            {
                cancellation.Cancel();
                OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => pending.DefaultTimeout());
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }
            else
            {
                reader.CancelPendingRead();
                ReadResult canceled = await pending.DefaultTimeout();
                Assert.True(canceled.IsCanceled);
                reader.AdvanceTo(canceled.Buffer.End);
            }

            Assert.False(source.Stopped.Task.IsCompleted);
            source.Write(new TestOwner([42]));
            ReadResult result = await reader.ReadAsync().AsTask().DefaultTimeout();
            Assert.Equal(new byte[] { 42 }, result.Buffer.ToArray());
            reader.AdvanceTo(result.Buffer.End);
        }
        finally
        {
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultishotReaderCompletesPendingReadOnErrorOrEof(bool error)
    {
        ReceiveSource source = new ReceiveSource();
        IoUringMultishotPipeReader reader = new(source.ReadAsync, PipeOptions.Default);
        reader.Start();
        Task<ReadResult> pending = reader.ReadAsync().AsTask();
        SocketException reset = new SocketException((int)SocketError.ConnectionReset);
        source.Complete(error ? reset : null);
        try
        {
            if (error)
            {
                Assert.Same(reset, await Assert.ThrowsAsync<SocketException>(() => pending.DefaultTimeout()));
            }
            else
            {
                ReadResult result = await pending.DefaultTimeout();
                Assert.True(result.IsCompleted);
                reader.AdvanceTo(result.Buffer.End);
            }

            await reader.Closed.DefaultTimeout();
        }
        finally
        {
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task MultishotReaderCleanupCancelsPendingOrPausedReceive(bool paused, bool abort)
    {
        ReceiveSource source = new ReceiveSource();
        IoUringMultishotPipeReader reader = new(source.ReadAsync,
            new PipeOptions(pauseWriterThreshold: 4, resumeWriterThreshold: 2));
        reader.Start();
        TestOwner owner = new TestOwner([1, 2, 3, 4]);
        Task<ReadResult> pending = reader.ReadAsync().AsTask();
        if (paused)
        {
            source.Write(owner);
            ReadResult result = await pending.DefaultTimeout();
            Assert.False(owner.Disposed);
            reader.AdvanceTo(result.Buffer.Start, result.Buffer.Start);
        }

        IOException failure = new IOException("Stopped");
        if (abort)
        {
            reader.Abort(failure);
        }
        else
        {
            reader.Complete();
        }

        if (!paused)
        {
            if (abort)
            {
                Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => pending.DefaultTimeout()));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => pending.DefaultTimeout());
            }
        }

        await reader.Closed.DefaultTimeout();
        await source.Stopped.Task.DefaultTimeout();
        await reader.CompleteAsync().AsTask().DefaultTimeout();
        await reader.CompleteAsync().AsTask().DefaultTimeout();
        if (paused)
        {
            Assert.True(owner.Disposed);
        }
    }

    [Fact]
    public async Task MultishotReaderBackpressureUsesExaminedBytes()
    {
        ReceiveSource source = new ReceiveSource();
        TaskCompletionSource paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IoUringMultishotPipeReader reader = new(source.ReadAsync,
            new PipeOptions(pauseWriterThreshold: 4, resumeWriterThreshold: 2),
            onPause: value => { if (value) { paused.TrySetResult(); } });
        reader.Start();
        try
        {
            source.Write(new TestOwner([1, 2, 3, 4]));
            source.Write(new TestOwner([5]));
            await paused.Task.DefaultTimeout();
            ReadResult result = await reader.ReadAsync().AsTask().DefaultTimeout();
            Assert.Equal(4, result.Buffer.Length);
            reader.AdvanceTo(result.Buffer.Start, result.Buffer.Start);
            Assert.Equal(1, source.Yielded);

            result = await reader.ReadAsync().AsTask().DefaultTimeout();
            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            result = await reader.ReadAsync().AsTask().DefaultTimeout();
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, result.Buffer.ToArray());
            reader.AdvanceTo(result.Buffer.End);
        }
        finally
        {
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    [Fact]
    public async Task MultishotReaderStartFailureAndCompleteBeforeStartDoNotHang()
    {
        IOException failure = new IOException("Start failed");
        IoUringMultishotPipeReader reader = new(_ => throw failure, PipeOptions.Default);
        Task<ReadResult> pending = reader.ReadAsync().AsTask();
        reader.Start();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => pending.DefaultTimeout()));
        await reader.CompleteAsync().AsTask().DefaultTimeout();

        reader = new IoUringMultishotPipeReader(_ => throw failure, PipeOptions.Default);
        reader.Complete();
        reader.Start();
        await reader.Closed.DefaultTimeout();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MultishotReaderLoopbackReceivesAndDrains(bool retainExamined, bool asyncSend)
    {
        using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Task connecting = client.ConnectAsync(listener.LocalEndPoint!);
        using Socket server = await listener.AcceptAsync().DefaultTimeout();
        await connecting.DefaultTimeout();
        IoUringMultishotPipeReader reader = new(server, PipeOptions.Default);
        reader.Start();
        try
        {
            if (!IoUring.IsSupported)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => reader.ReadAsync().AsTask().DefaultTimeout());
                return;
            }

            byte[] payload = new byte[retainExamined ? 1025 * 4096 + 17 : 8193];
            for (int index = 0; index < payload.Length; index++)
            {
                payload[index] = (byte)(index % 251);
            }

            Task sending = Task.Run(async () =>
            {
                int sent = 0;
                while (sent != payload.Length)
                {
                    sent += asyncSend
                        ? await client.SendAsync(payload.AsMemory(sent), SocketFlags.None)
                        : client.Send(payload.AsSpan(sent), SocketFlags.None);
                }

                client.Shutdown(SocketShutdown.Send);
            });

            int received = 0;
            while (true)
            {
                ReadResult result = await reader.ReadAsync().AsTask().DefaultTimeout();
                Assert.True(!retainExamined || result.Buffer.Length >= received,
                    $"Retained buffer shrank: length={result.Buffer.Length}, received={received}, completed={result.IsCompleted}");
                byte[] bytes = (retainExamined ? result.Buffer.Slice(received) : result.Buffer).ToArray();
                Assert.True(received + bytes.Length <= payload.Length,
                    $"Received too many bytes: previous={received}, additional={bytes.Length}, expected={payload.Length}");
                Assert.Equal(payload.AsSpan(received, bytes.Length).ToArray(), bytes);
                received += bytes.Length;
                reader.AdvanceTo(retainExamined && !result.IsCompleted ? result.Buffer.Start : result.Buffer.End, result.Buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }

            Assert.Equal(payload.Length, received);
            await sending.DefaultTimeout();
        }
        finally
        {
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultishotConnectionClosesAndDisposesWithPendingOrPausedInput(bool paused)
    {
        using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Task connecting = client.ConnectAsync(listener.LocalEndPoint!);
        Socket server = await listener.AcceptAsync().DefaultTimeout();
        await connecting.DefaultTimeout();
        using SocketConnectionContextFactory factory = new SocketConnectionContextFactory(
            new SocketConnectionFactoryOptions { MaxReadBufferSize = 4 }, NullLogger.Instance);
        ConnectionContext connection = factory.Create(server);
        TestOutputHelper.WriteLine($"Connection implementation: {connection.GetType().Name}");
        Assert.Equal(System.Threading.IoUring.IsSupported, connection is IoUringPairedConnection);
        Assert.Same(server, connection.Features.Get<IConnectionSocketFeature>()!.Socket);
        CancellationToken closed = connection.ConnectionClosed;
        try
        {
            if (paused)
            {
                await client.SendAsync(new byte[] { 1, 2, 3, 4 }, SocketFlags.None);
                ReadResult result = await connection.Transport.Input.ReadAsync().AsTask().DefaultTimeout();
                connection.Transport.Input.AdvanceTo(result.Buffer.Start, result.Buffer.Start);
            }

            ConnectionAbortedException failure = new ConnectionAbortedException("Test abort");
            connection.Abort(failure);
            await connection.DisposeAsync().AsTask().DefaultTimeout();
            Assert.True(closed.IsCancellationRequested);
            Assert.True(server.SafeHandle.IsClosed);
            await connection.DisposeAsync().AsTask().DefaultTimeout();
        }
        finally
        {
            await connection.DisposeAsync().AsTask().DefaultTimeout();
        }
    }

    [Fact]
    public async Task MultishotTransportAsyncSenderPreservesLargePayload()
    {
        using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Task connecting = client.ConnectAsync(listener.LocalEndPoint!);
        using Socket server = await listener.AcceptAsync().DefaultTimeout();
        await connecting.DefaultTimeout();
        byte[] payload = new byte[1025 * 4096 + 17];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index % 251);
        }

        Task receiving = Task.Run(() =>
        {
            byte[] buffer = new byte[65536];
            int received = 0;
            int count;
            while ((count = server.Receive(buffer)) != 0)
            {
                Assert.True(received + count <= payload.Length,
                    $"Ordinary receive got too many bytes: previous={received}, additional={count}, expected={payload.Length}");
                Assert.Equal(payload.AsSpan(received, count).ToArray(), buffer.AsSpan(0, count).ToArray());
                received += count;
            }

            Assert.Equal(payload.Length, received);
        });
        try
        {
            int sent = 0;
            while (sent != payload.Length)
            {
                sent += await client.SendAsync(payload.AsMemory(sent), SocketFlags.None);
            }

            client.Shutdown(SocketShutdown.Send);
            await receiving.DefaultTimeout();
        }
        finally
        {
            server.Dispose();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MultishotConnectionSendsSingleAndMultipleBuffers(bool abort, bool multipleBuffers)
    {
        if (!IoUringMultishotConnection.IsSupported)
        {
            return;
        }

        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Task connecting = client.ConnectAsync(listener.LocalEndPoint!);
        using Socket server = await listener.AcceptAsync().DefaultTimeout();
        await connecting.DefaultTimeout();
        using SocketConnectionContextFactory factory = new(
            new SocketConnectionFactoryOptions { MaxWriteBufferSize = 2 }, NullLogger.Instance);
        await using ConnectionContext connection = factory.Create(server);
        for (int iteration = 0; iteration < 5; iteration++)
        {
            bool scatterGather = multipleBuffers && iteration is 2 or 4;
            byte[] payload = new byte[scatterGather ? 8192 : 128];
            Array.Fill(payload, (byte)iteration);
            int written = 0;
            while (written < payload.Length)
            {
                Memory<byte> memory = connection.Transport.Output.GetMemory(1);
                int length = Math.Min(memory.Length, payload.Length - written);
                payload.AsMemory(written, length).CopyTo(memory);
                connection.Transport.Output.Advance(length);
                written += length;
            }

            await connection.Transport.Output.FlushAsync().AsTask().DefaultTimeout();
            byte[] received = new byte[payload.Length];
            int count = 0;
            while (count < received.Length)
            {
                int read = await client.ReceiveAsync(received.AsMemory(count), SocketFlags.None).DefaultTimeout();
                Assert.True(read > 0);
                count += read;
            }

            Assert.Equal(payload, received);
        }

        if (abort)
        {
            connection.Abort(new ConnectionAbortedException("Test sender cleanup"));
        }

        await connection.DisposeAsync().AsTask().DefaultTimeout();
        Assert.True(server.SafeHandle.IsClosed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MultishotConnectionDrainsOutputBeforeAdvancing(bool abort, bool completeOutput)
    {
        if (!IoUringMultishotConnection.IsSupported)
        {
            return;
        }

        const int PayloadLength = 2 * 1024 * 1024;
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.ReceiveBufferSize = 4096;
        Task connecting = client.ConnectAsync(listener.LocalEndPoint!);
        using Socket server = await listener.AcceptAsync().DefaultTimeout();
        await connecting.DefaultTimeout();
        server.SendBufferSize = 4096;
        using CountingPool pool = new();
        await using IoUringMultishotConnection connection = new(server, pool, NullLogger.Instance,
            new PipeOptions(useSynchronizationContext: false),
            new PipeOptions(pool, pauseWriterThreshold: PayloadLength, resumeWriterThreshold: PayloadLength / 2,
                useSynchronizationContext: false));
        byte[] payload = new byte[PayloadLength];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index % 251);
        }

        PipeWriter output = connection.Transport.Output;
        payload.CopyTo(output.GetMemory(payload.Length));
        output.Advance(payload.Length);
        Task<FlushResult> flush = output.FlushAsync().AsTask();
        if (completeOutput)
        {
            output.Complete();
        }
        connection.Start();

        byte[] received = new byte[payload.Length];
        int prefixLength = payload.Length * 3 / 4;
        try
        {
            // Blocking receives keep the client independent of the experimental async receive path.
            await Task.Factory.StartNew(() => Receive(0, prefixLength), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).DefaultTimeout();

            FieldInfo pipeField = typeof(IoUringMultishotConnection).GetField("_sendPipe", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Pipe pipe = Assert.IsType<Pipe>(pipeField.GetValue(connection));
            PropertyInfo lengthProperty = typeof(Pipe).GetProperty("Length", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal((long)payload.Length, Assert.IsType<long>(lengthProperty.GetValue(pipe)));
            if (!completeOutput)
            {
                Assert.False(flush.IsCompleted);
            }

            if (abort)
            {
                connection.Abort(new ConnectionAbortedException("Test output abort"));
            }
            else
            {
                await Task.Factory.StartNew(() => Receive(prefixLength, payload.Length), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default).DefaultTimeout();
                Assert.Equal(payload, received);
            }

            await flush.DefaultTimeout();
        }
        finally
        {
            connection.Abort(new ConnectionAbortedException("Test cleanup"));
            client.Dispose();
            await connection.DisposeAsync().AsTask().DefaultTimeout();
        }

        void Receive(int offset, int end)
        {
            while (offset < end)
            {
                int count = client.Receive(received.AsSpan(offset, end - offset));
                Assert.True(count > 0);
                offset += count;
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void MultishotFactoryDoesNotCreateSenderPools(int ioQueueCount)
    {
        using SocketConnectionContextFactory factory = new(
            new SocketConnectionFactoryOptions { IOQueueCount = ioQueueCount }, NullLogger.Instance);
        FieldInfo settingsField = typeof(SocketConnectionContextFactory).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Array settings = Assert.IsAssignableFrom<Array>(settingsField.GetValue(factory));
        Assert.Equal(Math.Max(1, ioQueueCount), settings.Length);
        foreach (object setting in settings)
        {
            object? pool = setting.GetType().GetProperty("SocketSenderPool")!.GetValue(setting);
            if (IoUringMultishotConnection.IsSupported)
            {
                Assert.Null(pool);
            }
            else
            {
                Assert.IsType<SocketSenderPool>(pool);
            }
        }
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    public async Task MultishotConnectionsSendAndDisposeWithOutputBackpressure(bool abort, bool writeOnWorker, bool large)
    {
        using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(4);
        using SocketConnectionContextFactory factory = new SocketConnectionContextFactory(
            new SocketConnectionFactoryOptions { IOQueueCount = 1, MaxWriteBufferSize = 1024 }, NullLogger.Instance);
        List<(Socket Client, ConnectionContext Connection, Task<FlushResult> Flush)> connections = new();
        List<Socket> sockets = new();
        List<ConnectionContext> ownedConnections = new();
        byte[] payload = new byte[large ? 128 * 4096 + 17 : 17];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index % 251);
        }

        try
        {
            for (int index = 0; index < 4; index++)
            {
                Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sockets.Add(client);
                client.ReceiveBufferSize = 4096;
                Task connecting = client.ConnectAsync(listener.LocalEndPoint!);
                Socket server = await listener.AcceptAsync().DefaultTimeout();
                sockets.Add(server);
                await connecting.DefaultTimeout();
                server.SendBufferSize = 4096;
                ConnectionContext connection = factory.Create(server);
                ownedConnections.Add(connection);
                Task<FlushResult> flush = await Task.Factory.StartNew(() =>
                {
                    Assert.Equal(writeOnWorker, Thread.CurrentThread.IsThreadPoolThread);
                    return connection.Transport.Output.WriteAsync(payload).AsTask();
                }, CancellationToken.None, writeOnWorker ? TaskCreationOptions.None : TaskCreationOptions.LongRunning, TaskScheduler.Default);
                connections.Add((client, connection, flush));
                if (large)
                {
                    Assert.False(flush.IsCompleted);
                }
            }

            await Task.WhenAll(connections.Select(async item =>
            {
                if (abort)
                {
                    item.Connection.Abort(new ConnectionAbortedException("Test output abort"));
                    await item.Connection.DisposeAsync().AsTask().DefaultTimeout();
                    Assert.True((await item.Flush.DefaultTimeout()).IsCompleted);
                    return;
                }

                byte[] buffer = new byte[65536];
                int received = 0;
                while (received < payload.Length)
                {
                    int count = await item.Client.ReceiveAsync(buffer, SocketFlags.None).DefaultTimeout();
                    Assert.True(count > 0);
                    Assert.Equal(payload.AsSpan(received, count).ToArray(), buffer.AsSpan(0, count).ToArray());
                    received += count;
                }

                Assert.False((await item.Flush.DefaultTimeout()).IsCanceled);
                await item.Connection.DisposeAsync().AsTask().DefaultTimeout();
            })).DefaultTimeout();
        }
        finally
        {
            foreach (Socket socket in sockets)
            {
                socket.Dispose();
            }
            foreach (ConnectionContext connection in ownedConnections)
            {
                await connection.DisposeAsync().AsTask().DefaultTimeout();
            }
        }
    }

    [Fact]
    public async Task MultishotOutputDoesNotFlowProducerExecutionContext()
    {
        using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Task connecting = client.ConnectAsync(listener.LocalEndPoint!);
        Socket server = await listener.AcceptAsync().DefaultTimeout();
        await connecting.DefaultTimeout();
        using SocketConnectionContextFactory factory = new SocketConnectionContextFactory(
            new SocketConnectionFactoryOptions { IOQueueCount = 1 }, NullLogger.Instance);
        await using ConnectionContext connection = factory.Create(server);
        int workerTransitions = 0;
        AsyncLocal<bool> producerContext = new AsyncLocal<bool>(change =>
        {
            if (change.ThreadContextChanged && change.CurrentValue && Thread.CurrentThread.IsThreadPoolThread)
            {
                Interlocked.Increment(ref workerTransitions);
            }
        });

        Task<FlushResult> flush = await Task.Factory.StartNew(() =>
        {
            Assert.False(Thread.CurrentThread.IsThreadPoolThread);
            producerContext.Value = true;
            try
            {
                return connection.Transport.Output.WriteAsync(new byte[] { 42 }).AsTask();
            }
            finally
            {
                producerContext.Value = false;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        byte[] received = new byte[1];
        Assert.Equal(1, await client.ReceiveAsync(received, SocketFlags.None).DefaultTimeout());
        Assert.Equal(42, received[0]);
        await flush.DefaultTimeout();
        Assert.Equal(0, Volatile.Read(ref workerTransitions));
    }

    [Fact]
    public async Task MultishotConnectionMapsPeerResetToPendingRead()
    {
        using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Task connecting = client.ConnectAsync(listener.LocalEndPoint!);
        Socket server = await listener.AcceptAsync().DefaultTimeout();
        await connecting.DefaultTimeout();
        using SocketConnectionContextFactory factory = new SocketConnectionContextFactory(
            new SocketConnectionFactoryOptions(), NullLogger.Instance);
        await using ConnectionContext connection = factory.Create(server);
        Task<ReadResult> pending = connection.Transport.Input.ReadAsync().AsTask();
        client.Close(0);
        await Assert.ThrowsAsync<ConnectionResetException>(() => pending.DefaultTimeout());
    }

    [Fact]
    public async Task AcceptCancellationDoesNotStopListener()
    {
        await using SocketConnectionListener listener = new SocketConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0), new SocketTransportOptions(), NullLoggerFactory.Instance);
        listener.Bind();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        Task<ConnectionContext?> canceled = listener.AcceptAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.DefaultTimeout());
        Assert.Equal(cancellation.Token, error.CancellationToken);

        using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.EndPoint).DefaultTimeout();
        await using ConnectionContext? accepted = await listener.AcceptAsync().AsTask().DefaultTimeout();
        Assert.NotNull(accepted);
        Assert.True(accepted.Features.Get<IConnectionSocketFeature>()!.Socket.NoDelay);
        Task<ConnectionContext?> pending = listener.AcceptAsync().AsTask();
        await listener.UnbindAsync();
        Assert.Null(await pending.DefaultTimeout());
    }

    [Fact]
    public async Task MultishotReaderRejectsOverlappingReadsAndInvalidAdvances()
    {
        ReceiveSource source = new ReceiveSource();
        IoUringMultishotPipeReader reader = new(source.ReadAsync, PipeOptions.Default);
        reader.Start();
        try
        {
            Task<ReadResult> pending = reader.ReadAsync().AsTask();
            Assert.Throws<InvalidOperationException>(() => reader.ReadAsync());
            source.Write(new TestOwner([1, 2, 3]));
            ReadResult result = await pending.DefaultTimeout();
            Assert.Throws<InvalidOperationException>(() => reader.TryRead(out _));
            Assert.Throws<InvalidOperationException>(() => reader.AdvanceTo(result.Buffer.End, result.Buffer.Start));
            reader.AdvanceTo(result.Buffer.Start, result.Buffer.GetPosition(1));
            result = await reader.ReadAsync().AsTask().DefaultTimeout();
            reader.AdvanceTo(result.Buffer.Start, result.Buffer.Start);
            result = await reader.ReadAsync().AsTask().DefaultTimeout();
            Assert.Equal(new byte[] { 1, 2, 3 }, result.Buffer.ToArray());
            reader.AdvanceTo(result.Buffer.End);

            reader.CancelPendingRead();
            result = await reader.ReadAsync().AsTask().DefaultTimeout();
            Assert.True(result.IsCanceled);
            reader.AdvanceTo(result.Buffer.End);

            using CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => reader.ReadAsync(cancellation.Token).AsTask());
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally
        {
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    [Theory]
    [InlineData(3, int.MaxValue)]
    [InlineData(4096, 4096)]
    [InlineData(16384, 4096)]
    public async Task MultishotReaderHonorsInputPoolAndScheduler(int bufferSize, int maxBufferSize)
    {
        ReceiveSource source = new ReceiveSource();
        using CountingPool pool = new CountingPool(maxBufferSize);
        CountingScheduler scheduler = new CountingScheduler();
        IoUringMultishotPipeReader reader = new(source.ReadAsync,
            new PipeOptions(pool, scheduler, useSynchronizationContext: false));
        reader.Start();
        try
        {
            Task<ReadResult> pending = reader.ReadAsync().AsTask();
            byte[] expected = new byte[bufferSize];
            new Random(42).NextBytes(expected);
            TestOwner owner = new TestOwner((byte[])expected.Clone());
            source.Write(owner);
            ReadResult result = await pending.DefaultTimeout();
            Assert.Equal(1, scheduler.Scheduled);
            Assert.Equal(0, pool.Rented);
            Assert.False(owner.Disposed);
            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            Assert.Equal(bufferSize <= maxBufferSize ? 1 : 0, pool.Rented);
            Assert.True(owner.Disposed);
            source.Complete();
            result = await reader.ReadAsync().AsTask().DefaultTimeout();
            Assert.Equal(expected, result.Buffer.ToArray());
            reader.AdvanceTo(result.Buffer.End);
        }
        finally
        {
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    [Fact]
    public async Task MultishotReaderAbortDoesNotInvalidateActiveRead()
    {
        ReceiveSource source = new ReceiveSource();
        IoUringMultishotPipeReader reader = new(source.ReadAsync, PipeOptions.Default);
        reader.Start();
        TestOwner owner = new TestOwner([1, 2, 3]);
        source.Write(owner);
        ReadResult result = await reader.ReadAsync().AsTask().DefaultTimeout();
        reader.Abort(new IOException("Aborted"));
        await reader.Closed.DefaultTimeout();
        Assert.False(owner.Disposed);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Buffer.ToArray());
        reader.AdvanceTo(result.Buffer.End);
        Assert.True(owner.Disposed);
        await reader.CompleteAsync().AsTask().DefaultTimeout();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultishotPipeContractAllowsUnexamining(bool explicitExamined)
    {
        ReceiveSource source = new ReceiveSource();
        IoUringMultishotPipeReader reader = new(source.ReadAsync, PipeOptions.Default);
        reader.Start();
        try
        {
            source.Write(new TestOwner([0, 1, 2, 3]));
            ReadResult result = await reader.ReadAsync().AsTask().DefaultTimeout();
            reader.AdvanceTo(result.Buffer.GetPosition(1), result.Buffer.End);
            source.Write(new TestOwner([4]));
            result = await reader.ReadAsync().AsTask().DefaultTimeout();
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.Buffer.ToArray());
            if (explicitExamined)
            {
                reader.AdvanceTo(result.Buffer.Start, result.Buffer.GetPosition(1));
            }
            else
            {
                reader.AdvanceTo(result.Buffer.Start);
            }

            result = await reader.ReadAsync().AsTask().DefaultTimeout();
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.Buffer.ToArray());
            reader.AdvanceTo(result.Buffer.End);
        }
        finally
        {
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultishotPipeContractCanceledReadCanBeRetriedWithoutAdvancing(bool buffered)
    {
        ReceiveSource source = new ReceiveSource();
        if (buffered)
        {
            source.Write(new TestOwner([1, 2, 3]));
        }

        IoUringMultishotPipeReader reader = new(source.ReadAsync, PipeOptions.Default);
        reader.Start();
        try
        {
            reader.CancelPendingRead();
            ReadResult canceled = await reader.ReadAsync().AsTask().DefaultTimeout();
            Assert.True(canceled.IsCanceled);
            Task<ReadResult> pending = reader.ReadAsync().AsTask();
            if (!buffered)
            {
                source.Write(new TestOwner([1, 2, 3]));
            }

            ReadResult result = await pending.DefaultTimeout();
            Assert.False(result.IsCanceled);
            Assert.Equal(new byte[] { 1, 2, 3 }, result.Buffer.ToArray());
            reader.AdvanceTo(result.Buffer.End);
        }
        finally
        {
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    [Fact]
    public async Task MultishotPipeContractPrematureGetResultDoesNotRetirePendingRead()
    {
        ReceiveSource source = new ReceiveSource();
        IoUringMultishotPipeReader reader = new(source.ReadAsync, PipeOptions.Default);
        reader.Start();
        ValueTask<ReadResult> pending = reader.ReadAsync();
        try
        {
            Assert.Throws<InvalidOperationException>(() => pending.GetAwaiter().GetResult());
            Assert.Throws<InvalidOperationException>(() => reader.ReadAsync());
            source.Write(new TestOwner([42]));
            ReadResult result = await pending.AsTask().DefaultTimeout();
            Assert.Equal(new byte[] { 42 }, result.Buffer.ToArray());
            reader.AdvanceTo(result.Buffer.End);
        }
        finally
        {
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    [Fact]
    public async Task MultishotPipeContractCustomSchedulerPreservesExecutionContext()
    {
        ReceiveSource source = new ReceiveSource();
        IoUringMultishotPipeReader reader = new(source.ReadAsync,
            new PipeOptions(readerScheduler: new UnsafeScheduler(), useSynchronizationContext: false));
        reader.Start();
        try
        {
            AsyncLocal<int> local = new AsyncLocal<int> { Value = 42 };
            TaskCompletionSource<int> completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            ValueTaskAwaiter<ReadResult> awaiter = reader.ReadAsync().GetAwaiter();
            awaiter.OnCompleted(() =>
            {
                ReadResult result = awaiter.GetResult();
                reader.AdvanceTo(result.Buffer.End);
                completed.SetResult(local.Value);
            });
            local.Value = 84;
            source.Write(new TestOwner([42]));
            Assert.Equal(42, await completed.Task.DefaultTimeout());
        }
        finally
        {
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultishotReaderStaleResultDoesNotRetireNextRead(bool useToken)
    {
        ReceiveSource source = new ReceiveSource();
        IoUringMultishotPipeReader reader = new(source.ReadAsync,
            new PipeOptions(readerScheduler: PipeScheduler.Inline, useSynchronizationContext: false));
        using CancellationTokenSource cancellation = new();
        reader.Start();
        try
        {
            ValueTask<ReadResult> first = reader.ReadAsync(useToken ? cancellation.Token : default);
            if (useToken)
            {
                cancellation.Cancel();
                OperationCanceledException error = Assert.ThrowsAny<OperationCanceledException>(
                    () => first.GetAwaiter().GetResult());
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }
            else
            {
                reader.CancelPendingRead();
                ReadResult canceled = await first;
                Assert.True(canceled.IsCanceled);
                reader.AdvanceTo(canceled.Buffer.End);
            }

            ValueTask<ReadResult> second = reader.ReadAsync();
            Assert.Throws<InvalidOperationException>(() => first.GetAwaiter().GetResult());
            Assert.Throws<InvalidOperationException>(() => reader.TryRead(out _));

            source.Write(new TestOwner([42]));
            ReadResult result = await second.AsTask().DefaultTimeout();
            Assert.Equal(new byte[] { 42 }, result.Buffer.ToArray());
            reader.AdvanceTo(result.Buffer.End);
        }
        finally
        {
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultishotReaderReceiveRacingCancellationPreservesData(bool useToken)
    {
        for (int iteration = 0; iteration < 32; iteration++)
        {
            ReceiveSource source = new ReceiveSource();
            IoUringMultishotPipeReader reader = new(source.ReadAsync,
                new PipeOptions(useSynchronizationContext: false));
            using CancellationTokenSource cancellation = new();
            TestOwner owner = new([1, 2, 3, 4]);
            reader.Start();
            try
            {
                ValueTask<ReadResult> pending = reader.ReadAsync(useToken ? cancellation.Token : default);
                await Task.WhenAll(Task.Run(() => source.Write(owner)), Task.Run(() =>
                {
                    if (useToken)
                    {
                        cancellation.Cancel();
                    }
                    else
                    {
                        reader.CancelPendingRead();
                    }
                })).DefaultTimeout();
                source.Complete();

                ReadResult result;
                try
                {
                    result = await pending.AsTask().DefaultTimeout();
                }
                catch (OperationCanceledException error) when (useToken)
                {
                    Assert.Equal(cancellation.Token, error.CancellationToken);
                    result = await reader.ReadAsync().AsTask().DefaultTimeout();
                }

                List<byte> received = new();
                while (true)
                {
                    received.AddRange(result.Buffer.ToArray());
                    reader.AdvanceTo(result.Buffer.End);
                    if (result.IsCompleted)
                    {
                        break;
                    }

                    result = await reader.ReadAsync().AsTask().DefaultTimeout();
                }

                Assert.Equal(new byte[] { 1, 2, 3, 4 }, received);
                Assert.True(owner.Disposed);
            }
            finally
            {
                await reader.CompleteAsync().AsTask().DefaultTimeout();
            }
        }
    }

    [Fact]
    public async Task MultishotPipeContractUsesCapturedSynchronizationContext()
    {
        ReceiveSource source = new ReceiveSource();
        IoUringMultishotPipeReader reader = new(source.ReadAsync, PipeOptions.Default);
        reader.Start();
        QueuedSynchronizationContext context = new QueuedSynchronizationContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        TaskCompletionSource<SynchronizationContext?> completed =
            new TaskCompletionSource<SynchronizationContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            ValueTaskAwaiter<ReadResult> awaiter = reader.ReadAsync().GetAwaiter();
            awaiter.OnCompleted(() =>
            {
                ReadResult result = awaiter.GetResult();
                reader.AdvanceTo(result.Buffer.End);
                completed.SetResult(SynchronizationContext.Current);
            });
            SynchronizationContext.SetSynchronizationContext(previous);
            source.Write(new TestOwner([42]));
            (SendOrPostCallback callback, object? state) = await context.Posted.Reader.ReadAsync().AsTask().DefaultTimeout();
            SynchronizationContext.SetSynchronizationContext(context);
            callback(state);
            SynchronizationContext.SetSynchronizationContext(previous);
            Assert.Same(context, await completed.Task.DefaultTimeout());
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    [Fact]
    public async Task MultishotPipeContractReadAtLeastCrossesPauseThreshold()
    {
        ReceiveSource source = new ReceiveSource();
        IoUringMultishotPipeReader reader = new(source.ReadAsync,
            new PipeOptions(pauseWriterThreshold: 4, resumeWriterThreshold: 2));
        reader.Start();
        try
        {
            source.Write(new TestOwner([1, 2, 3, 4]));
            source.Write(new TestOwner([5, 6, 7, 8]));
            source.Write(new TestOwner([9, 10, 11, 12]));
            ReadResult result = await reader.ReadAtLeastAsync(12).AsTask().DefaultTimeout();
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, result.Buffer.ToArray());
            reader.AdvanceTo(result.Buffer.End);
        }
        finally
        {
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    [Fact]
    public async Task MultishotPipeContractInlineCancellationCanReenterReader()
    {
        ReceiveSource source = new ReceiveSource();
        IoUringMultishotPipeReader reader = new(source.ReadAsync,
            new PipeOptions(readerScheduler: PipeScheduler.Inline, useSynchronizationContext: false));
        reader.Start();
        try
        {
            int thread = Environment.CurrentManagedThreadId;
            bool called = false;
            ValueTaskAwaiter<ReadResult> awaiter = reader.ReadAsync().GetAwaiter();
            awaiter.OnCompleted(() =>
            {
                Assert.Equal(thread, Environment.CurrentManagedThreadId);
                ReadResult result = awaiter.GetResult();
                Assert.True(result.IsCanceled);
                Assert.False(reader.TryRead(out _));
                called = true;
            });
            reader.CancelPendingRead();
            Assert.True(called);
        }
        finally
        {
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    [Fact]
    public async Task MultishotPipeContractBackpressureResumesStrictlyBelowThreshold()
    {
        ReceiveSource source = new ReceiveSource();
        TaskCompletionSource paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CountingScheduler writerScheduler = new CountingScheduler();
        IoUringMultishotPipeReader reader = new(source.ReadAsync,
            new PipeOptions(writerScheduler: writerScheduler, pauseWriterThreshold: 4, resumeWriterThreshold: 2),
            onPause: value => (value ? paused : resumed).TrySetResult());
        reader.Start();
        try
        {
            source.Write(new TestOwner([1, 2, 3, 4]));
            await paused.Task.DefaultTimeout();
            ReadResult result = await reader.ReadAsync().AsTask().DefaultTimeout();
            reader.AdvanceTo(result.Buffer.Start, result.Buffer.GetPosition(2));
            Assert.False(resumed.Task.IsCompleted);
            result = await reader.ReadAsync().AsTask().DefaultTimeout();
            reader.AdvanceTo(result.Buffer.Start, result.Buffer.GetPosition(3));
            await resumed.Task.DefaultTimeout();
            Assert.Equal(1, writerScheduler.Scheduled);
            result = await reader.ReadAsync().AsTask().DefaultTimeout();
            reader.AdvanceTo(result.Buffer.End);
        }
        finally
        {
            await reader.CompleteAsync().AsTask().DefaultTimeout();
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        public Channel<(SendOrPostCallback, object?)> Posted { get; } =
            Channel.CreateUnbounded<(SendOrPostCallback, object?)>();

        public override void Post(SendOrPostCallback callback, object? state) =>
            Assert.True(Posted.Writer.TryWrite((callback, state)));
    }

    private sealed class UnsafeScheduler : PipeScheduler
    {
        public override void Schedule(Action<object?> action, object? state) =>
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(static item => item.Action(item.State),
                (Action: action, State: state), preferLocal: false);
    }

    private sealed class CountingScheduler : PipeScheduler
    {
        public int Scheduled { get; private set; }

        public override void Schedule(Action<object?> action, object? state)
        {
            Scheduled++;
            action(state);
        }
    }

    private sealed class CountingPool(int maxBufferSize = int.MaxValue) : MemoryPool<byte>
    {
        public int Rented { get; private set; }
        public override int MaxBufferSize => maxBufferSize;

        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(minBufferSize, MaxBufferSize);
            Rented++;
            return new TestOwner(new byte[Math.Max(1, minBufferSize)]);
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

    private sealed class TestOwner(byte[] bytes) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => bytes;
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Assert.False(Disposed);
            Disposed = true;
            Array.Fill(bytes, (byte)0xff);
        }
    }

    private sealed class ReceiveSource
    {
        private readonly Channel<IMemoryOwner<byte>> _channel = Channel.CreateUnbounded<IMemoryOwner<byte>>();
        public TaskCompletionSource Stopped { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Yielded { get; private set; }

        public void Write(IMemoryOwner<byte> owner) => Assert.True(_channel.Writer.TryWrite(owner));
        public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);

        public async IAsyncEnumerable<IMemoryOwner<byte>> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await foreach (IMemoryOwner<byte> owner in _channel.Reader.ReadAllAsync(cancellationToken))
                {
                    Yielded++;
                    yield return owner;
                }
            }
            finally
            {
                while (_channel.Reader.TryRead(out IMemoryOwner<byte>? owner))
                {
                    owner.Dispose();
                }

                Stopped.TrySetResult();
            }
        }
    }

#nullable restore

    [Fact]
    public async Task SocketTransportExposesSocketsFeature()
    {
        var builder = TransportSelector.GetHostBuilder()
            .ConfigureWebHost(webHostBuilder =>
            {
                webHostBuilder
                    .UseKestrel()
                    .UseUrls("http://127.0.0.1:0")
                    .Configure(app =>
                    {
                        app.Run(context =>
                        {
                            var socket = context.Features.Get<IConnectionSocketFeature>().Socket;
                            Assert.NotNull(socket);
                            Assert.Equal(ProtocolType.Tcp, socket.ProtocolType);
                            var ip = (IPEndPoint)socket.RemoteEndPoint;
                            Assert.Equal(ip.Address, context.Connection.RemoteIpAddress);
                            Assert.Equal(ip.Port, context.Connection.RemotePort);

                            return Task.CompletedTask;
                        });
                    });
            })
            .ConfigureServices(AddTestLogging);

        using var host = builder.Build();
        using var client = new HttpClient();

        await host.StartAsync();

        var response = await client.GetAsync($"http://127.0.0.1:{host.GetPort()}/");
        response.EnsureSuccessStatusCode();

        await host.StopAsync();
    }
}
