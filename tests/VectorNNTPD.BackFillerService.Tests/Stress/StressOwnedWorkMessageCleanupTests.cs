using System.Text;
using static VectorNNTPD.BackFillerService.Tests.Stress.StressOwnedWorkMessageCleanup;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Deterministic coverage for StressRunId matching and the hold-unacked foreign scan
    /// (<see cref="DiscardOwnedHoldingForeignAsync"/>).
    /// </summary>
    public sealed class StressOwnedWorkMessageCleanupTests
    {
        private const string OwnedRunId = "owned-run";

        /// <summary>String header values match the owned run id.</summary>
        [Fact]
        public void IsOwnedByRun_matches_string_header()
        {
            var headers = new Dictionary<string, object?>
            {
                [StressRunIdHeader] = "run-a",
            };
            Assert.True(IsOwnedByRun(headers, "run-a"));
            Assert.False(IsOwnedByRun(headers, "run-b"));
        }

        /// <summary>UTF-8 byte[] headers (RabbitMQ.Client default) match the owned run id.</summary>
        [Fact]
        public void IsOwnedByRun_matches_utf8_byte_header()
        {
            var headers = new Dictionary<string, object?>
            {
                [StressRunIdHeader] = Encoding.UTF8.GetBytes("run-bytes"),
            };
            Assert.True(IsOwnedByRun(headers, "run-bytes"));
            Assert.False(IsOwnedByRun(headers, "other"));
        }

        /// <summary>Missing, blank, empty-byte, and non-string header values are never treated as owned.</summary>
        [Fact]
        public void IsOwnedByRun_rejects_missing_blank_empty_bytes_and_unknown_types()
        {
            Assert.False(IsOwnedByRun(null, "run-a"));
            Assert.False(IsOwnedByRun(new Dictionary<string, object?>(), "run-a"));
            Assert.False(
                IsOwnedByRun(
                    new Dictionary<string, object?> { [StressRunIdHeader] = "   " },
                    "run-a"));
            Assert.False(
                IsOwnedByRun(
                    new Dictionary<string, object?> { [StressRunIdHeader] = Array.Empty<byte>() },
                    "run-a"));
            Assert.False(
                IsOwnedByRun(
                    new Dictionary<string, object?> { [StressRunIdHeader] = 42 },
                    "run-a"));
            Assert.Null(
                TryGetStressRunId(new Dictionary<string, object?> { [StressRunIdHeader] = 42 }));
        }

        /// <summary>TryGetStressRunId trims string values.</summary>
        [Fact]
        public void TryGetStressRunId_trims_string()
        {
            var headers = new Dictionary<string, object?>
            {
                [StressRunIdHeader] = "  padded-id  ",
            };
            Assert.Equal("padded-id", TryGetStressRunId(headers));
        }

        /// <summary>Owned deliveries are nack-discarded; foreign deliveries are never settled.</summary>
        [Fact]
        public async Task Scan_discards_owned_and_never_acks_foreign()
        {
            var channel = new FakeCleanupChannel(
                Delivery(1, OwnedRunId),
                Delivery(2, "foreign-run"),
                Delivery(3, OwnedRunId));

            var removed = await DiscardOwnedHoldingForeignAsync(
                channel,
                OwnedRunId,
                maxVisits: 32,
                CancellationToken.None);

            Assert.Equal(2, removed);
            Assert.Equal(new ulong[] { 1, 3 }, channel.DiscardedTags);
            Assert.Empty(channel.AckedTags);
            Assert.Equal(new ulong[] { 2 }, channel.UnackedForeignTags);
            Assert.Equal(0, channel.DisposeCount);
        }

        /// <summary>Missing, blank, empty-byte, and unsupported headers are held as foreign (never discarded).</summary>
        [Fact]
        public async Task Scan_treats_missing_blank_and_malformed_headers_as_foreign()
        {
            var channel = new FakeCleanupChannel(
                new StressCleanupDelivery(1, null),
                new StressCleanupDelivery(2, new Dictionary<string, object?>()),
                new StressCleanupDelivery(3, new Dictionary<string, object?> { [StressRunIdHeader] = "  " }),
                new StressCleanupDelivery(4, new Dictionary<string, object?> { [StressRunIdHeader] = Array.Empty<byte>() }),
                new StressCleanupDelivery(5, new Dictionary<string, object?> { [StressRunIdHeader] = 99 }),
                Delivery(6, OwnedRunId));

            var removed = await DiscardOwnedHoldingForeignAsync(
                channel,
                OwnedRunId,
                maxVisits: 32,
                CancellationToken.None);

            Assert.Equal(1, removed);
            Assert.Equal(new ulong[] { 6 }, channel.DiscardedTags);
            Assert.Empty(channel.AckedTags);
            Assert.Equal(new ulong[] { 1, 2, 3, 4, 5 }, channel.UnackedForeignTags);
        }

        /// <summary>Owned messages behind foreign ready messages are still reached (no head-spin).</summary>
        [Fact]
        public async Task Scan_reaches_owned_messages_behind_foreign()
        {
            var channel = new FakeCleanupChannel(
                Delivery(10, "foreign-a"),
                Delivery(11, "foreign-b"),
                Delivery(12, OwnedRunId));

            var removed = await DiscardOwnedHoldingForeignAsync(
                channel,
                OwnedRunId,
                maxVisits: 32,
                CancellationToken.None);

            Assert.Equal(1, removed);
            Assert.Equal(new ulong[] { 12 }, channel.DiscardedTags);
            Assert.Equal(new ulong[] { 10, 11 }, channel.UnackedForeignTags);
            Assert.Equal(3, channel.GetCount);
        }

        /// <summary>Scan stops at maxVisits even when more deliveries remain.</summary>
        [Fact]
        public async Task Scan_stops_at_visit_limit()
        {
            var channel = new FakeCleanupChannel(
                Delivery(1, "foreign"),
                Delivery(2, OwnedRunId),
                Delivery(3, OwnedRunId),
                Delivery(4, OwnedRunId));

            var removed = await DiscardOwnedHoldingForeignAsync(
                channel,
                OwnedRunId,
                maxVisits: 2,
                CancellationToken.None);

            Assert.Equal(1, removed);
            Assert.Equal(2, channel.GetCount);
            Assert.Equal(new ulong[] { 2 }, channel.DiscardedTags);
            Assert.Equal(new ulong[] { 1 }, channel.UnackedForeignTags);
            Assert.Equal(2, channel.RemainingReadyCount);
        }

        /// <summary>
        /// Callers that use <c>await using</c> on the cleanup channel dispose it after the scan,
        /// including when the scan throws — modelling live channel dispose that requeues foreign.
        /// </summary>
        [Fact]
        public async Task Channel_dispose_is_guaranteed_when_scan_throws()
        {
            var channel = new FakeCleanupChannel(Delivery(1, "foreign"))
            {
                ThrowOnGetAfter = 0,
            };

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using (channel)
                {
                    _ = await DiscardOwnedHoldingForeignAsync(
                        channel,
                        OwnedRunId,
                        maxVisits: 8,
                        CancellationToken.None);
                }
            });

            Assert.Equal(1, channel.DisposeCount);
            Assert.Empty(channel.DiscardedTags);
            Assert.Empty(channel.AckedTags);
        }

        /// <summary>Builds an owned/foreign delivery with a string StressRunId header.</summary>
        private static StressCleanupDelivery Delivery(ulong tag, string stressRunId) =>
            new(
                tag,
                new Dictionary<string, object?>
                {
                    [StressRunIdHeader] = stressRunId,
                });

        /// <summary>
        /// In-memory cleanup channel: gets advance past unacked foreign holds; never invents acks for foreign.
        /// </summary>
        private sealed class FakeCleanupChannel : IStressCleanupChannel
        {
            private readonly Queue<StressCleanupDelivery> _ready;
            private readonly List<ulong> _unackedForeign = [];
            private readonly List<ulong> _discarded = [];
            private readonly List<ulong> _acked = [];
            private int _gets;

            /// <summary>Creates a fake with the given ready deliveries in FIFO order.</summary>
            /// <param name="ready">Initial ready set.</param>
            public FakeCleanupChannel(params StressCleanupDelivery[] ready)
            {
                _ready = new Queue<StressCleanupDelivery>(ready);
            }

            /// <summary>When non-negative, the get after this many successful gets throws.</summary>
            public int ThrowOnGetAfter { get; init; } = -1;

            /// <summary>Successful get count.</summary>
            public int GetCount => _gets;

            /// <summary>Ready messages not yet gotten.</summary>
            public int RemainingReadyCount => _ready.Count;

            /// <summary>Delivery tags left unacked (foreign holds).</summary>
            public IReadOnlyList<ulong> UnackedForeignTags => _unackedForeign;

            /// <summary>Delivery tags discarded via nack requeue=false.</summary>
            public IReadOnlyList<ulong> DiscardedTags => _discarded;

            /// <summary>Delivery tags that were incorrectly acked (must stay empty in correct scans).</summary>
            public IReadOnlyList<ulong> AckedTags => _acked;

            /// <summary>Dispose invocations.</summary>
            public int DisposeCount { get; private set; }

            /// <inheritdoc />
            public Task<StressCleanupDelivery?> BasicGetAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ThrowOnGetAfter >= 0 && _gets >= ThrowOnGetAfter)
                {
                    throw new InvalidOperationException("simulated get failure");
                }

                if (_ready.Count == 0)
                {
                    return Task.FromResult<StressCleanupDelivery?>(null);
                }

                var next = _ready.Dequeue();
                _gets++;
                // Model broker behaviour: gotten foreign stays out of ready until channel dispose requeues.
                _unackedForeign.Add(next.DeliveryTag);
                return Task.FromResult<StressCleanupDelivery?>(next);
            }

            /// <inheritdoc />
            public Task BasicNackDiscardAsync(ulong deliveryTag, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_unackedForeign.Remove(deliveryTag))
                {
                    throw new InvalidOperationException($"delivery {deliveryTag} was not unacked");
                }

                _discarded.Add(deliveryTag);
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                DisposeCount++;
                // Model broker requeue of remaining unacked foreign on channel close.
                foreach (var tag in _unackedForeign)
                {
                    _ready.Enqueue(new StressCleanupDelivery(tag, null));
                }

                _unackedForeign.Clear();
                return ValueTask.CompletedTask;
            }
        }
    }
}
