using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Retention
{
    /// <summary>ArticleRetentionSweepService hosted loop: immediate sweep, failure retry, fatal exit.</summary>
    public sealed class ArticleRetentionSweepServiceTests
    {
        /// <summary>Hosted sweep runs at least once then BeginShutdown on stop.</summary>
        [Fact]
        public async Task Sweep_runs_immediately_then_stops_on_cancellation_and_closes_admission()
        {
            var authority = new RecordingRetentionAuthority();
            var sweep = new ArticleRetentionSweepService(authority, NullLogger<ArticleRetentionSweepService>.Instance);
            using var cts = new CancellationTokenSource();
            await sweep.StartAsync(cts.Token);
            await authority.Swept.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await sweep.StopAsync(CancellationToken.None);
            Assert.True(authority.ShutdownStarted);
            Assert.True(authority.SweepCount >= 1);
        }
        /// <summary>Transient sweep exception is retried; shutdown still runs.</summary>

        [Fact]
        public async Task Sweep_continues_after_a_failed_pass()
        {
            var authority = new ThrowingThenSucceedingAuthority();
            var sweep = new ArticleRetentionSweepService(authority, NullLogger<ArticleRetentionSweepService>.Instance);
            using var cts = new CancellationTokenSource();
            await sweep.StartAsync(cts.Token);
            await authority.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await sweep.StopAsync(CancellationToken.None);
            Assert.True(authority.Failures >= 1);
            Assert.True(authority.SweepCount >= 2);
            Assert.True(authority.ShutdownStarted);
        }
        /// <summary>Fatal sweep exception surfaces on ExecuteTask without retry.</summary>

        [Fact]
        public async Task Process_fatal_sweep_failure_leaves_the_hosted_loop()
        {
            var authority = new FatalSweepAuthority();
            var sweep = new ArticleRetentionSweepService(authority, NullLogger<ArticleRetentionSweepService>.Instance);

            await sweep.StartAsync(CancellationToken.None);
            var execute = sweep.ExecuteTask;
            Assert.NotNull(execute);
            var thrown = await Assert.ThrowsAsync<OutOfMemoryException>(() => execute!);

            Assert.Equal("sweep exhausted", thrown.Message);
            Assert.Equal(1, authority.SweepCount);
        }
        /// <summary>SweepInterval on authority matches configured retention sweep period.</summary>

        [Fact]
        public async Task Sweep_interval_is_the_configured_retention_interval()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 64, sweep: TimeSpan.FromSeconds(7));
            Assert.Equal(TimeSpan.FromSeconds(7), authority.SweepInterval);
            var sweep = new ArticleRetentionSweepService(authority, NullLogger<ArticleRetentionSweepService>.Instance);
            Assert.Equal(TimeSpan.FromSeconds(7), ((IArticleRetentionAuthority)authority).SweepInterval);
            await sweep.StopAsync(CancellationToken.None);
        }

        /// <summary>Records sweep invocations and signals the first sweep for hosted service tests.</summary>
        private sealed class RecordingRetentionAuthority : IArticleRetentionAuthority
        {
            /// <summary>Completes when the hosted service invokes the first <see cref="SweepExpired"/>.</summary>
            public TaskCompletionSource Swept { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Number of sweep calls observed.</summary>
            public int SweepCount { get; private set; }

            /// <summary>Whether <see cref="BeginShutdown"/> was invoked.</summary>
            public bool ShutdownStarted { get; private set; }

            /// <inheritdoc />
            public TimeSpan SweepInterval => TimeSpan.FromHours(1);

            /// <inheritdoc />
            public long RetainedPayloadBytes => 0;

            /// <inheritdoc />
            public int RetainedCount => 0;

            /// <inheritdoc />
            public ArticleRetentionResult RetainCanonical(
                string messageId,
                Guid requestId,
                VectorNNTPD.Common.Articles.ArticleRecord record,
                VectorNNTPD.Common.Articles.Parsing.NntpArticleHeaderName selectedDateHeaderName,
                string? endpointFqdn = null) =>
                new(ArticleRetentionKind.ShuttingDown, null, null, null, 0, 0);

            /// <inheritdoc />
            public ArticleDeliveryClaimResult TryClaimForDelivery(ReadOnlySpan<byte> messageIdOctets) =>
                ArticleDeliveryClaimResult.NotFound();

            /// <inheritdoc />
            public bool TryCancelPendingRequest(Guid requestId) => false;

            /// <inheritdoc />
            public long SweepExpired()
            {
                SweepCount++;
                Swept.TrySetResult();
                return 0;
            }

            /// <inheritdoc />
            public void BeginShutdown() => ShutdownStarted = true;
        }

        /// <summary>Fails the first SweepExpired call then succeeds to verify retry without stopping the loop.</summary>
        private sealed class ThrowingThenSucceedingAuthority : IArticleRetentionAuthority
        {
            /// <summary>Completes after the first successful sweep following a failure.</summary>
            public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Total sweep invocations including the failed pass.</summary>
            public int SweepCount { get; private set; }

            /// <summary>Count of sweeps that threw before succeeding.</summary>
            public int Failures { get; private set; }

            /// <summary>Whether shutdown admission was closed.</summary>
            public bool ShutdownStarted { get; private set; }

            /// <inheritdoc />
            public TimeSpan SweepInterval => TimeSpan.Zero;

            /// <inheritdoc />
            public long RetainedPayloadBytes => 0;

            /// <inheritdoc />
            public int RetainedCount => 0;

            /// <inheritdoc />
            public ArticleRetentionResult RetainCanonical(
                string messageId,
                Guid requestId,
                VectorNNTPD.Common.Articles.ArticleRecord record,
                VectorNNTPD.Common.Articles.Parsing.NntpArticleHeaderName selectedDateHeaderName,
                string? endpointFqdn = null) =>
                new(ArticleRetentionKind.ShuttingDown, null, null, null, 0, 0);

            /// <inheritdoc />
            public ArticleDeliveryClaimResult TryClaimForDelivery(ReadOnlySpan<byte> messageIdOctets) =>
                ArticleDeliveryClaimResult.NotFound();

            /// <inheritdoc />
            public bool TryCancelPendingRequest(Guid requestId) => false;

            /// <inheritdoc />
            public long SweepExpired()
            {
                SweepCount++;
                if (SweepCount == 1)
                {
                    Failures++;
                    throw new InvalidOperationException("sweep failed");
                }

                Recovered.TrySetResult();
                return 0;
            }

            /// <inheritdoc />
            public void BeginShutdown() => ShutdownStarted = true;
        }

        /// <summary>Always throws from SweepExpired to surface fatal errors on ExecuteTask.</summary>
        private sealed class FatalSweepAuthority : IArticleRetentionAuthority
        {
            /// <summary>Sweep attempts before the hosted loop faults.</summary>
            public int SweepCount { get; private set; }

            /// <inheritdoc />
            public TimeSpan SweepInterval => TimeSpan.FromHours(1);

            /// <inheritdoc />
            public long RetainedPayloadBytes => 0;

            /// <inheritdoc />
            public int RetainedCount => 0;

            /// <inheritdoc />
            public ArticleRetentionResult RetainCanonical(
                string messageId,
                Guid requestId,
                VectorNNTPD.Common.Articles.ArticleRecord record,
                VectorNNTPD.Common.Articles.Parsing.NntpArticleHeaderName selectedDateHeaderName,
                string? endpointFqdn = null) =>
                new(ArticleRetentionKind.ShuttingDown, null, null, null, 0, 0);

            /// <inheritdoc />
            public ArticleDeliveryClaimResult TryClaimForDelivery(ReadOnlySpan<byte> messageIdOctets) =>
                ArticleDeliveryClaimResult.NotFound();

            /// <inheritdoc />
            public bool TryCancelPendingRequest(Guid requestId) => false;

            /// <inheritdoc />
            public long SweepExpired()
            {
                SweepCount++;
                throw new OutOfMemoryException("sweep exhausted");
            }

            /// <inheritdoc />
            public void BeginShutdown()
            {
            }
        }
    }
}
