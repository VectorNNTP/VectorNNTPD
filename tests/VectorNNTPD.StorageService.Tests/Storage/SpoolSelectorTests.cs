using VectorNNTPD.StorageService.Storage.SpoolPersistence;

namespace VectorNNTPD.StorageService.Tests.Storage
{

    public sealed class SpoolSelectorTests
    {
        private const long Tebibyte = 1L << 40;
        private const long ArticleBytes = 768_000;

        [Fact]
        public void More_free_space_wins_when_performance_is_comparable()
        {
            var crowded = Candidate("spool-b", available: 8 * Tebibyte, active: 10);
            var roomy = Candidate("spool-a", available: 12 * Tebibyte, active: 12);
            var selected = Select(crowded, roomy);

            Assert.Equal("spool-a", selected?.SpoolRoot);
            Assert.False(selected?.Busy);
        }

        [Fact]
        public void Comparable_capacity_prefers_the_less_loaded_spool()
        {
            var busy = Candidate("spool-busy", available: 10 * Tebibyte, active: 95, writeLatency: 40);
            var light = Candidate("spool-light", available: 10 * Tebibyte + (1L << 20), active: 5, writeLatency: 1);
            Assert.Equal("spool-light", Select(busy, light)?.SpoolRoot);
            Assert.Equal("spool-light", Select(light, busy)?.SpoolRoot);
        }

        [Fact]
        public void A_busy_spool_with_materially_more_free_space_wins()
        {
            var busy = Candidate("spool-busy", available: 20 * Tebibyte, active: 95, writeLatency: 40);
            var light = Candidate("spool-light", available: 10 * Tebibyte, active: 5, writeLatency: 1);
            Assert.True(SpoolSelector.IsBusy(busy));
            Assert.Equal("spool-busy", Select(busy, light)?.SpoolRoot);
            Assert.True(Select(busy, light)?.Busy);
        }

        [Fact]
        public void Heavily_loaded_spools_are_still_selected()
        {
            var left = Candidate("left", available: 4 * Tebibyte, active: 99, writeLatency: 80, currentQueue: 40);
            var right = Candidate("right", available: 9 * Tebibyte, active: 96, writeLatency: 50, currentQueue: 20);
            var selected = Select(left, right);
            Assert.Equal("right", selected?.SpoolRoot);
            Assert.True(selected?.Busy);
        }

        [Fact]
        public void Used_space_percent_does_not_reject_a_writable_spool()
        {
            var critical = Candidate("critical", total: 100, available: 15);
            var high = Candidate("high", total: 100, available: 21);
            var low = Candidate("low", total: 100, available: 25);

            Assert.Equal(85, SpoolSelector.UsedPercent(100, 15));
            Assert.Equal(79, SpoolSelector.UsedPercent(100, 21));
            Assert.Equal(75, SpoolSelector.UsedPercent(100, 25));
            Assert.True(SpoolSelector.IsEligible(critical, 1));
            Assert.True(SpoolSelector.IsEligible(high, 1));
            Assert.True(SpoolSelector.IsEligible(low, 1));
            Assert.NotNull(SpoolSelector.Select([critical, high, low], 1));
        }

        [Fact]
        public void A_near_full_spool_with_room_for_the_article_is_not_rejected()
        {
            var near = Candidate("near", total: 100, available: 16);
            Assert.Equal(84, SpoolSelector.UsedPercent(100, 16));
            Assert.True(SpoolSelector.IsEligible(near, 16));
            Assert.False(SpoolSelector.IsEligible(near, 17));
            Assert.Equal("near", SpoolSelector.Select([near], 16)?.SpoolRoot);
        }

        [Fact]
        public void Missing_or_stale_measurements_stay_selectable()
        {
            var unknownSpace = Candidate("unknown-space", spaceKnown: false, available: 0);
            Assert.True(SpoolSelector.IsEligible(unknownSpace, ArticleBytes));
            Assert.Equal("unknown-space", SpoolSelector.Select([unknownSpace], ArticleBytes)?.SpoolRoot);

            var staleDevice = Candidate("stale-device", available: 10 * Tebibyte, deviceFresh: false, active: null, writeLatency: null);
            Assert.True(SpoolSelector.IsEligible(staleDevice, ArticleBytes));
            Assert.False(SpoolSelector.IsBusy(staleDevice));
            Assert.Equal("stale-device", SpoolSelector.Select([staleDevice], ArticleBytes)?.SpoolRoot);

            var known = Candidate("known", available: 10 * Tebibyte);
            Assert.Equal("known", Select(unknownSpace, known)?.SpoolRoot);
        }

        [Fact]
        public void A_stale_device_sample_does_not_count_as_idle_or_busy()
        {
            var staleRoomy = Candidate("stale", available: 20 * Tebibyte, deviceFresh: false, active: null);
            var freshTight = Candidate("fresh", available: 10 * Tebibyte, active: 4);
            Assert.Equal("stale", Select(freshTight, staleRoomy)?.SpoolRoot);

            var staleTied = Candidate("stale-tied", available: 10 * Tebibyte, deviceFresh: false);
            var freshTied = Candidate("fresh-tied", available: 10 * Tebibyte, active: 4);
            Assert.Equal("fresh-tied", Select(staleTied, freshTied)?.SpoolRoot);
            Assert.False(SpoolSelector.IsBusy(staleTied));
        }

        [Fact]
        public void In_flight_writes_break_a_free_space_tie_and_the_cap_skips_a_spool()
        {
            var busy = Candidate("busy", available: 10 * Tebibyte, inFlight: 2);
            var idle = Candidate("idle", available: 10 * Tebibyte + (1L << 20), inFlight: 0);
            Assert.Equal("idle", Select(busy, idle)?.SpoolRoot);

            var capped = Candidate("capped", available: 30 * Tebibyte, inFlight: 2, inFlightLimit: 2);
            Assert.False(SpoolSelector.IsEligible(capped, ArticleBytes));
            Assert.Equal("idle", Select(capped, idle)?.SpoolRoot);
        }

        [Fact]
        public void Reservations_do_not_herd_onto_one_spool()
        {
            var gate = new SpoolWriteGate();
            var left = Candidate("spool-a", available: 10 * Tebibyte);
            var right = Candidate("spool-b", available: 10 * Tebibyte);
            Assert.True(gate.TryReserve([left, right], ArticleBytes, out var first));
            Assert.True(gate.TryReserve([left, right], ArticleBytes, out var second));
            Assert.NotEqual(first.SpoolRoot, second.SpoolRoot);
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
            Assert.False(SpoolSelector.IsEligible(blocked, ArticleBytes));
            Assert.False(SpoolSelector.IsEligible(tooSmall, ArticleBytes));
            Assert.Null(SpoolSelector.Select([blocked, tooSmall], ArticleBytes));
        }

        [Fact]
        public void A_lightly_loaded_near_full_spool_beats_a_busier_one_when_capacity_is_comparable()
        {
            var busy = Candidate("a-busy", total: 100, available: 50, active: 90);
            var near = Candidate("z-near", total: 100, available: 16, active: 30);
            Assert.Equal(84, SpoolSelector.UsedPercent(100, 16));
            Assert.True(SpoolSelector.IsEligible(near, 1));
            Assert.True(SpoolSelector.IsBusy(busy));
            Assert.False(SpoolSelector.IsBusy(near));
            Assert.Equal("z-near", SpoolSelector.Select([busy, near], 1)?.SpoolRoot);
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
        public void Null_latency_is_not_treated_as_zero()
        {
            var unmeasured = Candidate("z-unmeasured", available: 10 * Tebibyte, writeLatency: null);
            var fast = Candidate("a-fast", available: 10 * Tebibyte, writeLatency: 1);
            Assert.False(SpoolSelector.IsBusy(unmeasured));
            Assert.Equal("a-fast", Select(unmeasured, fast)?.SpoolRoot);
        }

        private static SpoolSelection? Select(params SpoolCandidate[] candidates) =>
            SpoolSelector.Select(candidates, ArticleBytes);

        private static SpoolCandidate Candidate(
            string path,
            long available,
            long total = 40 * Tebibyte,
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
                "device:" + path,
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
