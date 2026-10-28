using System.Diagnostics.Metrics;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Retention
{
    /// <summary>
    /// Serializes <see cref="ArticleRetentionMetricsTests"/> so MeterListener observes a single MeterName at a time.
    /// </summary>
    [CollectionDefinition(nameof(ArticleRetentionMetricsTests), DisableParallelization = true)]
    public sealed class ArticleRetentionMetricsTestsCollection
    {
    }

    /// <summary>
    /// Observable retention gauges mirror <see cref="IArticleRetentionAuthority"/> occupancy via MeterListener.
    /// </summary>
    [Collection(nameof(ArticleRetentionMetricsTests))]
    public sealed class ArticleRetentionMetricsTests
    {
        /// <summary>Fixed clock for ManualTimeProvider.</summary>
        private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

        /// <summary>Empty authority reports zero bytes and zero entries on both gauges.</summary>
        [Fact]
        public async Task Empty_authority_observes_zero_bytes_and_zero_entries()
        {
            await using var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            using var metrics = new ArticleRetentionMetrics(authority);
            var sample = Observe();
            Assert.Equal(0L, sample.PayloadBytes);
            Assert.Equal(0L, sample.Count);
        }

        /// <summary>One retain updates both gauges to the article ArtSize and count 1.</summary>
        [Fact]
        public async Task Retain_updates_payload_bytes_and_count_gauges()
        {
            await using var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            using var metrics = new ArticleRetentionMetrics(authority);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");

            var sample = Observe();
            Assert.Equal(prepared.Record.ArtSize, sample.PayloadBytes);
            Assert.Equal(1L, sample.Count);
            Assert.Equal(authority.RetainedPayloadBytes, sample.PayloadBytes);
            Assert.Equal(authority.RetainedCount, sample.Count);
        }

        /// <summary>Two distinct Message-IDs aggregate bytes and count on the gauges.</summary>
        [Fact]
        public async Task Multiple_retains_aggregate_on_gauges()
        {
            await using var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            using var metrics = new ArticleRetentionMetrics(authority);
            var first = RetentionTestArticles.RetainPrepared(authority, "<a@b>", "one\r\n");
            var second = RetentionTestArticles.RetainPrepared(authority, "<c@d>", "two-bytes\r\n");

            var sample = Observe();
            Assert.Equal(first.Record.ArtSize + second.Record.ArtSize, sample.PayloadBytes);
            Assert.Equal(2L, sample.Count);
        }

        /// <summary>TTL expiry that clears the authority also clears both gauges.</summary>
        [Fact]
        public async Task Expiry_updates_gauges_to_zero()
        {
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            using var metrics = new ArticleRetentionMetrics(authority);
            _ = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            Assert.Equal(1L, Observe().Count);

            time.Advance(TimeSpan.FromSeconds(10));
            ArticleRetentionAuthorityTests.AssertNotFound(authority, "<a@b>");

            var sample = Observe();
            Assert.Equal(0L, sample.PayloadBytes);
            Assert.Equal(0L, sample.Count);
        }

        /// <summary>Committed ARTICLE delivery unindexes and both gauges return to zero.</summary>
        [Fact]
        public async Task Commit_delivery_clears_gauges()
        {
            await using var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            using var metrics = new ArticleRetentionMetrics(authority);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            using (var claim = ArticleRetentionAuthorityTests.Claim(authority, "<a@b>", prepared.ArtData))
            {
                claim.Commit();
            }

            var sample = Observe();
            Assert.Equal(0L, sample.PayloadBytes);
            Assert.Equal(0L, sample.Count);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.RetainedCount);
        }

        /// <summary>Repeated SweepExpired after empty retention does not drive gauges negative.</summary>
        [Fact]
        public async Task Repeated_sweep_on_empty_authority_stays_at_zero()
        {
            await using var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            using var metrics = new ArticleRetentionMetrics(authority);
            Assert.Equal(0, authority.SweepExpired());
            Assert.Equal(0, authority.SweepExpired());

            var sample = Observe();
            Assert.Equal(0L, sample.PayloadBytes);
            Assert.Equal(0L, sample.Count);
        }

        /// <summary>Concurrent retains and gauge observes stay non-negative and match authority totals.</summary>
        [Fact]
        public async Task Concurrent_retain_and_observe_stay_consistent_with_authority()
        {
            await using var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 16 * 1024 * 1024);
            using var metrics = new ArticleRetentionMetrics(authority);
            var barrier = new Barrier(4);
            var tasks = new Task[3];
            for (var i = 0; i < tasks.Length; i++)
            {
                var index = i;
                tasks[index] = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    for (var n = 0; n < 20; n++)
                    {
                        _ = RetentionTestArticles.RetainPrepared(
                            authority,
                            $"<{index}-{n}@metrics.test>",
                            $"body-{index}-{n}\r\n");
                    }
                });
            }

            var observeTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (var n = 0; n < 40; n++)
                {
                    // During concurrent retains, gauges and a later property read are not one
                    // atomic snapshot; only require non-negative observations mid-flight.
                    var sample = Observe();
                    Assert.True(sample.PayloadBytes >= 0);
                    Assert.True(sample.Count >= 0);
                }
            });

            await Task.WhenAll(tasks.Append(observeTask));

            // After writers finish, gauges must match the authority (source of truth).
            var final = Observe();
            Assert.Equal(60L, final.Count);
            Assert.Equal(authority.RetainedPayloadBytes, final.PayloadBytes);
            Assert.Equal(authority.RetainedCount, final.Count);
        }

        /// <summary>Dispose is idempotent; a replacement metrics instance observes the same authority again.</summary>
        [Fact]
        public async Task Dispose_is_idempotent_and_replacement_observes_again()
        {
            await using var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var metrics = new ArticleRetentionMetrics(authority);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            metrics.Dispose();
            metrics.Dispose();

            using var replacement = new ArticleRetentionMetrics(authority);
            var sample = Observe();
            Assert.Equal(prepared.Record.ArtSize, sample.PayloadBytes);
            Assert.Equal(1L, sample.Count);
        }

        /// <summary>Null authority is rejected.</summary>
        [Fact]
        public void Constructor_rejects_null_authority()
        {
            Assert.Throws<ArgumentNullException>(static () => new ArticleRetentionMetrics(null!));
        }

        /// <summary>Records one observe pass for both retention instruments.</summary>
        private static (long PayloadBytes, long Count) Observe()
        {
            var (listener, holder) = StartListening();
            using (listener)
            {
                listener.RecordObservableInstruments();
                Assert.True(holder.PayloadBytes.HasValue);
                Assert.True(holder.Count.HasValue);
                return (holder.PayloadBytes.GetValueOrDefault(), holder.Count.GetValueOrDefault());
            }
        }

        /// <summary>Creates a started listener for the article-retention meter instruments.</summary>
        private static (MeterListener Listener, MeasurementHolder Holder) StartListening()
        {
            var holder = new MeasurementHolder();
            var listener = new MeterListener();
            listener.InstrumentPublished = static (instrument, meterListener) =>
            {
                if (instrument.Meter.Name != ArticleRetentionMetrics.MeterName)
                {
                    return;
                }

                if (instrument.Name is ArticleRetentionMetrics.RetainedPayloadBytesInstrumentName
                    or ArticleRetentionMetrics.RetainedCountInstrumentName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            {
                if (instrument.Name == ArticleRetentionMetrics.RetainedPayloadBytesInstrumentName)
                {
                    holder.PayloadBytes = measurement;
                }
                else if (instrument.Name == ArticleRetentionMetrics.RetainedCountInstrumentName)
                {
                    holder.Count = measurement;
                }
            });
            listener.Start();
            return (listener, holder);
        }

        /// <summary>Holds the last observed gauge values for one MeterListener session.</summary>
        private sealed class MeasurementHolder
        {
            /// <summary>Last <see cref="ArticleRetentionMetrics.RetainedPayloadBytesInstrumentName"/> sample.</summary>
            public long? PayloadBytes;

            /// <summary>Last <see cref="ArticleRetentionMetrics.RetainedCountInstrumentName"/> sample.</summary>
            public long? Count;
        }
    }
}
