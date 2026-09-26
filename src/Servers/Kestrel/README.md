# Kestrel

Kestrel is our cross-platform web server that is included and enabled by default in ASP.NET Core.

Documentation for ASP.NET Core Kestrel can be found in the [ASP.NET Core Kestrel Docs](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel).

## Description

The following contains a description of the sub-directories.

- [Core/](Core/): Contains the main server implementation for Kestrel.
- [Kestrel/](Kestrel/): Contains the public API exposed to use Kestrel.
- [test/](test/): Contains End to End tests for Kestrel.
- [Transport.Sockets/](Transport.Sockets/):Contains the Sockets transport for connection management.
- [Transport.Quic/](Transport.Quic/): Contains the QUIC transport for connection management.

## Development Setup

### Build

To build this specific project from source, follow the instructions [on building the project](../../../docs/BuildFromSource.md#step-3-build-the-repo).

Or for the less detailed explanation, run the following command inside this directory.
```powershell
> ./build.cmd
```

### Test

To run the tests for this project, you can [run the tests on the command line](https://github.com/dotnet/aspnetcore/blob/main/docs/BuildFromSource.md#running-tests-on-command-line) in this directory.

Or for the less detailed explanation, run the following command inside this directory.
```powershell
> ./build.cmd -t
```

You can also run project specific tests by running `dotnet test` in the `tests` directory next to the `src` directory of the project.

## More Information

### Experimental io_uring socket transport

This branch's selected io_uring transport requires the companion runtime's
experimental `IoUringOperation` and paired `IoUring.TrySubmit` APIs.
Connections use ordinary `Socket.AcceptAsync`.
`DOTNET_USE_IO_URING=1` is the only enable switch; the transport selects the
ordinary socket implementation when `System.Threading.IoUring.IsSupported` is false.
The new transport is deliberately a sequential JSON request/response experiment,
not a general-purpose Kestrel transport. Each connection pins separate 16 KiB
input and output arrays for its lifetime. Requests and responses must fit those
buffers. Retained fragmented input is compacted within the input allocation.
There is no alternate output pool, SocketSender, or Socket send/receive fallback.

A response flush and the next receive are published as one pair to the same
issuer queue once the previous input is retired. Initial and fragmented reads
can be receive-only; final output can be send-only. Positive partial sends retry
on the issuer. Successful sends need no worker continuation. Normal receive
continuations run on ThreadPool workers, not issuers.

Pipelining, overlapping writes, and streaming/multiple flushes of one response
are unsupported and rejected. Fixed-length JSON responses are the target.
If a receive finishes before its preceding send, its result remains private
until the send finishes. The send then dispatches the deferred read continuation;
there is still only one normal worker callback for the pair. Pairing does not
guarantee ordered completion or one kernel enter under submission-queue pressure.
Cancellation targets live operations and disposal waits for kernel ownership
before unpinning. These restrictions do not apply when io_uring is disabled.

Unlike the preceding transport's optimistic synchronous sends, paired sends
execute kernel send work on the ring issuers. An issuer count tuned for receives
alone can therefore limit throughput while other CPUs remain idle. Tune
`DOTNET_IORING_THREAD_COUNT` for the paired workload rather than carrying over the
multishot setting. Worker-minimum changes are separate experiments, not required
to enable this transport.

The preceding multishot connection and reader remain in source for compatibility
tests, but the connection factory selects the paired implementation.

For more information, see the [ASP.NET Core README](../../../README.md).
