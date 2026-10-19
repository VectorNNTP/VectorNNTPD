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
    /// Allocation counts are the measurement. This is an in-memory reader, one scripted session, and one idle lease, not the full service.
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
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(static command =>
                command.StartsWith("ARTICLE", StringComparison.Ordinal)
                    ? "220 0 <id@example.invalid> article follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n"
                    : "201 ready\r\n");
            factory.Enqueue(server);
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
                    Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
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
