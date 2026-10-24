using VectorNNTPD.StorageService.Storage.SpoolPersistence;

namespace VectorNNTPD.StorageService.Tests.Storage
{
    public sealed class SpoolSelectorTests
    {
        private const long Tebibyte = 1L << 40;
        private const long ArticleBytes = 768_000;
        private const int CriticalPercent = 85;

        [Fact]
        public void Greatest_available_bytes_wins()
        {
            var crowded = Candidate("spool-b", available: 8 * Tebibyte);
            var roomy = Candidate("spool-a", available: 12 * Tebibyte);
            Assert.Equal("spool-a", Select(crowded, roomy)?.SpoolRoot);
        }

        [Fact]
        public void Available_bytes_beats_used_percent_on_different_volume_sizes()
        {
            // 50% of 100 TiB still has more bytes free than 10% of 10 TiB.
            var largeHalfFull = Candidate("large", total: 100 * Tebibyte, available: 50 * Tebibyte);
            var smallAlmostEmpty = Candidate("small", total: 10 * Tebibyte, available: 9 * Tebibyte);
            Assert.Equal("large", Select(largeHalfFull, smallAlmostEmpty)?.SpoolRoot);
        }

        [Fact]
        public void Above_critical_is_excluded()
        {
            var criticalExact = Candidate("exact", total: 100, available: 15); // 85%
            var overCritical = Candidate("over", total: 100, available: 14); // 86%
            var healthy = Candidate("ok", total: 100, available: 40);
            Assert.Equal(85, SpoolSelector.UsedPercent(100, 15));
            Assert.True(SpoolSelector.IsEligible(criticalExact, 1, CriticalPercent));
            Assert.False(SpoolSelector.IsEligible(overCritical, 1, CriticalPercent));
            Assert.Equal("ok", SpoolSelector.Select([overCritical, criticalExact, healthy], 1, CriticalPercent)?.SpoolRoot);
        }

        [Fact]
        public void Unknown_space_is_ineligible()
        {
            var unknown = Candidate("unknown-space", spaceKnown: false, available: 0);
            Assert.False(SpoolSelector.IsEligible(unknown, ArticleBytes, CriticalPercent));
            Assert.Null(SpoolSelector.Select([unknown], ArticleBytes, CriticalPercent));

            var known = Candidate("known", available: 10 * Tebibyte);
            Assert.Equal("known", Select(unknown, known)?.SpoolRoot);
        }

        [Fact]
        public void A_near_full_spool_with_room_for_the_article_is_selected_when_under_critical()
        {
            var near = Candidate("near", total: 100, available: 16); // 84%
            Assert.Equal(84, SpoolSelector.UsedPercent(100, 16));
            Assert.True(SpoolSelector.IsEligible(near, 16, CriticalPercent));
            Assert.False(SpoolSelector.IsEligible(near, 17, CriticalPercent));
            Assert.Equal("near", SpoolSelector.Select([near], 16, CriticalPercent)?.SpoolRoot);
        }

        [Fact]
        public void In_flight_writes_break_an_available_bytes_tie()
        {
            var busy = Candidate("busy", available: 10 * Tebibyte, inFlight: 2);
            var idle = Candidate("idle", available: 10 * Tebibyte, inFlight: 0);
            Assert.Equal("idle", Select(busy, idle)?.SpoolRoot);

            var capped = Candidate("capped", available: 30 * Tebibyte, inFlight: 2, inFlightLimit: 2);
            Assert.False(SpoolSelector.IsEligible(capped, ArticleBytes, CriticalPercent));
            Assert.Equal("idle", Select(capped, idle)?.SpoolRoot);
        }

        [Fact]
        public void Reservations_do_not_herd_onto_one_spool()
        {
            var gate = new SpoolWriteGate();
            var left = Candidate("spool-a", available: 10 * Tebibyte);
            var right = Candidate("spool-b", available: 10 * Tebibyte);
            Assert.True(gate.TryReserve([left, right], ArticleBytes, CriticalPercent, out var first));
            Assert.True(gate.TryReserve([left, right], ArticleBytes, CriticalPercent, out var second));
            Assert.NotEqual(first.SpoolRoot, second.SpoolRoot);
        }

        [Fact]
        public void Pending_bytes_on_shared_device_reduce_projected_capacity()
        {
            var gate = new SpoolWriteGate();
            var left = Candidate("spool-a", deviceId: "disk-1", total: 2_000_000, available: 1_000_000);
            var right = Candidate("spool-b", deviceId: "disk-1", total: 2_000_000, available: 1_000_000);
            Assert.True(gate.TryReserve([left, right], 400_000, CriticalPercent, out var first));
            Assert.True(gate.TryReserve([left, right], 400_000, CriticalPercent, out var second));
            Assert.NotEqual(first.SpoolRoot, second.SpoolRoot);
            // Raw samples still show 1_000_000 free, but shared-device pending (800_000) blocks a third 400_000 write.
            Assert.False(gate.TryReserve([left, right], 400_000, CriticalPercent, out _));
            gate.Exit(first);
            Assert.True(gate.TryReserve([left, right], 400_000, CriticalPercent, out _));
        }

        [Fact]
        public void Concurrent_same_spool_reservations_release_exact_pending_bytes()
        {
            var gate = new SpoolWriteGate();
            var spool = Candidate("spool-a", deviceId: "disk-1", total: 10_000_000, available: 5_000_000);
            Assert.True(gate.TryReserve([spool], 100_000, CriticalPercent, out var small));
            Assert.True(gate.TryReserve([spool], 700_000, CriticalPercent, out var large));
            Assert.Equal(800_000, gate.PendingBytes("disk-1"));
            // Releasing the first (smaller) lease must not pop the larger pending amount.
            gate.Exit(small);
            Assert.Equal(700_000, gate.PendingBytes("disk-1"));
            gate.Exit(large);
            Assert.Equal(0, gate.PendingBytes("disk-1"));
            // Double-exit is a no-op on pending (in-flight already zero).
            gate.Exit(large);
            Assert.Equal(0, gate.PendingBytes("disk-1"));
        }

        [Fact]
        public void Tie_break_is_ordinal_and_ignores_input_order()
        {
            var later = Candidate("spool-b", available: 10 * Tebibyte);
            var earlier = Candidate("spool-a", available: 10 * Tebibyte);
            Assert.Equal("spool-a", Select(later, earlier)?.SpoolRoot);
            Assert.Equal("spool-a", Select(earlier, later)?.SpoolRoot);
        }

        [Fact]
        public void A_blocked_spool_or_a_spool_that_cannot_fit_the_article_is_skipped()
        {
            var blocked = Candidate("down", available: 10 * Tebibyte, acceptsWrites: false);
            var tooSmall = Candidate("full", total: 100, available: 10);
            Assert.False(SpoolSelector.IsEligible(blocked, ArticleBytes, CriticalPercent));
            Assert.False(SpoolSelector.IsEligible(tooSmall, ArticleBytes, CriticalPercent));
            Assert.Null(SpoolSelector.Select([blocked, tooSmall], ArticleBytes, CriticalPercent));
        }

        [Fact]
        public void Used_percent_stays_in_range_when_the_product_would_overflow()
        {
            var total = long.MaxValue / 50;
            var available = total / 2;
            var used = SpoolSelector.UsedPercent(total, available);
            Assert.InRange(used, 49, 51);
        }

        [Fact]
        public void Equal_available_bytes_prefer_lower_write_latency_then_path()
        {
            var slow = Candidate("z-slow", available: 10 * Tebibyte, writeLatency: 40);
            var fast = Candidate("a-fast", available: 10 * Tebibyte, writeLatency: 1);
            Assert.Equal("a-fast", Select(slow, fast)?.SpoolRoot);

            var left = Candidate("a-spool", available: 10 * Tebibyte, writeLatency: 12.5);
            var right = Candidate("b-spool", available: 10 * Tebibyte, writeLatency: 12.5);
            Assert.Equal("a-spool", Select(left, right)?.SpoolRoot);
            Assert.Equal("a-spool", Select(right, left)?.SpoolRoot);
        }

        private static SpoolSelection? Select(params SpoolCandidate[] candidates) =>
            SpoolSelector.Select(candidates, ArticleBytes, CriticalPercent);

        private static SpoolCandidate Candidate(
            string path,
            long available,
            long total = 40 * Tebibyte,
            string? deviceId = null,
            double? active = 1,
            double? writeLatency = 1,
            long? currentQueue = null,
            bool spaceKnown = true,
            bool deviceFresh = true,
            bool acceptsWrites = true,
            int inFlight = 0,
            int inFlightLimit = 2) =>
            new(
                path,
                deviceId ?? ("device:" + path),
                total,
                available,
                spaceKnown,
                Initialized: true,
                acceptsWrites,
                deviceFresh,
                active,
                writeLatency,
                AverageQueueDepth: null,
                currentQueue,
                inFlight,
                inFlightLimit);
    }
}
