using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;

namespace Microsoft.AspNetCore.Server.Kestrel.Performance
{
    public class TechEmpowerHttpParserBenchmark
    {
        private HttpParser<ParsingAdapter> Parser { get; } = new HttpParser<ParsingAdapter>();

        private RequestType _requestType;
        private State _state;

        [Benchmark]
        public void PlaintextTechEmpower() => Parse(RequestParsingData.PlaintextTechEmpowerPipelinedRequests);

        [Benchmark]
        public void JsonTechEmpower() => Parse(RequestParsingData.JsonTechEmpowerRequest);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Parse(byte[] request)
        {
            var sequence = new ReadOnlySequence<byte>(request);
            var readResult = new ReadResult(sequence, isCanceled: false, isCompleted: false);

            HandleRequest(in readResult);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool HandleRequest(in ReadResult result)
        {
            var buffer = result.Buffer;
            // var writer = GetWriter(Writer);

            while (true)
            {
                if (!ParseHttpRequest(ref buffer, result.IsCompleted, out var examined))
                {
                    return false;
                }

                if (_state == State.Body)
                {
                    // ProcessRequest(ref writer);

                    _state = State.StartLine;

                    if (!buffer.IsEmpty)
                    {
                        // More input data to parse
                        continue;
                    }
                }

                // No more input or incomplete data, Advance the Reader
                // Reader.AdvanceTo(buffer.Start, examined);
                break;
            }

            // writer.Commit();
            return true;
        }

        private bool ParseHttpRequest(ref ReadOnlySequence<byte> buffer, bool isCompleted, out SequencePosition examined)
        {
            examined = buffer.End;

            var consumed = buffer.Start;
            var state = _state;

            if (!buffer.IsEmpty)
            {
                if (state == State.StartLine)
                {
                    if (Parser.ParseRequestLine(new ParsingAdapter(this), buffer, out consumed, out examined))
                    {
                        state = State.Headers;
                    }

                    buffer = buffer.Slice(consumed);
                }

                if (state == State.Headers)
                {
                    var reader = new SequenceReader<byte>(buffer);
                    var success = Parser.ParseHeaders(new ParsingAdapter(this), ref reader);

                    consumed = reader.Position;
                    if (success)
                    {
                        examined = consumed;
                        state = State.Body;
                    }
                    else
                    {
                        examined = buffer.End;
                    }

                    buffer = buffer.Slice(consumed);
                }

                if (state != State.Body && isCompleted)
                {
                    ThrowUnexpectedEndOfData();
                }
            }
            else if (isCompleted)
            {
                return false;
            }

            _state = state;
            return true;
        }

        public void OnStartLine(HttpMethod method, HttpVersion version, Span<byte> target, Span<byte> path, Span<byte> query, Span<byte> customMethod, bool pathEncoded)
        {
            var requestType = RequestType.NotRecognized;
            if (method == HttpMethod.Get)
            {
                if (Paths.Plaintext.Length <= path.Length && path.StartsWith(Paths.Plaintext))
                {
                    requestType = RequestType.PlainText;
                }
                else if (Paths.Json.Length <= path.Length && path.StartsWith(Paths.Json))
                {
                    requestType = RequestType.Json;
                }
            }

            _requestType = requestType;
        }

        private static void ThrowUnexpectedEndOfData()
        {
            throw new InvalidOperationException("Unexpected end of data!");
        }

        private enum State
        {
            StartLine,
            Headers,
            Body
        }

        private enum RequestType
        {
            NotRecognized,
            PlainText,
            Json
        }

#if NETCOREAPP5_0
        public void OnStaticIndexedHeader(int index) { }
        public void OnStaticIndexedHeader(int index, ReadOnlySpan<byte> value) { }
        public void OnHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value) { }
        public void OnHeadersComplete(bool endStream) { }
#else
        public void OnHeader(Span<byte> name, Span<byte> value) { }
        public void OnHeadersComplete() { }
#endif

        private struct ParsingAdapter : IHttpRequestLineHandler, IHttpHeadersHandler
        {
            public TechEmpowerHttpParserBenchmark RequestHandler;

            public ParsingAdapter(TechEmpowerHttpParserBenchmark requestHandler)
                => RequestHandler = requestHandler;

#if NETCOREAPP5_0
            public void OnStaticIndexedHeader(int index)
                => RequestHandler.OnStaticIndexedHeader(index);

            public void OnStaticIndexedHeader(int index, ReadOnlySpan<byte> value)
                => RequestHandler.OnStaticIndexedHeader(index, value);

            public void OnHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
                => RequestHandler.OnHeader(name, value);

            public void OnHeadersComplete(bool endStream)
                => RequestHandler.OnHeadersComplete(endStream);
#else
            public void OnHeader(Span<byte> name, Span<byte> value)
                => RequestHandler.OnHeader(name, value);

            public void OnHeadersComplete()
                => RequestHandler.OnHeadersComplete();
#endif

            public void OnStartLine(HttpMethod method, HttpVersion version, Span<byte> target, Span<byte> path, Span<byte> query, Span<byte> customMethod, bool pathEncoded)
                => RequestHandler.OnStartLine(method, version, target, path, query, customMethod, pathEncoded);
        }

        public static class Paths
        {
            public readonly static byte[] Plaintext = "/plaintext".Select(c => (byte)c).ToArray();
            public readonly static byte[] Json = "/json".Select(c => (byte)c).ToArray();
        }
    }
}
