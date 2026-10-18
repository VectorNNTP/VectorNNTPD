using System.Text.Json;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.Retention;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Articles;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Integration
{
    /// <summary>
    /// Article work pipeline outcomes: publish, ack, retention, and response wire contract.
    /// </summary>
    public sealed class BackFillerEndToEndTests
    {
        /// <summary>Default successful article payload for end-to-end pipeline tests.</summary>
        private static readonly byte[] CanonicalPayload = ArticleWorkTestArticles.Valid();
        /// <summary>Happy path publishes v1 JSON response, acks delivery tag, retains artdata claimable for delivery.</summary>

        [Fact]
        public async Task Successful_article_work_confirms_then_acks_and_delivery_serves_artdata()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            harness.EnqueueArticle(CanonicalPayload);
            var channel = new FakeBackFillerRabbitMqChannel(1);

            var outcome = await harness.ProcessCanonicalAsync(channel);

            Assert.Equal(ArticleWorkOutcome.Success, outcome);
            Assert.Equal(ArticleRetentionKind.Retained, harness.Handler.LastRetentionKind);
            var publication = Assert.Single(harness.PublishChannel.Publications);
            using var document = JsonDocument.Parse(publication.Body);
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalRequestId, document.RootElement.GetProperty("requestId").GetString());
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalMessageId, document.RootElement.GetProperty("messageId").GetString());
            Assert.Equal("Giganews", document.RootElement.GetProperty("backbone").GetString());
            Assert.Equal("Success", document.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(harness.Handler.LastFqdn, document.RootElement.GetProperty("fqdn").GetString());
            Assert.Equal(harness.Handler.LastListenerPort, document.RootElement.GetProperty("listenerPort").GetInt32());
            Assert.False(document.RootElement.TryGetProperty("uri", out _));
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalCorrelationId, publication.CorrelationId);
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalReplyTo, publication.RoutingKey);
            Assert.Equal(ArticleWorkResponseWireProtocol.JsonContentType, publication.ContentType);
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalRequestId, publication.RequestIdHeader);
            Assert.Equal(ArticleWorkResponseWireProtocol.ExpirationMilliseconds, publication.ExpirationMilliseconds);
            var settlement = Assert.Single(channel.Settlements);
            Assert.True(settlement.Acknowledge);
            Assert.Equal(7UL, settlement.DeliveryTag);
            var articleIdHex = document.RootElement.GetProperty("articleId").GetString();
            Assert.Equal(ArticleId.HexLength, articleIdHex!.Length);
            Assert.Equal("backfiller.test", harness.Handler.LastFqdn);
            Assert.Equal(1190, harness.Handler.LastListenerPort);
            Assert.True(ArticleId.TryParseLowerHex(articleIdHex, out var articleId));
            var claimed = harness.Retention.TryClaimForDelivery(
                Encoding.ASCII.GetBytes(ArticleWorkTestDeliveries.CanonicalMessageId));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            var retained = claimed.Claim!.ArticleBytes.ToArray();
            Assert.Contains("body"u8, retained);
            Assert.Equal(ArticleId.FromMessageId(Encoding.ASCII.GetBytes(ArticleWorkTestDeliveries.CanonicalMessageId)), articleId);
            claimed.Claim.Dispose();
            Assert.Equal(retained.Length, harness.Retention.RetainedPayloadBytes);
            Assert.Equal(1, harness.Retention.RetainedCount);
        }
        /// <summary>ThrowOnPublish and publish nack NACK-requeue inbound work after successful retention. They do not ACK.</summary>

        [Fact]
        public async Task Publish_failure_and_confirm_failure_requeue_successful_work()
        {
            await using var publishFail = await BackFillerPipelineHarness.StartAsync(FakePublishConfirmBehavior.ThrowOnPublish);
            publishFail.EnqueueArticle(CanonicalPayload);
            var publishChannel = new FakeBackFillerRabbitMqChannel(1);
            var publishOutcome = await publishFail.ProcessCanonicalAsync(publishChannel);
            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, publishOutcome);
            Assert.False(Assert.Single(publishChannel.Settlements).Acknowledge);
            Assert.True(Assert.Single(publishChannel.Settlements).Requeue);

            await using var confirmFail = await BackFillerPipelineHarness.StartAsync(FakePublishConfirmBehavior.Nack);
            confirmFail.EnqueueArticle(CanonicalPayload);
            var confirmChannel = new FakeBackFillerRabbitMqChannel(1);
            var confirmOutcome = await confirmFail.ProcessCanonicalAsync(confirmChannel);
            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, confirmOutcome);
            Assert.False(Assert.Single(confirmChannel.Settlements).Acknowledge);
            Assert.True(Assert.Single(confirmChannel.Settlements).Requeue);
            Assert.Equal(
                ArticleWorkTestDeliveries.CanonicalCorrelationId,
                Assert.Single(confirmFail.PublishChannel.Publications).CorrelationId);
        }
        /// <summary>Success processing with stale channel or closed channel leaves settlements empty after confirm.</summary>

        [Fact]
        public async Task Stale_generation_or_closed_channel_after_confirm_does_not_ack()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            harness.EnqueueArticle(CanonicalPayload);
            var stale = new FakeBackFillerRabbitMqChannel(1);
            var seen = 0;
            var staleOutcome = await harness.ProcessCanonicalAsync(stale, channelStillCurrent: () => Interlocked.Increment(ref seen) == 1);
            Assert.Equal(ArticleWorkOutcome.Success, staleOutcome);
            Assert.Single(harness.PublishChannel.Publications);
            Assert.Empty(stale.Settlements);

            harness.EnqueueArticle(CanonicalPayload);
            var closed = new FakeBackFillerRabbitMqChannel(1) { IsOpen = false };
            var closedOutcome = await harness.ProcessCanonicalAsync(closed, deliveryTag: 8);
            Assert.Equal(ArticleWorkOutcome.Success, closedOutcome);
            Assert.Empty(closed.Settlements);
        }
        /// <summary>Ack exception after successful publish leaves no settlement while retention keeps the article.</summary>

        [Fact]
        public async Task Confirm_then_ack_failure_does_not_record_an_ack()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            harness.EnqueueArticle(CanonicalPayload);
            var channel = new FakeBackFillerRabbitMqChannel(1)
            {
                AckException = new InvalidOperationException("ack failed"),
            };

            var outcome = await harness.ProcessCanonicalAsync(channel);

            Assert.Equal(ArticleWorkOutcome.Success, outcome);
            Assert.Single(harness.PublishChannel.Publications);
            Assert.Empty(channel.Settlements);
            Assert.Equal(1, harness.Retention.RetainedCount);
        }
        /// <summary>Second retain for same message id is AlreadyPresent; delivery serves first body bytes.</summary>

        [Fact]
        public async Task Same_message_id_first_wins_and_delivery_keeps_original_artdata()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            var first = ArticleWorkTestArticles.Valid(body: "first-body\r\n");
            var second = ArticleWorkTestArticles.Valid(body: "second-body\r\n");
            harness.EnqueueArticle(first);
            harness.EnqueueArticle(second);
            var channel = new FakeBackFillerRabbitMqChannel(1);

            Assert.Equal(ArticleWorkOutcome.Success, await harness.ProcessCanonicalAsync(channel, deliveryTag: 7));
            var firstLength = harness.Retention.RetainedPayloadBytes;
            Assert.Equal(ArticleWorkOutcome.Success, await harness.ProcessCanonicalAsync(channel, deliveryTag: 8));
            Assert.Equal(ArticleRetentionKind.AlreadyPresent, harness.Handler.LastRetentionKind);
            Assert.Equal(firstLength, harness.Retention.RetainedPayloadBytes);
            Assert.Equal(1, harness.Retention.RetainedCount);
            Assert.Equal(2, channel.Settlements.Count);
            Assert.All(channel.Settlements, static settlement => Assert.True(settlement.Acknowledge));

            var claimed = harness.Retention.TryClaimForDelivery(
                Encoding.ASCII.GetBytes(ArticleWorkTestDeliveries.CanonicalMessageId));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            Assert.Contains("first-body"u8, claimed.Claim!.ArticleBytes.Span);
            Assert.DoesNotContain("second-body"u8, claimed.Claim.ArticleBytes.Span);
            claimed.Claim.Dispose();
            Assert.Equal(firstLength, harness.Retention.RetainedPayloadBytes);
        }
        /// <summary>Two prepared retains produce distinct ArtId values and summed byte accounting.</summary>

        [Fact]
        public async Task Distinct_message_ids_keep_independent_identities()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            var first = RetentionTestArticles.RetainPrepared(harness.Retention, "<one@example.invalid>", "payload-a\r\n");
            var second = RetentionTestArticles.RetainPrepared(harness.Retention, "<two@example.invalid>", "payload-b\r\n");
            Assert.NotEqual(first.Record.ArtId, second.Record.ArtId);
            Assert.Equal(first.Record.ArtSize + second.Record.ArtSize, harness.Retention.RetainedPayloadBytes);
            Assert.Equal(2, harness.Retention.RetainedCount);
        }
        /// <summary>Parallel RetainCanonical on one message id yields one Retained and fifteen AlreadyPresent.</summary>

        [Fact]
        public async Task Concurrent_retention_of_the_same_identity_keeps_one_owner()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            var candidates = Enumerable.Range(0, 16)
                .Select(i => RetentionTestArticles.Create(ArticleWorkTestDeliveries.CanonicalMessageId, $"body-{i}\r\n"))
                .ToArray();
            var tasks = candidates.Select(async item =>
            {
                await Task.Yield();
                return harness.Retention.RetainCanonical(
                    item.MessageId,
                    item.RequestId,
                    item.Record,
                    item.SelectedDateHeaderName);
            });
            var results = await Task.WhenAll(tasks);
            Assert.Equal(1, results.Count(static result => result.Kind == ArticleRetentionKind.Retained));
            Assert.Equal(15, results.Count(static result => result.Kind == ArticleRetentionKind.AlreadyPresent));
            Assert.Equal(1, harness.Retention.RetainedCount);
            var winner = Assert.Single(results, static result => result.Kind == ArticleRetentionKind.Retained);
            Assert.Equal(winner.RetainedPayloadBytes, harness.Retention.RetainedPayloadBytes);
        }
        /// <summary>Two request ids on one message id both cancel independently while bytes remain until unpinned.</summary>

        [Fact]
        public async Task Multiple_request_ids_for_same_article_remain_independently_openable()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            var first = RetentionTestArticles.Create("<multi-req@example.invalid>", "canonical-one\r\n");
            var second = RetentionTestArticles.Create("<multi-req@example.invalid>", "canonical-two\r\n");
            Assert.Equal(ArticleRetentionKind.Retained, harness.Retention.RetainCanonical(
                first.MessageId, first.RequestId, first.Record, first.SelectedDateHeaderName).Kind);
            Assert.Equal(ArticleRetentionKind.AlreadyPresent, harness.Retention.RetainCanonical(
                second.MessageId, second.RequestId, second.Record, second.SelectedDateHeaderName).Kind);

            using (var claim = ArticleRetentionAuthorityTests.Claim(harness.Retention, first.MessageId, first.ArtData))
            {
                Assert.True(claim.ArticleBytes.Span.SequenceEqual(first.ArtData.AsSpan()));
            }

            Assert.True(harness.Retention.TryCancelPendingRequest(first.RequestId));
            Assert.True(harness.Retention.TryCancelPendingRequest(second.RequestId));
            Assert.Equal(first.Record.ArtSize, harness.Retention.RetainedPayloadBytes);
        }
        /// <summary>After capacity pressure evicts filler, successful published work artdata remains claimable.</summary>

        [Fact]
        public async Task Published_article_work_success_survives_fifo_capacity_pressure()
        {
            await using var probe = await BackFillerPipelineHarness.StartAsync();
            probe.EnqueueArticle(CanonicalPayload);
            Assert.Equal(ArticleWorkOutcome.Success, await probe.ProcessCanonicalAsync(new FakeBackFillerRabbitMqChannel(1)));
            var successBytes = probe.Retention.RetainedPayloadBytes;
            Assert.True(successBytes > 0);
            await probe.DisposeAsync();

            var filler = RetentionTestArticles.Create("<fifo-fill@example.invalid>", "shared-body\r\n");
            var pressure = RetentionTestArticles.Create("<fifo-pres@example.invalid>", "shared-body\r\n");
            Assert.Equal(filler.Record.ArtSize, pressure.Record.ArtSize);

            await using var harness = await BackFillerPipelineHarness.StartAsync(
                maxRetainedPayloadBytes: checked((int)(successBytes + filler.Record.ArtSize)));
            harness.EnqueueArticle(CanonicalPayload);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            Assert.Equal(ArticleWorkOutcome.Success, await harness.ProcessCanonicalAsync(channel));
            Assert.Equal(ArticleRetentionKind.Retained, harness.Handler.LastRetentionKind);
            Assert.Single(harness.PublishChannel.Publications);
            Assert.True(Assert.Single(channel.Settlements).Acknowledge);

            Assert.Equal(
                ArticleRetentionKind.Retained,
                harness.Retention.RetainCanonical(
                    filler.MessageId, filler.RequestId, filler.Record, filler.SelectedDateHeaderName).Kind);
            Assert.True(harness.Retention.TryCancelPendingRequest(filler.RequestId));

            Assert.Equal(
                ArticleRetentionKind.Retained,
                harness.Retention.RetainCanonical(
                    pressure.MessageId, pressure.RequestId, pressure.Record, pressure.SelectedDateHeaderName).Kind);

            var claimed = harness.Retention.TryClaimForDelivery(
                Encoding.ASCII.GetBytes(ArticleWorkTestDeliveries.CanonicalMessageId));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            Assert.Contains("body"u8, claimed.Claim!.ArticleBytes.Span);
            claimed.Claim.Dispose();
            Assert.Equal(successBytes + pressure.Record.ArtSize, harness.Retention.RetainedPayloadBytes);
        }
        /// <summary>Processing one article work message does not increment provider account query count.</summary>

        [Fact]
        public async Task Article_work_request_path_does_not_query_mysql()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            var queries = harness.Accounts.QueryCount;
            harness.EnqueueArticle(CanonicalPayload);
            await harness.ProcessCanonicalAsync(new FakeBackFillerRabbitMqChannel(1));
            Assert.Equal(queries, harness.Accounts.QueryCount);
        }
    }
}
