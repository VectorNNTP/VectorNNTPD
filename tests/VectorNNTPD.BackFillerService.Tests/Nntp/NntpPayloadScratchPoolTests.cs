using System.Buffers;
using System.Runtime.InteropServices;
using VectorNNTPD.BackFillerService.Nntp;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>
    /// Rent, growth, trim, and disposal of <see cref="NntpStreamReader"/> payload scratch.
    /// The retained request after a payload read is 1 MiB. A larger rental is returned once that read finishes.
    /// </summary>
    public sealed class NntpPayloadScratchPoolTests
    {
        /// <summary>Request size the reader keeps for later ordinary articles.</summary>
        private const int WorkingSetBytes = 1024 * 1024;

        /// <summary>Article ceiling used when a test must be allowed to pass 1 MiB.</summary>
        private const int MaximumArticleBytes = 5 * 1024 * 1024;

        /// <summary>Two small payloads on one reader rent a single scratch.</summary>
        [Fact]
        public async Task Ordinary_payloads_reuse_one_scratch_without_corruption()
        {
            var first = Pattern(48);
            var second = Pattern(80, seed: 9);
            var pool = CreatePooled();
            using var reader = Reader(Frame(first).Concat(Frame(second)).ToArray(), pool);

            Assert.Equal(first, await ReadExactAsync(reader, WorkingSetBytes));
            var rentsAfterFirst = pool.RentCount;
            Assert.Equal(second, await ReadExactAsync(reader, WorkingSetBytes));

            Assert.Equal(rentsAfterFirst, pool.RentCount);
            Assert.Equal(0, pool.ReturnCount);
            Assert.Equal(1, pool.OutstandingCount);
            Assert.True(pool.OutstandingRequests().Single() <= WorkingSetBytes);
            Assert.True(pool.OutstandingLengths().Single() >= pool.OutstandingRequests().Single());
        }

        /// <summary>An array longer than the rental request still carries the payload and is returned once.</summary>
        [Fact]
        public async Task Rented_array_longer_than_the_request_is_returned_once()
        {
            var payload = Pattern(40);
            var pool = new TrackingArrayPool(slackBytes: 128);
            using var reader = Reader(Frame(payload), pool);
            Assert.Equal(payload, await ReadExactAsync(reader, maxBytes: 256));
            Assert.Equal(1, pool.RentCount);
            Assert.True(pool.OutstandingLengths().Single() >= pool.OutstandingRequests().Single() + 128);
            Assert.Equal(0, pool.ReturnCount);

            reader.Dispose();
            reader.Dispose();
            Assert.Equal(pool.RentCount, pool.ReturnCount);
            Assert.Equal(0, pool.OutstandingCount);
        }

        /// <summary>A payload that crosses 1 MiB returns the replaced scratch, and the destuffed bytes match.</summary>
        [Fact]
        public async Task Growth_returns_the_replaced_buffer_and_preserves_received_bytes()
        {
            var payload = Pattern(WorkingSetBytes + 64);
            var pool = CreatePooled();
            using (var reader = Reader(Frame(payload), pool))
            {
                Assert.Equal(payload, await ReadExactAsync(reader, MaximumArticleBytes));
                Assert.Contains(pool.ReturnedRequests, static request => request > 0 && request <= WorkingSetBytes);
                Assert.Equal(WorkingSetBytes, pool.OutstandingRequests().Single());
                Assert.True(pool.OutstandingLengths().Single() >= WorkingSetBytes);
            }

            Assert.Equal(pool.RentCount, pool.ReturnCount);
        }

        /// <summary>Exactly 1 MiB does not grow. One byte past it is trimmed back to a 1 MiB request.</summary>
        [Fact]
        public async Task Working_set_boundary_trims_only_after_the_request_grows()
        {
            var exact = Pattern(WorkingSetBytes);
            var exactPool = CreatePooled();
            using (var reader = Reader(Frame(exact), exactPool))
            {
                Assert.Equal(exact, await ReadExactAsync(reader, MaximumArticleBytes));
                Assert.Equal(1, exactPool.RentCount);
                Assert.Equal(WorkingSetBytes, exactPool.OutstandingRequests().Single());
                Assert.Empty(exactPool.ReturnedRequests);
            }

            var over = Pattern(WorkingSetBytes + 1);
            var overPool = CreatePooled();
            using (var reader = Reader(Frame(over), overPool))
            {
                Assert.Equal(over, await ReadExactAsync(reader, MaximumArticleBytes));
                Assert.True(overPool.RentCount > 1);
                Assert.Equal(WorkingSetBytes, overPool.OutstandingRequests().Single());
            }
        }

        /// <summary>A 5 MiB article is received, then the next ordinary article reuses the trimmed 1 MiB request.</summary>
        [Fact]
        public async Task Large_article_is_trimmed_before_the_next_ordinary_article()
        {
            var large = Pattern(MaximumArticleBytes);
            var ordinary = Pattern(120, seed: 3);
            var pool = CreatePooled();
            using var reader = Reader(Frame(large).Concat(Frame(ordinary)).ToArray(), pool);

            Assert.Equal(large, await ReadExactAsync(reader, MaximumArticleBytes));
            Assert.Equal(WorkingSetBytes, pool.OutstandingRequests().Single());
            var rentsAfterLarge = pool.RentCount;

            Assert.Equal(ordinary, await ReadExactAsync(reader, MaximumArticleBytes));
            Assert.Equal(rentsAfterLarge, pool.RentCount);
            Assert.Equal(WorkingSetBytes, pool.OutstandingRequests().Single());
            Assert.True(pool.OutstandingLengths().Single() >= WorkingSetBytes);
        }

        /// <summary>Bytes still in the receive buffer after a large article are the following article.</summary>
        [Fact]
        public async Task Trim_between_articles_keeps_the_unread_tail()
        {
            var large = Pattern(WorkingSetBytes + 32);
            var ordinary = Pattern(24, seed: 5);
            var pool = CreatePooled();
            using var reader = Reader(Frame(large).Concat(Frame(ordinary)).ToArray(), pool);

            Assert.Equal(large, await ReadExactAsync(reader, MaximumArticleBytes));
            Assert.Equal(WorkingSetBytes, pool.OutstandingRequests().Single());
            Assert.Equal(ordinary, await ReadExactAsync(reader, MaximumArticleBytes));
        }

        /// <summary>An immediate terminator rents nothing, and a second disposal does not return a buffer.</summary>
        [Fact]
        public async Task Empty_payload_and_repeated_disposal_return_nothing()
        {
            var pool = new TrackingArrayPool(slackBytes: 0);
            using var reader = Reader(".\r\n"u8.ToArray(), pool);
            var payload = await reader.ReadArticlePayloadAsync(1024, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Empty(payload);
            Assert.Equal(0, pool.RentCount);
            reader.Dispose();
            reader.Dispose();
            Assert.Equal(0, pool.ReturnCount);
        }

        /// <summary>The callback observes the scratch before disposal, and disposal returns it once.</summary>
        [Fact]
        public async Task Callback_failure_does_not_return_the_active_scratch()
        {
            var payload = Pattern(48);
            var pool = new TrackingArrayPool(slackBytes: 32);
            using var reader = Reader(Frame(payload), pool);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reader.ReadArticlePayloadAsync(
                    1024,
                    TimeSpan.FromSeconds(5),
                    memory => FailWhileScratchIsLive(memory, payload, pool),
                    CancellationToken.None));
            Assert.Equal("consumer failed", exception.Message);
            Assert.Equal(1, pool.OutstandingCount);
            Assert.True(pool.ReturnCount < pool.RentCount);
            reader.Dispose();

            Assert.Equal(pool.RentCount, pool.ReturnCount);
            Assert.Equal(0, pool.OutstandingCount);
        }

        /// <summary>A cancelled read returns any scratch it rented when the reader is disposed.</summary>
        [Fact]
        public async Task Cancellation_before_the_payload_stays_balanced()
        {
            var pool = new TrackingArrayPool(slackBytes: 0);
            using var reader = Reader(Frame(Pattern(32)), pool);
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                reader.ReadArticlePayloadAsync(1024, TimeSpan.FromSeconds(5), cancelled.Token));
            Assert.Equal(pool.RentCount, pool.ReturnCount);
        }

        /// <summary>Disposal while the socket read is blocked returns the scratch only after the callback returns.</summary>
        [Fact]
        public async Task Disposal_during_a_read_returns_the_scratch_after_the_callback()
        {
            var payload = Pattern(36);
            var pool = new TrackingArrayPool(slackBytes: 16);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var gate = new GateStream(Frame(payload), entered, release.Task);
            using var reader = new NntpStreamReader(gate, 1024, pool);
            var read = reader.ReadArticlePayloadAsync(
                1024,
                TimeSpan.FromSeconds(5),
                memory =>
                {
                    Assert.False(OwnsReturnedArray(memory, pool));
                    return memory.ToArray();
                },
                CancellationToken.None);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            reader.Dispose();
            release.SetResult();

            Assert.Equal(payload, await read.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(pool.RentCount, pool.ReturnCount);
            reader.Dispose();
            Assert.Equal(pool.RentCount, pool.ReturnCount);
        }

        /// <summary>The exact array is a copy. Disposal may return the scratch without changing that copy.</summary>
        [Fact]
        public async Task Exact_array_survives_returning_the_scratch()
        {
            var payload = Pattern(28);
            var pool = new TrackingArrayPool(slackBytes: 64);
            byte[] exact;
            using (var reader = Reader(Frame(payload), pool))
            {
                exact = await reader.ReadArticlePayloadAsync(1024, TimeSpan.FromSeconds(5), CancellationToken.None);
            }

            Assert.Equal(payload, exact);
            Assert.Equal(payload.Length, exact.Length);
            Assert.Equal(pool.RentCount, pool.ReturnCount);
            Assert.All(pool.ReturnedArrays, array => Assert.NotSame(exact, array));
        }

        /// <summary>A stream that ends without a terminator still throws, and disposal balances rentals.</summary>
        [Fact]
        public async Task Missing_terminator_preserves_the_error_and_returns_scratch()
        {
            var pool = new TrackingArrayPool(slackBytes: 0);
            using (var reader = Reader("abc"u8.ToArray(), pool))
            {
                var exception = await Assert.ThrowsAsync<EndOfStreamException>(() =>
                    reader.ReadArticlePayloadAsync(1024, TimeSpan.FromSeconds(5), CancellationToken.None));
                Assert.Contains("terminator", exception.Message, StringComparison.Ordinal);
            }

            Assert.Equal(pool.RentCount, pool.ReturnCount);
        }

        /// <summary>Crossing a caller ceiling still reports MaxArticleBytes, and disposal returns the scratch.</summary>
        [Fact]
        public async Task Maximum_size_violation_preserves_the_error_and_balances_the_pool()
        {
            var pool = new TrackingArrayPool(slackBytes: 0);
            using (var reader = Reader(Frame(Pattern(33)), pool))
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    ReadExactAsync(reader, maxBytes: 32));
                Assert.Contains("MaxArticleBytes", exception.Message, StringComparison.Ordinal);
            }

            Assert.Equal(pool.RentCount, pool.ReturnCount);
        }

        /// <summary>
        /// Prints scratch rent traffic for ordinary, large, and repeated-session reads.
        /// Counts are evidence. They are not a pass/fail budget.
        /// </summary>
        [Fact]
        public async Task Measure_payload_scratch_rent_and_retained_request()
        {
            var ordinary = Pattern(4096);
            var ordinaryWire = Frame(ordinary);
            var pool = CreatePooled();
            using (var reader = Reader(Repeat(ordinaryWire, 16), pool))
            {
                for (var i = 0; i < 16; i++)
                {
                    Assert.Equal(ordinary.Length, await ReadLengthAsync(reader, MaximumArticleBytes));
                }
            }

            Console.WriteLine(
                $"payload-scratch ordinary n=16 rents={pool.RentCount} returns={pool.ReturnCount} outstanding={pool.OutstandingCount}");
            Assert.Equal(pool.RentCount, pool.ReturnCount);

            pool.ResetCounts();
            var large = Pattern(MaximumArticleBytes);
            using (var reader = Reader(Frame(large).Concat(ordinaryWire).ToArray(), pool))
            {
                Assert.Equal(large.Length, await ReadLengthAsync(reader, MaximumArticleBytes));
                var afterLarge = pool.OutstandingRequests().Single();
                var rentsForLarge = pool.RentCount;
                Assert.Equal(ordinary.Length, await ReadLengthAsync(reader, MaximumArticleBytes));
                Console.WriteLine(
                    $"payload-scratch large-then-ordinary largeRents={rentsForLarge} retainedRequest={afterLarge} ordinaryExtraRents={pool.RentCount - rentsForLarge} outstandingRequest={pool.OutstandingRequests().Single()}");
                Assert.Equal(WorkingSetBytes, afterLarge);
                Assert.Equal(0, pool.RentCount - rentsForLarge);
            }

            pool.ResetCounts();
            long secondArticleAllocated;
            using (var reader = Reader(Frame(large).Concat(Frame(large)).ToArray(), pool))
            {
                Assert.Equal(large.Length, await ReadLengthAsync(reader, MaximumArticleBytes));
                var before = GC.GetAllocatedBytesForCurrentThread();
                Assert.Equal(large.Length, await ReadLengthAsync(reader, MaximumArticleBytes));
                secondArticleAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Console.WriteLine(
                $"payload-scratch repeated-large secondArticleAllocated={secondArticleAllocated} totalRents={pool.RentCount} totalReturns={pool.ReturnCount}");
            Assert.Equal(pool.RentCount, pool.ReturnCount);

            pool.ResetCounts();
            var sessionBefore = GC.GetAllocatedBytesForCurrentThread();
            const int sessions = 16;
            for (var i = 0; i < sessions; i++)
            {
                using var reader = Reader(ordinaryWire, pool);
                Assert.Equal(ordinary.Length, await ReadLengthAsync(reader, MaximumArticleBytes));
            }

            var sessionAllocated = GC.GetAllocatedBytesForCurrentThread() - sessionBefore;
            Console.WriteLine(
                $"payload-scratch session-churn n={sessions} alloc/session={sessionAllocated / sessions} rents={pool.RentCount} returns={pool.ReturnCount} outstanding={pool.OutstandingCount}");
            Assert.Equal(0, pool.OutstandingCount);
            Assert.Equal(pool.RentCount, pool.ReturnCount);
        }

        /// <summary>Creates the same pool shape the reader uses in production.</summary>
        private static TrackingArrayPool CreatePooled() =>
            new(ArrayPool<byte>.Create(maxArrayLength: 8 * 1024 * 1024, maxArraysPerBucket: 4));

        /// <summary>Creates a reader for one or more framed payloads.</summary>
        private static NntpStreamReader Reader(byte[] wire, TrackingArrayPool pool) =>
            new(new MemoryStream(wire, writable: false), 1024, pool);

        /// <summary>Reads one exact destuffed payload from the next framed article on <paramref name="reader"/>.</summary>
        private static Task<byte[]> ReadExactAsync(NntpStreamReader reader, int maxBytes) =>
            reader.ReadArticlePayloadAsync(maxBytes, TimeSpan.FromSeconds(5), CancellationToken.None);

        /// <summary>Reads one payload through the callback and returns only its length.</summary>
        private static Task<int> ReadLengthAsync(NntpStreamReader reader, int maxBytes) =>
            reader.ReadArticlePayloadAsync(
                maxBytes,
                TimeSpan.FromSeconds(5),
                static memory => memory.Length,
                CancellationToken.None);

        /// <summary>Repeats <paramref name="wire"/> so one reader can pull several articles.</summary>
        private static byte[] Repeat(byte[] wire, int count)
        {
            var all = new byte[wire.Length * count];
            for (var i = 0; i < count; i++)
            {
                wire.CopyTo(all.AsSpan(i * wire.Length));
            }

            return all;
        }

        /// <summary>Builds a destuffed payload of <paramref name="length"/> bytes ending in CRLF.</summary>
        private static byte[] Pattern(int length, int seed = 1)
        {
            var payload = new byte[length];
            for (var i = 0; i < length - 2; i++)
            {
                payload[i] = (byte)((i + seed) % 251);
            }

            payload[^2] = (byte)'\r';
            payload[^1] = (byte)'\n';
            return payload;
        }

        /// <summary>Appends the multiline terminator to a payload that ends at a line boundary.</summary>
        private static byte[] Frame(byte[] payload)
        {
            var framed = new byte[payload.Length + 3];
            payload.CopyTo(framed);
            framed[^3] = (byte)'.';
            framed[^2] = (byte)'\r';
            framed[^1] = (byte)'\n';
            return framed;
        }

        /// <summary>Asserts the callback still owns <paramref name="memory"/>, then fails the read.</summary>
        private static int FailWhileScratchIsLive(ReadOnlyMemory<byte> memory, byte[] payload, TrackingArrayPool pool)
        {
            Assert.Equal(payload, memory.ToArray());
            Assert.False(OwnsReturnedArray(memory, pool));
            throw new InvalidOperationException("consumer failed");
        }

        /// <summary>Returns whether the callback memory's array has already been returned to <paramref name="pool"/>.</summary>
        private static bool OwnsReturnedArray(ReadOnlyMemory<byte> memory, TrackingArrayPool pool)
        {
            Assert.True(MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment));
            Assert.NotNull(segment.Array);
            return pool.WasReturned(segment.Array);
        }

        /// <summary>Records rent and return identity for one reader.</summary>
        private sealed class TrackingArrayPool : ArrayPool<byte>
        {
            /// <summary>Inner pool. Null when this pool allocates <see cref="_slack"/> extra bytes itself.</summary>
            private readonly ArrayPool<byte>? _inner;

            /// <summary>Extra bytes added to each standalone rental. Zero when <see cref="_inner"/> is set.</summary>
            private readonly int _slack;

            /// <summary>Guards the counters and the outstanding map.</summary>
            private readonly Lock _gate = new();

            /// <summary>Arrays rented and not yet returned, keyed by reference, valued by the requested minimum.</summary>
            private readonly Dictionary<byte[], int> _outstanding = new(ReferenceEqualityComparer.Instance);

            /// <summary>Request sizes passed to <see cref="Return"/>, in return order.</summary>
            private readonly List<int> _returnedRequests = [];

            /// <summary>Arrays passed to <see cref="Return"/>, in return order.</summary>
            private readonly List<byte[]> _returnedArrays = [];

            /// <summary>Creates a pool that delegates storage to <paramref name="inner"/>.</summary>
            internal TrackingArrayPool(ArrayPool<byte> inner)
            {
                _inner = inner;
            }

            /// <summary>Creates a pool whose arrays are <paramref name="slackBytes"/> longer than the request.</summary>
            internal TrackingArrayPool(int slackBytes)
            {
                _slack = slackBytes;
            }

            /// <summary>Calls to <see cref="Rent"/>.</summary>
            internal int RentCount { get; private set; }

            /// <summary>Calls to <see cref="Return"/>.</summary>
            internal int ReturnCount { get; private set; }

            /// <summary>Request sizes that have been returned.</summary>
            internal List<int> ReturnedRequests
            {
                get
                {
                    lock (_gate)
                    {
                        return [.. _returnedRequests];
                    }
                }
            }

            /// <summary>Arrays that have been returned.</summary>
            internal List<byte[]> ReturnedArrays
            {
                get
                {
                    lock (_gate)
                    {
                        return [.. _returnedArrays];
                    }
                }
            }

            /// <summary>Rentals that have not been returned.</summary>
            internal int OutstandingCount
            {
                get
                {
                    lock (_gate)
                    {
                        return _outstanding.Count;
                    }
                }
            }

            /// <inheritdoc />
            public override byte[] Rent(int minimumLength)
            {
                var array = _inner is null ? new byte[minimumLength + _slack] : _inner.Rent(minimumLength);
                lock (_gate)
                {
                    RentCount++;
                    _outstanding.Add(array, minimumLength);
                }

                return array;
            }

            /// <inheritdoc />
            public override void Return(byte[] array, bool clearArray = false)
            {
                ArgumentNullException.ThrowIfNull(array);
                lock (_gate)
                {
                    if (!_outstanding.Remove(array, out var request))
                    {
                        throw new InvalidOperationException("Returned a payload scratch that was not outstanding.");
                    }

                    ReturnCount++;
                    _returnedRequests.Add(request);
                    _returnedArrays.Add(array);
                }

                _inner?.Return(array, clearArray);
            }

            /// <summary>Request sizes of rentals that have not been returned.</summary>
            internal List<int> OutstandingRequests()
            {
                lock (_gate)
                {
                    return [.. _outstanding.Values];
                }
            }

            /// <summary>Physical lengths of rentals that have not been returned.</summary>
            internal List<int> OutstandingLengths()
            {
                lock (_gate)
                {
                    return [.. _outstanding.Keys.Select(static array => array.Length)];
                }
            }

            /// <summary>Returns whether <paramref name="array"/> was already passed to <see cref="Return"/>.</summary>
            internal bool WasReturned(byte[] array)
            {
                lock (_gate)
                {
                    return _returnedArrays.Contains(array);
                }
            }

            /// <summary>Clears counters and return history. Outstanding rentals stay outstanding.</summary>
            internal void ResetCounts()
            {
                lock (_gate)
                {
                    RentCount = 0;
                    ReturnCount = 0;
                    _returnedRequests.Clear();
                    _returnedArrays.Clear();
                }
            }
        }

        /// <summary>Blocks the first socket read until <see cref="_release"/> completes, after signalling <see cref="_entered"/>.</summary>
        private sealed class GateStream(byte[] data, TaskCompletionSource entered, Task release) : Stream
        {
            /// <summary>Next unread index in <paramref name="data"/>.</summary>
            private int _pos;

            /// <inheritdoc />
            public override bool CanRead => true;

            /// <inheritdoc />
            public override bool CanSeek => false;

            /// <inheritdoc />
            public override bool CanWrite => false;

            /// <inheritdoc />
            public override long Length => data.Length;

            /// <inheritdoc />
            public override long Position { get => _pos; set => throw new NotSupportedException(); }

            /// <inheritdoc />
            public override void Flush()
            {
            }

            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void SetLength(long value) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <inheritdoc />
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                entered.TrySetResult();
                await release.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (_pos >= data.Length)
                {
                    return 0;
                }

                var count = Math.Min(buffer.Length, data.Length - _pos);
                data.AsSpan(_pos, count).CopyTo(buffer.Span);
                _pos += count;
                return count;
            }
        }
    }
}
