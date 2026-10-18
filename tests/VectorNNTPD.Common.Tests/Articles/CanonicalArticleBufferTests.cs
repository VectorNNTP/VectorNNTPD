using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles.Processing;
using Xunit.Abstractions;

namespace VectorNNTPD.Common.Tests.Articles
{
    /// <summary>
    /// Proves pooled canonical materialization keeps the logical article length distinct from the rented array.
    /// </summary>
    public sealed class CanonicalArticleBufferTests
    {
        /// <summary>Body size near the corpus median. Headers make the canonical image slightly larger.</summary>
        private const int LargeBodyBytes = 740 * 1024;

        private readonly ITestOutputHelper _output;

        /// <summary>Captures allocation figures. Those figures are not pass conditions except the bounds below.</summary>
        /// <param name="output">xUnit output for the allocation report.</param>
        public CanonicalArticleBufferTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// A rented canonical image keeps its logical bytes and hash when the array is longer than the article.
        /// </summary>
        [Fact]
        public void Pooled_large_article_keeps_logical_bytes_and_matches_the_exact_hash()
        {
            var pool = new TrackingBytePool();
            var destuffed = Article("<large@example.test>", Body(LargeBodyBytes));
            var exact = ArticleRecordFactory.TryCreate(Parser(), destuffed, ArticlePathMode.Traverse);
            var pooled = ArticleRecordFactory.TryCreate(Parser(), destuffed, ArticlePathMode.Traverse, pool);

            Assert.True(exact.IsAccepted);
            Assert.True(pooled.IsAccepted);
            Assert.Equal(1, pool.RentCount);
            Assert.Equal(0, pool.ReturnCount);
            Assert.True(pooled.Record.ArtSize > LargeBodyBytes);
            Assert.Equal(exact.Record.ArtSize, pooled.Record.ArtSize);
            Assert.Equal(exact.Record.ArtHash, pooled.Record.ArtHash);
            Assert.True(pooled.Record.ArtData.Span.SequenceEqual(exact.Record.ArtData.Span));
            Assert.True(MemoryMarshal.TryGetArray(pooled.Record.ArtData, out var segment));
            Assert.True(segment.Array!.Length > pooled.Record.ArtSize);
            segment.Array.AsSpan(pooled.Record.ArtSize).Fill(0x5A);
            Assert.Equal(exact.Record.ArtHash, System.IO.Hashing.XxHash3.HashToUInt64(pooled.Record.ArtData.Span));
            Assert.NotEqual(
                exact.Record.ArtHash,
                System.IO.Hashing.XxHash3.HashToUInt64(segment.Array));

            pooled.Record.ReleaseUnusedCanonicalBuffer();
            pooled.Record.ReleaseUnusedCanonicalBuffer();
            Assert.Equal(1, pool.ReturnCount);
            Assert.Equal(0, pool.Outstanding);
            Assert.True(pooled.Record.ArtData.IsEmpty);
            var released = pooled.Record;
            Assert.False(released.TryCreateBufferLease(out var revived));
            Assert.Null(revived);
            Assert.Equal(1, pool.ReturnCount);
        }

        /// <summary>
        /// A lease releases only the reference it acquired. After the last release, another lease cannot revive the array.
        /// </summary>
        [Fact]
        public void Lease_release_cannot_revive_or_return_the_array_twice()
        {
            var pool = new TrackingBytePool();
            var created = ArticleRecordFactory.TryCreate(
                Parser(),
                Article("<lease@example.test>", "body\r\n"),
                ArticlePathMode.Traverse,
                pool);
            Assert.True(created.IsAccepted);
            var record = created.Record;
            Assert.True(record.TryCreateBufferLease(out var lease));
            lease!.Dispose();
            lease.Dispose();
            Assert.Equal(0, pool.ReturnCount);
            Assert.False(record.ArtData.IsEmpty);

            record.ReleaseUnusedCanonicalBuffer();
            Assert.Equal(1, pool.ReturnCount);
            Assert.True(record.ArtData.IsEmpty);
            Assert.False(record.TryCreateBufferLease(out _));
            record.ReleaseUnusedCanonicalBuffer();
            Assert.Equal(1, pool.ReturnCount);
            Assert.Equal(0, pool.Outstanding);
        }

        /// <summary>A parse rejection rents nothing. A rent that cannot hold the article is returned once.</summary>
        [Fact]
        public void Rejected_parse_and_failed_rent_release_every_rented_buffer()
        {
            var idle = new TrackingBytePool();
            var rejected = ArticleRecordFactory.TryCreate(
                Parser(),
                "not an article"u8.ToArray(),
                ArticlePathMode.Traverse,
                idle);
            Assert.False(rejected.IsAccepted);
            Assert.Equal(0, idle.RentCount);
            Assert.Equal(0, idle.ReturnCount);

            var parsed = Parser().Parse(Article("<ok@example.test>", "body\r\n"));
            Assert.True(parsed.IsAccepted);
            var shortPool = new ShortArrayPool();
            Assert.ThrowsAny<ArgumentException>(() => NntpArticleCanonicalMaterializer.Materialize(
                in parsed,
                ArticlePathMode.Normalize,
                ArticleResourceLimits.MaxArticleBytes,
                ArticlePathCanonicalizer.OrganizationalTrackerHost,
                shortPool));
            Assert.Equal(1, shortPool.RentCount);
            Assert.Equal(1, shortPool.ReturnCount);

            var factoryPool = new ShortArrayPool();
            Assert.ThrowsAny<ArgumentException>(() => ArticleRecordFactory.TryCreate(
                Parser(),
                Article("<ok@example.test>", "body\r\n"),
                ArticlePathMode.Traverse,
                factoryPool));
            Assert.Equal(1, factoryPool.RentCount);
            Assert.Equal(1, factoryPool.ReturnCount);
        }

        /// <summary>Omitting the pool still produces an exact-size array and does not rent.</summary>
        [Fact]
        public void Exact_array_mode_uses_the_logical_length_as_the_array_length()
        {
            var pool = new TrackingBytePool();
            var created = ArticleRecordFactory.TryCreate(
                Parser(),
                Article("<exact@example.test>", "body\r\n"),
                ArticlePathMode.Traverse);
            Assert.True(created.IsAccepted);
            Assert.Equal(0, pool.RentCount);
            Assert.True(MemoryMarshal.TryGetArray(created.Record.ArtData, out var segment));
            Assert.Equal(created.Record.ArtSize, segment.Count);
            Assert.Equal(created.Record.ArtSize, segment.Array!.Length);
            Assert.Equal(created.Record.ArtData.Length, created.Record.ArtSize);
            var image = created.Record.ArtData.ToArray();
            created.Record.ReleaseUnusedCanonicalBuffer();
            created.Record.ReleaseUnusedCanonicalBuffer();
            Assert.True(created.Record.ArtData.Span.SequenceEqual(image));
            Assert.Equal(0, pool.RentCount);
        }

        /// <summary>
        /// Separates the first pooled rent, which allocates the article array, from later rents of a private reuse pool.
        /// The warmed bound is the pass condition. This is not an <see cref="ArrayPool{T}.Shared"/> measurement:
        /// Shared may retain a large array for the process lifetime, and the printed GC snapshot still contains the exact-path arrays.
        /// </summary>
        [Fact]
        public void Pooled_materialization_allocates_far_less_than_the_article()
        {
            var sizes = new (string Name, int Body)[]
            {
                ("small", 2 * 1024),
                ("median", LargeBodyBytes),
                ("large", 2 * 1024 * 1024),
            };

            foreach (var (name, bodyBytes) in sizes)
            {
                var destuffed = Article("<m@example.test>", Body(bodyBytes));
                var parser = Parser();
                var exactWarm = ArticleRecordFactory.TryCreate(parser, destuffed, ArticlePathMode.Traverse);
                Assert.True(exactWarm.IsAccepted);
                var beforeExact = GC.GetAllocatedBytesForCurrentThread();
                var exact = ArticleRecordFactory.TryCreate(parser, destuffed, ArticlePathMode.Traverse);
                var exactAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeExact;
                Assert.True(exact.IsAccepted);

                var coldPool = new ReuseBytePool();
                var beforeCold = GC.GetAllocatedBytesForCurrentThread();
                var cold = ArticleRecordFactory.TryCreate(parser, destuffed, ArticlePathMode.Traverse, coldPool);
                var coldAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeCold;
                Assert.True(cold.IsAccepted);
                cold.Record.ReleaseUnusedCanonicalBuffer();

                var pool = new ReuseBytePool();
                var warm = ArticleRecordFactory.TryCreate(parser, destuffed, ArticlePathMode.Traverse, pool);
                Assert.True(warm.IsAccepted);
                warm.Record.ReleaseUnusedCanonicalBuffer();
                long warmedMin = long.MaxValue;
                long warmedMax = 0;
                for (var iteration = 0; iteration < 4; iteration++)
                {
                    var beforePooled = GC.GetAllocatedBytesForCurrentThread();
                    var pooled = ArticleRecordFactory.TryCreate(parser, destuffed, ArticlePathMode.Traverse, pool);
                    var pooledAllocated = GC.GetAllocatedBytesForCurrentThread() - beforePooled;
                    Assert.True(pooled.IsAccepted);
                    Assert.Equal(exact.Record.ArtHash, pooled.Record.ArtHash);
                    Assert.Equal(exact.Record.ArtSize, pooled.Record.ArtSize);
                    pooled.Record.ReleaseUnusedCanonicalBuffer();
                    warmedMin = Math.Min(warmedMin, pooledAllocated);
                    warmedMax = Math.Max(warmedMax, pooledAllocated);
                }

                _output.WriteLine(
                    $"{name} artSize={exact.Record.ArtSize} exactAllocated={exactAllocated} "
                    + $"firstRentAllocated={coldAllocated} warmedMin={warmedMin} warmedMax={warmedMax} "
                    + "reusePool=private snapshot=not-Shared-LOH");

                Assert.True(exactAllocated >= exact.Record.ArtSize);
                Assert.True(coldAllocated >= exact.Record.ArtSize);
                Assert.True(warmedMax < exact.Record.ArtSize / 8);
                Assert.True(warmedMin < 1024);
            }
        }

        /// <summary>Parser identity used by these materialization tests.</summary>
        private static NntpArticleParser Parser() => new("backfiller.test");

        /// <summary>Destuffed article with a stable header set and the supplied body.</summary>
        private static byte[] Article(string messageId, string body)
        {
            var text =
                "Path: peer.example\r\n"
                + "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n"
                + "Message-ID: " + messageId + "\r\n"
                + "Newsgroups: alt.test\r\n"
                + "From: user@example.test\r\n"
                + "Subject: s\r\n"
                + "\r\n"
                + body;
            return Encoding.ASCII.GetBytes(text);
        }

        /// <summary>Repeats a 1000-byte line until <paramref name="targetBytes"/> is reached.</summary>
        private static string Body(int targetBytes)
        {
            var line = new string('x', 998) + "\r\n";
            var builder = new StringBuilder(targetBytes + line.Length);
            while (builder.Length < targetBytes)
            {
                builder.Append(line);
            }

            return builder.ToString();
        }

        /// <summary>Pool that rents a longer array and throws if the same array is returned twice.</summary>
        private sealed class TrackingBytePool : ArrayPool<byte>
        {
            private readonly ConcurrentDictionary<byte[], byte> _outstanding = new(ReferenceEqualityComparer.Instance);
            private int _rents;
            private int _returns;

            /// <summary>Number of <see cref="Rent"/> calls.</summary>
            public int RentCount => Volatile.Read(ref _rents);

            /// <summary>Number of successful <see cref="Return(byte[], bool)"/> calls.</summary>
            public int ReturnCount => Volatile.Read(ref _returns);

            /// <summary>Arrays rented and not yet returned.</summary>
            public int Outstanding => _outstanding.Count;

            /// <inheritdoc />
            public override byte[] Rent(int minimumLength)
            {
                Interlocked.Increment(ref _rents);
                var buffer = GC.AllocateUninitializedArray<byte>(checked(minimumLength + 64));
                if (!_outstanding.TryAdd(buffer, 0))
                {
                    throw new InvalidOperationException("The rented array was already outstanding.");
                }

                return buffer;
            }

            /// <inheritdoc />
            public override void Return(byte[] array, bool clearArray = false)
            {
                if (!_outstanding.TryRemove(array, out _))
                {
                    throw new InvalidOperationException("The array was returned twice or was not rented here.");
                }

                Interlocked.Increment(ref _returns);
                array.AsSpan().Fill(0xA5);
            }
        }

        /// <summary>Pool that hands back the same array after it is returned, so the next rent allocates nothing.</summary>
        private sealed class ReuseBytePool : ArrayPool<byte>
        {
            private byte[]? _cached;

            /// <inheritdoc />
            public override byte[] Rent(int minimumLength)
            {
                var cached = _cached;
                if (cached is not null && cached.Length >= minimumLength)
                {
                    _cached = null;
                    return cached;
                }

                return GC.AllocateUninitializedArray<byte>(checked(minimumLength + 64));
            }

            /// <inheritdoc />
            public override void Return(byte[] array, bool clearArray = false)
            {
                _cached = array;
            }
        }

        /// <summary>Pool that rents one byte so the materializer fails after the rent.</summary>
        private sealed class ShortArrayPool : ArrayPool<byte>
        {
            private int _rents;
            private int _returns;

            /// <summary>Number of <see cref="Rent"/> calls.</summary>
            public int RentCount => _rents;

            /// <summary>Number of <see cref="Return(byte[], bool)"/> calls.</summary>
            public int ReturnCount => _returns;

            /// <inheritdoc />
            public override byte[] Rent(int minimumLength)
            {
                _rents++;
                return new byte[1];
            }

            /// <inheritdoc />
            public override void Return(byte[] array, bool clearArray = false)
            {
                _returns++;
            }
        }
    }
}
