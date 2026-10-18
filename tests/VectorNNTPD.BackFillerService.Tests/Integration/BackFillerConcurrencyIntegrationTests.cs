using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.Retention;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Integration
{
    /// <summary>
    /// Pipeline concurrency: settlement, provider refresh, RabbitMQ generation, retention TTL, and shutdown ordering.
    /// </summary>
    public sealed class BackFillerConcurrencyIntegrationTests
    {
        /// <summary>Canonical valid article bytes used across concurrency scenarios.</summary>
        private static readonly byte[] Payload = ArticleWorkTestArticles.Valid();
        /// <summary>Provider connect failure requeues without ack; successful redelivery acks only the second delivery tag.</summary>

        [Fact]
        public async Task Retryable_failures_then_redelivery_acks_only_the_second_attempt()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            var channel = new FakeBackFillerRabbitMqChannel(1);

            var down = new ScriptedNntpTransportFactory { ConnectException = new IOException("down") };
            await using var downRegistry = new NntpProviderRegistry(
                harness.Catalog,
                down,
                NntpSessionOptions.Default,
                TimeSpan.FromSeconds(2),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<NntpProviderRegistry>.Instance);
            var downHandler = new ProviderArticleWorkHandler(
                new NntpArticleRetriever(downRegistry),
                harness.Retention);
            var downPipeline = new ArticleWorkDeliveryPipeline(downHandler, harness.Publisher, 1024);
            var providerFailure = await downPipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(deliveryTag: 7),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);
            Assert.Equal(ArticleWorkOutcome.ProviderFailure, providerFailure);
            Assert.False(Assert.Single(channel.Settlements).Acknowledge);
            Assert.True(channel.Settlements[0].Requeue);

            harness.EnqueueArticle(Payload);
            var success = await harness.ProcessCanonicalAsync(channel, deliveryTag: 8);
            Assert.Equal(ArticleWorkOutcome.Success, success);
            Assert.Equal(2, channel.Settlements.Count);
            Assert.True(channel.Settlements[1].Acknowledge);
            Assert.Equal(8UL, channel.Settlements[1].DeliveryTag);
            Assert.Equal(7UL, channel.Settlements[0].DeliveryTag);
            Assert.Equal(1, harness.Retention.RetainedCount);
        }
        /// <summary>Oversized retention requeues; a publish confirm failure after ingest NACK-requeues and does not ACK.</summary>

        [Fact]
        public async Task Retention_rejection_requeues_and_publish_failure_after_success_nacks()
        {
            await using var tiny = await BackFillerPipelineHarness.StartAsync();
            var oversized = ArticleWorkTestArticles.OversizedForOneMegabyteRetention();
            tiny.EnqueueArticle(oversized);
            var rejectChannel = new FakeBackFillerRabbitMqChannel(1);
            var rejected = await tiny.ProcessCanonicalAsync(rejectChannel);
            Assert.Equal(ArticleWorkOutcome.RetentionRejected, rejected);
            Assert.True(Assert.Single(rejectChannel.Settlements).Requeue);
            Assert.False(rejectChannel.Settlements[0].Acknowledge);
            Assert.Empty(tiny.PublishChannel.Publications);

            await using var publishFail = await BackFillerPipelineHarness.StartAsync(FakePublishConfirmBehavior.Nack);
            publishFail.EnqueueArticle(Payload);
            var failChannel = new FakeBackFillerRabbitMqChannel(1);
            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, await publishFail.ProcessCanonicalAsync(failChannel));
            var failedPublication = Assert.Single(failChannel.Settlements);
            Assert.False(failedPublication.Acknowledge);
            Assert.True(failedPublication.Requeue);

            await using var recovered = await BackFillerPipelineHarness.StartAsync();
            recovered.EnqueueArticle(Payload);
            var ok = new FakeBackFillerRabbitMqChannel(1);
            Assert.Equal(ArticleWorkOutcome.Success, await recovered.ProcessCanonicalAsync(ok, deliveryTag: 9));
            Assert.True(Assert.Single(ok.Settlements).Acknowledge);
        }
        /// <summary>In-flight NNTP work keeps the original pool lease while catalog refresh replaces the provider for later work.</summary>

        [Fact]
        public async Task Active_article_work_keeps_its_lease_when_the_provider_is_replaced()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = harness.EnqueueArticle(Payload, block);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var processing = harness.ProcessCanonicalAsync(channel);
            await server.ArticleStarted!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(harness.Registry.TryGetPool("Giganews", out var original));
            var replacing = harness.LoadProviderAsync(hostname: "replaced.example.test");
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => harness.Catalog.TryGetProvider("Giganews", out var provider)
                      && provider.Host == "replaced.example.test",
                TimeSpan.FromSeconds(2));
            Assert.True(harness.Registry.TryGetPool("Giganews", out var replaced));
            Assert.NotSame(original, replaced);
            Assert.Equal("replaced.example.test", replaced.Provider.Host);
            Assert.Equal(1, original.ActiveLeaseCount);

            block.TrySetResult();
            Assert.Equal(ArticleWorkOutcome.Success, await processing.WaitAsync(TimeSpan.FromSeconds(2)));
            await replacing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(Assert.Single(channel.Settlements).Acknowledge);
            Assert.Equal("news.example.test", harness.Nntp.ConnectAttempts[0].Host);

            harness.EnqueueArticle(Payload);
            var second = new FakeBackFillerRabbitMqChannel(1);
            Assert.Equal(ArticleWorkOutcome.Success, await harness.ProcessCanonicalAsync(second, deliveryTag: 8));
            Assert.Contains(harness.Nntp.ConnectAttempts, static attempt => attempt.Host == "replaced.example.test");
        }
        /// <summary>Empty account refresh during article fetch completes successfully and acks without corrupting the in-flight lease.</summary>

        [Fact]
        public async Task Provider_removal_during_active_work_does_not_corrupt_the_lease()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = harness.EnqueueArticle(Payload, block);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var processing = harness.ProcessCanonicalAsync(channel);
            await server.ArticleStarted!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            harness.Accounts.Rows = [];
            var removing = harness.AccountService.RefreshOnceAsync(CancellationToken.None);
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => !harness.Catalog.TryGetProvider("Giganews", out _),
                TimeSpan.FromSeconds(2));
            Assert.False(harness.Registry.TryGetPool("Giganews", out _));

            block.TrySetResult();
            Assert.Equal(ArticleWorkOutcome.Success, await processing.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(await removing.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(Assert.Single(channel.Settlements).Acknowledge);

            await harness.LoadProviderAsync();
            harness.EnqueueArticle(Payload);
            Assert.True(harness.Registry.TryGetPool("Giganews", out _));
            Assert.Equal(
                ArticleWorkOutcome.Success,
                await harness.ProcessCanonicalAsync(new FakeBackFillerRabbitMqChannel(1), deliveryTag: 8));
        }
        /// <summary>Failed account refresh during active retrieval retains last-known provider catalog entry until work completes.</summary>

        [Fact]
        public async Task Mysql_outage_during_active_work_keeps_last_known_good_provider()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = harness.EnqueueArticle(Payload, block);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var processing = harness.ProcessCanonicalAsync(channel);
            await server.ArticleStarted!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            harness.Accounts.QueryException = new InvalidOperationException("Provider account query failed against NntpDB.");
            Assert.False(await harness.AccountService.RefreshOnceAsync(CancellationToken.None));
            Assert.True(harness.Catalog.TryGetProvider("Giganews", out var retained));
            Assert.Equal("news.example.test", retained.Host);

            block.TrySetResult();
            Assert.Equal(ArticleWorkOutcome.Success, await processing.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(Assert.Single(channel.Settlements).Acknowledge);
        }
        /// <summary>After RabbitMQ generation advances, stale channelStillCurrent prevents ack while retention retains the article once.</summary>

        [Fact]
        public async Task Stale_consumer_generation_cannot_ack_after_a_connection_replacement()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = harness.EnqueueArticle(Payload, block);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var processing = harness.ProcessCanonicalAsync(
                channel,
                channelStillCurrent: () =>
                    harness.Connections.TryGetCurrent(out var handle)
                    && handle.Generation == 1
                    && handle.IsCurrent);

            await server.ArticleStarted!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var replaced = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.RabbitFactory.Connected = replaced;
            harness.RabbitFactory.LastConnection!.SimulateLost();
            await replaced.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => harness.Publisher.Generation == 2, TimeSpan.FromSeconds(2));
            harness.RabbitFactory.LastConnection!.DefaultPublishConfirmBehavior = FakePublishConfirmBehavior.Confirm;

            block.TrySetResult();
            var outcome = await processing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ArticleWorkOutcome.Success, outcome);
            Assert.Empty(channel.Settlements);
            Assert.Equal(1, harness.Retention.RetainedCount);

            harness.EnqueueArticle(Payload);
            var retry = new FakeBackFillerRabbitMqChannel(2);
            Assert.Equal(
                ArticleWorkOutcome.Success,
                await harness.ProcessCanonicalAsync(
                    retry,
                    deliveryTag: 8,
                    generation: 2,
                    channelStillCurrent: () =>
                        harness.Connections.TryGetCurrent(out var handle)
                        && handle.Generation == 2
                        && handle.IsCurrent));
            Assert.True(Assert.Single(retry.Settlements).Acknowledge);
            Assert.Equal(1, harness.Retention.RetainedCount);
        }
        /// <summary>Claim succeeds before TTL expiry; at expiry the message id is not found and byte accounting is zero.</summary>

        [Fact]
        public async Task Claim_before_and_after_expiry_follows_retention_ttl()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync(retentionTtl: TimeSpan.FromSeconds(10));
            var prepared = RetentionTestArticles.RetainPrepared(
                harness.Retention,
                ArticleWorkTestDeliveries.CanonicalMessageId,
                "ttl-body\r\n");

            harness.Time.Advance(TimeSpan.FromSeconds(9));
            using (var claim = ArticleRetentionAuthorityTests.Claim(
                harness.Retention,
                ArticleWorkTestDeliveries.CanonicalMessageId,
                prepared.ArtData))
            {
                Assert.True(claim.ArticleBytes.Span.SequenceEqual(prepared.ArtData.AsSpan()));
            }

            harness.Time.Advance(TimeSpan.FromSeconds(1));
            ArticleRetentionAuthorityTests.AssertNotFound(harness.Retention, ArticleWorkTestDeliveries.CanonicalMessageId);
            Assert.Equal(0, harness.Retention.RetainedCount);
            Assert.Equal(0, harness.Retention.RetainedPayloadBytes);
        }
        /// <summary>Sweep during an open delivery claim zeroes authority bytes but captured claim bytes remain valid until release.</summary>

        [Fact]
        public async Task Sweep_while_a_delivery_claim_is_held_drops_authority_bytes_and_keeps_captured_bytes()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync(retentionTtl: TimeSpan.FromSeconds(1));
            var prepared = RetentionTestArticles.RetainPrepared(
                harness.Retention,
                ArticleWorkTestDeliveries.CanonicalMessageId,
                "lease-body\r\n");
            using (var claim = ArticleRetentionAuthorityTests.Claim(
                harness.Retention,
                ArticleWorkTestDeliveries.CanonicalMessageId,
                prepared.ArtData))
            {
                harness.Time.Advance(TimeSpan.FromSeconds(1));
                Assert.Equal(prepared.Record.ArtSize, harness.Retention.SweepExpired());
                Assert.Equal(0, harness.Retention.RetainedPayloadBytes);
                Assert.Equal(0, harness.Retention.RetainedCount);
                Assert.True(claim.ArticleBytes.Span.SequenceEqual(prepared.ArtData.AsSpan()));
                ArticleRetentionAuthorityTests.AssertNotFound(harness.Retention, ArticleWorkTestDeliveries.CanonicalMessageId);
            }

            Assert.Equal(0, harness.Retention.RetainedPayloadBytes);
            Assert.Equal(0, harness.Retention.RetainedCount);
        }
        /// <summary>Cancel during NNTP article retrieval yields Cancelled outcome with requeue and no publish.</summary>

        [Fact]
        public async Task Shutdown_during_article_retrieval_does_not_ack()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = harness.EnqueueArticle(Payload, block);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            using var cts = new CancellationTokenSource();
            var processing = harness.ProcessCanonicalAsync(channel, cancellationToken: cts.Token);
            await server.ArticleStarted!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await cts.CancelAsync();
            block.TrySetResult();

            var outcome = await processing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ArticleWorkOutcome.Cancelled, outcome);
            Assert.False(Assert.Single(channel.Settlements).Acknowledge);
            Assert.True(channel.Settlements[0].Requeue);
            Assert.Empty(harness.PublishChannel.Publications);
        }
        /// <summary>Cancelled account refresh aborts without removing the last-known-good provider from the catalog.</summary>

        [Fact]
        public async Task Shutdown_during_mysql_query_and_pool_replacement_is_cancellation_aware()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            harness.Accounts.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Accounts.BlockAfterQueryCount = harness.Accounts.QueryCount + 1;
            using var queryCts = new CancellationTokenSource();
            var query = harness.AccountService.RefreshOnceAsync(queryCts.Token);
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => harness.AccountService.RefreshInProgress,
                TimeSpan.FromSeconds(2));
            await queryCts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.WaitAsync(TimeSpan.FromSeconds(2)));
            harness.Accounts.Block.TrySetResult();
            Assert.True(harness.Catalog.TryGetProvider("Giganews", out _));
        }
        /// <summary>Updating an unrelated backbone hostname reuses the existing Giganews pool instance.</summary>

        [Fact]
        public async Task Unrelated_provider_change_does_not_churn_the_active_backbone()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            harness.Accounts.Rows =
            [
                ProviderAccountTestRows.Create(),
                ProviderAccountTestRows.Create(backbone: "Eweka", hostname: "eweka.example.test"),
            ];
            Assert.True(await harness.AccountService.RefreshOnceAsync(CancellationToken.None));
            Assert.True(harness.Registry.TryGetPool("Giganews", out var giganews));

            harness.Accounts.Rows =
            [
                ProviderAccountTestRows.Create(),
                ProviderAccountTestRows.Create(backbone: "Eweka", hostname: "eweka-new.example.test"),
            ];
            Assert.True(await harness.AccountService.RefreshOnceAsync(CancellationToken.None));
            Assert.True(harness.Registry.TryGetPool("Giganews", out var still));
            Assert.Same(giganews, still);
            Assert.True(harness.Registry.TryGetPool("Eweka", out var eweka));
            Assert.Equal("eweka-new.example.test", eweka.Provider.Host);
        }
    }
}
