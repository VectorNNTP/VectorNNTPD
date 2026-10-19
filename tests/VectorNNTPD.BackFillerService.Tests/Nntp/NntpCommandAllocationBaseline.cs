using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>
    /// Prints allocation counts for a status-line read, one scripted ARTICLE, and an idle lease.
    /// The manual builder line reconstructs the retired per-line writer and linked source. It is not the production path.
    /// Timing assertions are absent. Printed rates use this run's iteration counts and are not a matched throughput comparison.
    /// Allocation counts are the measurement. This is an in-memory reader, one immediate session, and one idle lease, not the full service.
    /// </summary>
    public sealed class NntpCommandAllocationBaseline
    {
        /// <summary>Measures status parsing, line reads, one scripted ARTICLE, and idle acquire/return.</summary>
        [Fact]
        public async Task Measure_current_status_line_article_and_lease()
        {
            Console.WriteLine(
                $"baseline runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} cpus={Environment.ProcessorCount}");
            MeasureStatusParse();
            await MeasureStatusLineAsync();
            await MeasureArticleAsync();
            MeasureArticleDebugText();
            await MeasureLeaseAsync();
        }

        private static void MeasureStatusParse()
        {
            var manualBefore = GC.GetAllocatedBytesForCurrentThread();
            var builder = new System.Buffers.ArrayBufferWriter<byte>(256);
            var sample = "220 0 <id@example.invalid> article follows"u8;
            sample.CopyTo(builder.GetSpan(sample.Length));
            builder.Advance(sample.Length);
            var exact = builder.WrittenSpan.ToArray();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            linked.CancelAfter(TimeSpan.FromSeconds(2));
            var manual = GC.GetAllocatedBytesForCurrentThread() - manualBefore;
            Console.WriteLine($"baseline manual-builder+cts bytes={manual} exact={exact.Length} cts={linked.IsCancellationRequested}");

            var line = "220 0 <id@example.invalid> article follows"u8;
            for (var i = 0; i < 200; i++)
            {
                Assert.True(NntpProtocolIo.TryParseStatus(line, out var code, out var text));
                Assert.Equal(220, code);
                _ = text;
            }

            const int iterations = 4000;
            long allocated = 0;
            var g0 = GC.CollectionCount(0);
            for (var i = 0; i < iterations; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                Assert.True(NntpProtocolIo.TryParseStatus(line, out var code, out var textSpan));
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.Equal(220, code);
                Assert.False(textSpan.IsEmpty);
            }

            Console.WriteLine(
                $"after status-parse n={iterations} alloc/op={allocated / iterations} gen0={GC.CollectionCount(0) - g0}");
        }

        private static async Task MeasureStatusLineAsync()
        {
            var wire = "220 0 <id@example.invalid> article follows\n"u8.ToArray();
            var reader = new NntpStreamReader(new CyclingByteStream(wire), 1024);
            for (var i = 0; i < 50; i++)
            {
                var warmupLength = await reader.ReadLineAsync(16 * 1024, TimeSpan.FromSeconds(2), CancellationToken.None);
                Assert.True(NntpProtocolIo.TryParseStatus(reader.ConsumedLine.Span, out _, out _));
                Assert.Equal(42, warmupLength);
            }

            var syncBefore = GC.GetAllocatedBytesForCurrentThread();
            var syncLength = reader.ReadLineAsync(16 * 1024, TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult();
            var syncDelta = GC.GetAllocatedBytesForCurrentThread() - syncBefore;
            Console.WriteLine($"after sync-readline bytes={syncDelta} line={syncLength} thread={Environment.CurrentManagedThreadId}");

            const int iterations = 100_000;
            long allocated = 0;
            var g0 = GC.CollectionCount(0);
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < iterations; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var length = reader.ReadLineAsync(16 * 1024, TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult();
                Assert.True(NntpProtocolIo.TryParseStatus(reader.ConsumedLine.Span, out var code, out _));
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.Equal(42, length);
                Assert.Equal(220, code);
            }

            var elapsed = Stopwatch.GetElapsedTime(start);
            Console.WriteLine(
                $"after status-line n={iterations} alloc/op={allocated / iterations} lines/s={iterations / elapsed.TotalSeconds:F0} gen0={GC.CollectionCount(0) - g0} capacity={reader.LineBufferCapacity}");
        }

        private static async Task MeasureArticleAsync()
        {
            // The pipe-backed scripted peer completes reads on the thread pool. GetResult keeps this
            // thread blocked until that completion, and a saturated pool misses CommandTimeout or
            // ReceiveTimeout. The session then returns ProviderFailure for a valid ARTICLE. This peer
            // buffers the response inside the command write, before the status read waits.
            var stream = new ImmediateScriptedNntpStream();
            var factory = new ImmediateNntpTransportFactory(stream);
            var session = new NntpProviderSession(
                new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, null, null, 0, 1),
                NntpSessionOptions.Default,
                NullLogger.Instance);
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            try
            {
                using (var warmup = await session.DownloadArticleAsync("<id@example.invalid>", CancellationToken.None))
                {
                    Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, warmup.Kind);
                }

                const int iterations = 2000;
                long allocated = 0;
                var g0 = GC.CollectionCount(0);
                var totalBefore = GC.GetTotalAllocatedBytes(precise: true);
                var start = Stopwatch.GetTimestamp();
                for (var i = 0; i < iterations; i++)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    using var result = session.DownloadArticleAsync("<id@example.invalid>", CancellationToken.None).GetAwaiter().GetResult();
                    allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                    if (result.Kind != ArticleRetrievalKind.ArticleRetrieved)
                    {
                        Assert.Fail(
                            $"iteration={i} kind={result.Kind} status={result.StatusCode?.ToString() ?? "null"} reason={result.Reason} reusable={result.SessionReusable} state={session.State}");
                    }
                }

                var elapsed = Stopwatch.GetElapsedTime(start);
                var total = GC.GetTotalAllocatedBytes(precise: true) - totalBefore;
                Console.WriteLine(
                    $"after article n={iterations} caller-alloc/op={allocated / iterations} process-alloc/op={total / iterations} articles/s={iterations / elapsed.TotalSeconds:F0} gen0={GC.CollectionCount(0) - g0}");
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        /// <summary>
        /// Prints allocations for the ARTICLE debug text when Debug is disabled.
        /// The count is evidence. It is not a pass/fail budget.
        /// </summary>
        private static void MeasureArticleDebugText()
        {
            var messageId = "<id@example.invalid>";
            var disabled = NullLogger.Instance;
            for (var i = 0; i < 1000; i++)
            {
                Assert.Equal("ARTICLE", NntpProviderSession.FormatArticleDebugCommand(disabled, messageId));
            }

            const int iterations = 4000;
            long allocated = 0;
            var g0 = GC.CollectionCount(0);
            for (var i = 0; i < iterations; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var text = NntpProviderSession.FormatArticleDebugCommand(disabled, messageId);
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.Equal("ARTICLE", text);
            }

            Console.WriteLine(
                $"after article-debug-text-disabled n={iterations} alloc/op={allocated / iterations} gen0={GC.CollectionCount(0) - g0}");
        }

        private static async Task MeasureLeaseAsync()
        {
            var factory = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(factory, 1);
            await using var pool = NntpSessionPoolTests.CreatePool(factory, max: 1);
            await using (var warmup = await pool.AcquireAsync(CancellationToken.None))
            {
                Assert.Equal(NntpSessionState.Ready, warmup.Session.State);
            }

            const int iterations = 2000;
            long allocated = 0;
            var g0 = GC.CollectionCount(0);
            var totalBefore = GC.GetTotalAllocatedBytes(precise: true);
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < iterations; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var lease = pool.AcquireAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert.Equal(NntpSessionState.Ready, lease.Session.State);
                lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            }

            var elapsed = Stopwatch.GetElapsedTime(start);
            var total = GC.GetTotalAllocatedBytes(precise: true) - totalBefore;
            Console.WriteLine(
                $"after lease n={iterations} caller-alloc/op={allocated / iterations} process-alloc/op={total / iterations} acquires/s={iterations / elapsed.TotalSeconds:F0} gen0={GC.CollectionCount(0) - g0} permits={pool.AvailablePermitCount}");
        }

        /// <summary>Returns the already constructed stream. The article measurement owns its lifetime.</summary>
        private sealed class ImmediateNntpTransportFactory(ImmediateScriptedNntpStream stream) : INntpTransportFactory
        {
            public Task<Stream> ConnectAsync(
                BackFillerProviderDefinition provider,
                NntpSessionOptions options,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(provider);
                ArgumentNullException.ThrowIfNull(options);
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<Stream>(stream);
            }
        }

        /// <summary>
        /// In-memory NNTP peer. A command write appends the matching response before the write returns.
        /// </summary>
        private sealed class ImmediateScriptedNntpStream : Stream
        {
            private static readonly byte[] Greeting = "200 posting allowed\r\n"u8.ToArray();

            private static readonly byte[] Capabilities =
                "101 Capability list follows\r\nVERSION 2\r\nREADER\r\n.\r\n"u8.ToArray();

            private static readonly byte[] Article =
                "220 0 <id@example.invalid> article follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n"u8.ToArray();

            private static readonly byte[] Ready = "201 ready\r\n"u8.ToArray();

            private readonly byte[] _inbound = new byte[1024];

            private readonly byte[] _command = new byte[256];

            private int _inboundOffset;

            private int _inboundCount;

            private int _commandCount;

            public ImmediateScriptedNntpStream()
            {
                Append(Greeting);
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_inboundCount == 0)
                {
                    throw new InvalidOperationException("Immediate NNTP peer was read before a response was buffered.");
                }

                var n = Math.Min(buffer.Length, _inboundCount);
                _inbound.AsSpan(_inboundOffset, n).CopyTo(buffer.Span);
                _inboundOffset += n;
                _inboundCount -= n;
                if (_inboundCount == 0)
                {
                    _inboundOffset = 0;
                }

                return new ValueTask<int>(n);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = buffer.Span;
                while (remaining.Length > 0)
                {
                    var newline = remaining.IndexOf((byte)'\n');
                    if (newline < 0)
                    {
                        CopyCommand(remaining);
                        break;
                    }

                    CopyCommand(remaining[..newline]);
                    remaining = remaining[(newline + 1)..];
                    DispatchCommand();
                }

                return ValueTask.CompletedTask;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            private void CopyCommand(ReadOnlySpan<byte> data)
            {
                if (_commandCount + data.Length > _command.Length)
                {
                    throw new InvalidOperationException("Immediate NNTP command exceeded the buffer.");
                }

                data.CopyTo(_command.AsSpan(_commandCount));
                _commandCount += data.Length;
            }

            private void DispatchCommand()
            {
                var line = _command.AsSpan(0, _commandCount);
                if (line.Length > 0 && line[^1] == (byte)'\r')
                {
                    line = line[..^1];
                }

                _commandCount = 0;
                if (line.StartsWith("ARTICLE "u8) || line.SequenceEqual("ARTICLE"u8))
                {
                    Append(Article);
                }
                else if (line.SequenceEqual("CAPABILITIES"u8))
                {
                    Append(Capabilities);
                }
                else if (line.SequenceEqual("QUIT"u8))
                {
                    // Dispose writes QUIT and does not read a reply.
                }
                else
                {
                    Append(Ready);
                }
            }

            private void Append(ReadOnlySpan<byte> data)
            {
                if (_inboundOffset > 0 && _inboundOffset + _inboundCount + data.Length > _inbound.Length)
                {
                    Buffer.BlockCopy(_inbound, _inboundOffset, _inbound, 0, _inboundCount);
                    _inboundOffset = 0;
                }

                if (_inboundOffset + _inboundCount + data.Length > _inbound.Length)
                {
                    throw new InvalidOperationException("Immediate NNTP response exceeded the buffer.");
                }

                data.CopyTo(_inbound.AsSpan(_inboundOffset + _inboundCount));
                _inboundCount += data.Length;
            }
        }

        private sealed class CyclingByteStream(byte[] data) : Stream
        {
            private int _pos;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => data.Length;

            public override long Position
            {
                get => _pos;
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                if (_pos >= data.Length)
                {
                    _pos = 0;
                }

                var n = Math.Min(buffer.Length, data.Length - _pos);
                data.AsSpan(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<int>(Read(buffer.Span));
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
