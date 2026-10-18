using System.Text.Json;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using System.Text;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork
{
    /// <summary>
    /// End-to-end article-work pipeline through parse, canonical materialization, retention, response publication, and RabbitMQ settlement.
    /// </summary>
    public sealed class ArticleWorkArticlePipelineIntegrationTests
    {
        /// <summary>
        /// Local FQDN used when asserting canonical Path rewriting in retained articles.
        /// </summary>
        private const string LocalFqdn = "backfiller01.usenet.ninja";

        /// <summary>
        /// Valid plain article is retained with canonical headers, success JSON is published, and the delivery is acked without requeue.
        /// </summary>
        [Fact]
        public async Task Valid_normal_article_parses_materializes_retains_publishes_and_acks()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            harness.EnqueueArticle(ArticleWorkTestArticles.Valid());
            var channel = new FakeBackFillerRabbitMqChannel(1);

            var outcome = await harness.ProcessCanonicalAsync(channel);

            Assert.Equal(ArticleWorkOutcome.Success, outcome);
            Assert.Equal(ArticleRetentionKind.Retained, harness.Handler.LastRetentionKind);
            Assert.Equal(1, harness.Retention.RetainedCount);
            Assert.True(Assert.Single(channel.Settlements).Acknowledge);
            var publication = Assert.Single(harness.PublishChannel.Publications);
            using var document = JsonDocument.Parse(publication.Body);
            Assert.Equal("Success", document.RootElement.GetProperty("outcome").GetString());
            AssertCanonicalRetainedArticle(ReadRetainedArticle(harness));
        }

        /// <summary>
        /// Valid yEnc payload is validated on the destuffed body, retained in encoded form, and settled as success without requeue.
        /// </summary>
        [Fact]
        public async Task Valid_yenc_article_validates_materializes_retains_encoded_body_and_acks()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            harness.EnqueueArticle(ArticleWorkTestArticles.ValidYEnc());
            var channel = new FakeBackFillerRabbitMqChannel(1);

            var outcome = await harness.ProcessCanonicalAsync(channel);

            Assert.Equal(ArticleWorkOutcome.Success, outcome);
            Assert.Equal(ArticleRetentionKind.Retained, harness.Handler.LastRetentionKind);
            Assert.True(Assert.Single(channel.Settlements).Acknowledge);
            var retained = ReadRetainedArticle(harness);
            Assert.Contains("=ybegin "u8, retained);
            Assert.Contains("=yend "u8, retained);
        }

        /// <summary>
        /// Invalid Date header is a terminal <see cref="ArticleWorkOutcome.InvalidArticle"/> with ack, no requeue, and no retention.
        /// </summary>
        [Fact]
        public async Task Invalid_date_acks_without_requeue_or_retain()
            => await AssertInvalidArticleAsync(ArticleWorkTestArticles.InvalidDate());

        /// <summary>
        /// Invalid required headers yield terminal invalid-article settlement without retention or requeue.
        /// </summary>
        [Fact]
        public async Task Invalid_headers_acks_without_requeue_or_retain()
            => await AssertInvalidArticleAsync(ArticleWorkTestArticles.InvalidHeaders());

        /// <summary>
        /// Wire message-id that fails parser validation is terminal invalid-article with ack and no retain.
        /// </summary>
        [Fact]
        public async Task Invalid_message_id_acks_without_requeue_or_retain()
            => await AssertInvalidArticleAsync(ArticleWorkTestArticles.InvalidMessageId());

        /// <summary>
        /// Invalid Newsgroups is terminal invalid-article settlement without requeue or retention bytes.
        /// </summary>
        [Fact]
        public async Task Invalid_newsgroups_acks_without_requeue_or_retain()
            => await AssertInvalidArticleAsync(ArticleWorkTestArticles.InvalidNewsgroups());

        /// <summary>
        /// Invalid Path header rejects before retention; delivery acks without requeue.
        /// </summary>
        [Fact]
        public async Task Invalid_path_acks_without_requeue_or_retain()
            => await AssertInvalidArticleAsync(ArticleWorkTestArticles.InvalidPath());

        /// <summary>
        /// yEnc CRC failure is invalid-article terminal outcome with ack and published failure JSON, not success.
        /// </summary>
        [Fact]
        public async Task Bad_yenc_crc_acks_without_requeue_or_retain()
            => await AssertInvalidArticleAsync(ArticleWorkTestArticles.BadYEncCrc());

        /// <summary>
        /// Invalid yEnc escape sequences fail validation as invalid-article without retain or requeue.
        /// </summary>
        [Fact]
        public async Task Invalid_yenc_escape_acks_without_requeue_or_retain()
            => await AssertInvalidArticleAsync(ArticleWorkTestArticles.InvalidYEncEscape());

        /// <summary>
        /// Canonical Path rewrite that exceeds line limits fails materialization as invalid-article with terminal ack settlement.
        /// </summary>
        [Fact]
        public async Task Canonical_materialization_exceeding_line_limit_acks_without_requeue_or_retain()
            => await AssertInvalidArticleAsync(ArticleWorkTestArticles.PathRewriteExceedsLineLimit(LocalFqdn));

        /// <summary>
        /// Retrieved Message-ID must match the request; mismatch publishes InvalidArticle (not Success) and acks without retain.
        /// </summary>
        [Fact]
        public async Task Article_message_id_mismatch_acks_without_requeue_retain_or_success_response()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            harness.EnqueueArticle(ArticleWorkTestArticles.Valid("<actual-provider-identity@example.com>"));
            var channel = new FakeBackFillerRabbitMqChannel(1);

            var outcome = await harness.ProcessCanonicalAsync(channel);

            Assert.Equal(ArticleWorkOutcome.InvalidArticle, outcome);
            Assert.Null(harness.Handler.LastRetentionKind);
            Assert.Equal(0, harness.Retention.RetainedCount);
            var settlement = Assert.Single(channel.Settlements);
            Assert.True(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
            var publication = Assert.Single(harness.PublishChannel.Publications);
            using var document = JsonDocument.Parse(publication.Body);
            Assert.Equal("InvalidArticle", document.RootElement.GetProperty("outcome").GetString());
            Assert.NotEqual("Success", document.RootElement.GetProperty("outcome").GetString());
        }

        /// <summary>
        /// Provider connect failure is retryable: nack with requeue, no terminal response publication, no retention.
        /// </summary>
        [Fact]
        public async Task Provider_network_failure_nacks_with_requeue()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            harness.Nntp.ConnectException = new IOException("down");
            var channel = new FakeBackFillerRabbitMqChannel(1);

            var outcome = await harness.ProcessCanonicalAsync(channel);

            Assert.Equal(ArticleWorkOutcome.ProviderFailure, outcome);
            Assert.Equal(0, harness.Retention.RetainedCount);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
            Assert.Empty(harness.PublishChannel.Publications);
        }

        /// <summary>
        /// Article bytes above <c>MaxArticleBytes</c> abort receive as invalid-article: ack, terminal JSON without success endpoint fields, no retain.
        /// </summary>
        [Fact]
        public async Task Oversized_article_acks_as_invalid_article_without_requeue_retain_or_success()
        {
            const int maxArticleBytes = 64;
            await using var harness = await BackFillerPipelineHarness.StartAsync(maxArticleBytes: maxArticleBytes);
            // Destuffed payload larger than MaxArticleBytes; receive aborts before terminator.
            var oversized = Encoding.ASCII.GetBytes(
                $"Date: {ArticleWorkTestArticles.ValidDate}\r\n"
                + $"Message-ID: {ArticleWorkTestDeliveries.CanonicalMessageId}\r\n"
                + "Newsgroups: alt.test\r\n"
                + "From: user@example.test\r\n"
                + "\r\n"
                + new string('x', maxArticleBytes));
            Assert.True(oversized.Length > maxArticleBytes);
            harness.EnqueueArticle(oversized);
            var channel = new FakeBackFillerRabbitMqChannel(1);

            var outcome = await harness.ProcessCanonicalAsync(channel);

            Assert.Equal(ArticleWorkOutcome.InvalidArticle, outcome);
            Assert.Equal(ArticleRetrievalKind.InvalidArticle, harness.Handler.LastKind);
            Assert.Null(harness.Handler.LastRetentionKind);
            Assert.Equal(0, harness.Retention.RetainedCount);
            var settlement = Assert.Single(channel.Settlements);
            Assert.True(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
            var publication = Assert.Single(harness.PublishChannel.Publications);
            using var document = JsonDocument.Parse(publication.Body);
            Assert.Equal("InvalidArticle", document.RootElement.GetProperty("outcome").GetString());
            Assert.NotEqual("Success", document.RootElement.GetProperty("outcome").GetString());
            Assert.False(document.RootElement.TryGetProperty("uri", out _));
        }

        /// <summary>
        /// Oversized payload may retire the NNTP session yet a subsequent valid article on a new delivery still succeeds and retains.
        /// </summary>
        [Fact]
        public async Task Oversized_article_retires_session_but_pool_still_serves_later_work()
        {
            // Ceiling must remain above a normal Valid() article (~130+ bytes) so only the
            // deliberately oversized first payload trips InvalidArticle.
            const int maxArticleBytes = 256;
            await using var harness = await BackFillerPipelineHarness.StartAsync(maxArticleBytes: maxArticleBytes);
            var oversized = Encoding.ASCII.GetBytes(
                $"Date: {ArticleWorkTestArticles.ValidDate}\r\n"
                + $"Message-ID: {ArticleWorkTestDeliveries.CanonicalMessageId}\r\n"
                + "Newsgroups: alt.test\r\n"
                + "From: user@example.test\r\n"
                + "\r\n"
                + new string('x', maxArticleBytes));
            Assert.True(oversized.Length > maxArticleBytes);
            Assert.True(ArticleWorkTestArticles.Valid().Length <= maxArticleBytes);
            harness.EnqueueArticle(oversized);
            var firstChannel = new FakeBackFillerRabbitMqChannel(1);
            Assert.Equal(ArticleWorkOutcome.InvalidArticle, await harness.ProcessCanonicalAsync(firstChannel));
            Assert.True(Assert.Single(firstChannel.Settlements).Acknowledge);

            harness.EnqueueArticle(ArticleWorkTestArticles.Valid());
            var secondChannel = new FakeBackFillerRabbitMqChannel(1);
            Assert.Equal(
                ArticleWorkOutcome.Success,
                await harness.ProcessCanonicalAsync(secondChannel, deliveryTag: 8, generation: 1));
            Assert.True(Assert.Single(secondChannel.Settlements).Acknowledge);
            Assert.Equal(1, harness.Retention.RetainedCount);
        }

        /// <summary>
        /// A retained article is not ACKed when the terminal reply throws before publication. The delivery NACK-requeues.
        /// </summary>
        [Fact]
        public async Task Successful_article_requeues_when_terminal_reply_fails_before_publish()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync(FakePublishConfirmBehavior.ThrowOnPublish);
            harness.EnqueueArticle(ArticleWorkTestArticles.Valid());
            var channel = new FakeBackFillerRabbitMqChannel(1);

            var outcome = await harness.ProcessCanonicalAsync(channel);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.Equal(ArticleRetentionKind.Retained, harness.Handler.LastRetentionKind);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        }

        /// <summary>
        /// Redelivery after a failed terminal reply publishes once the confirm succeeds and does not retain a second copy.
        /// </summary>
        [Fact]
        public async Task Redelivery_after_failed_terminal_publish_keeps_one_retained_article()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync(FakePublishConfirmBehavior.ThrowOnPublish);
            harness.EnqueueArticle(ArticleWorkTestArticles.Valid());
            var first = new FakeBackFillerRabbitMqChannel(1);
            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, await harness.ProcessCanonicalAsync(first));
            Assert.Equal(1, harness.Retention.RetainedCount);
            Assert.Empty(harness.PublishChannel.Publications);

            harness.EnqueueArticle(ArticleWorkTestArticles.Valid());
            harness.PublishChannel.ConfirmBehavior = FakePublishConfirmBehavior.Confirm;
            var second = new FakeBackFillerRabbitMqChannel(1);
            Assert.Equal(ArticleWorkOutcome.Success, await harness.ProcessCanonicalAsync(second, deliveryTag: 8));
            Assert.True(Assert.Single(second.Settlements).Acknowledge);
            Assert.False(Assert.Single(first.Settlements).Acknowledge);
            Assert.Equal(1, harness.Retention.RetainedCount);
            Assert.Equal(
                ArticleWorkTestDeliveries.CanonicalCorrelationId,
                Assert.Single(harness.PublishChannel.Publications).CorrelationId);
        }

        /// <summary>
        /// Interior bare LF makes the article not frameable: invalid-article terminal settlement with explicit error, no success outcome.
        /// </summary>
        [Fact]
        public async Task Interior_bare_lf_is_invalid_article_and_is_not_retained_or_reported_as_success()
        {
            await AssertInvalidArticleAsync(ArticleWorkTestArticles.Valid(body: "plain\nmid.dot\r\n"), "NotFrameable");
        }

        /// <summary>
        /// Canonical retention rewrites Date and Path (single tracker insertion) while preserving the logical body bytes.
        /// </summary>
        [Fact]
        public async Task Canonical_retained_article_rewrites_date_and_path_and_keeps_body()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            harness.EnqueueArticle(ArticleWorkTestArticles.Valid(body: "unchanged-body\r\n", extraHeaders: "Path: news.example.org!feed2\r\n"));
            var channel = new FakeBackFillerRabbitMqChannel(1);

            Assert.Equal(ArticleWorkOutcome.Success, await harness.ProcessCanonicalAsync(channel));
            var retained = ReadRetainedArticle(harness);
            var text = Encoding.ASCII.GetString(retained);
            Assert.Contains($"Date: {ArticleWorkTestArticles.CanonicalUtcDate}\r\n", text, StringComparison.Ordinal);
            Assert.Contains($"Path: news.usenet.ninja!{LocalFqdn}!news.example.org!feed2", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Path: news.example.org!feed2\r\n", text, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(text, "news.usenet.ninja"));
            Assert.Contains("unchanged-body", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// Shared invalid-article invariant: ack without requeue, zero retention, published InvalidArticle JSON (optional error field).
        /// </summary>
        /// <param name="destuffed">Provider article bytes enqueued on the harness NNTP fake.</param>
        /// <param name="error">When set, expected <c>error</c> property in the response JSON.</param>
        private static async Task AssertInvalidArticleAsync(byte[] destuffed, string? error = null)
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            harness.EnqueueArticle(destuffed);
            var channel = new FakeBackFillerRabbitMqChannel(1);

            var outcome = await harness.ProcessCanonicalAsync(channel);

            Assert.Equal(ArticleWorkOutcome.InvalidArticle, outcome);
            Assert.Null(harness.Handler.LastRetentionKind);
            Assert.Equal(0, harness.Retention.RetainedCount);
            Assert.Equal(0, harness.Retention.RetainedPayloadBytes);
            var settlement = Assert.Single(channel.Settlements);
            Assert.True(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
            var publication = Assert.Single(harness.PublishChannel.Publications);
            using var document = JsonDocument.Parse(publication.Body);
            Assert.Equal("InvalidArticle", document.RootElement.GetProperty("outcome").GetString());
            if (error is not null)
            {
                Assert.Equal(error, document.RootElement.GetProperty("error").GetString());
            }
        }

        /// <summary>
        /// Asserts retained bytes include canonical Date/Path, one tracker hop, parse acceptance, and expected body marker.
        /// </summary>
        /// <param name="retained">Stuffed canonical article bytes read from retention.</param>
        private static void AssertCanonicalRetainedArticle(byte[]? retained)
        {
            Assert.NotNull(retained);
            var text = Encoding.ASCII.GetString(retained);
            Assert.Contains($"Date: {ArticleWorkTestArticles.CanonicalUtcDate}\r\n", text, StringComparison.Ordinal);
            Assert.Contains($"Path: news.usenet.ninja!{LocalFqdn}", text, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(text, "news.usenet.ninja"));
            Assert.Contains("body", text, StringComparison.Ordinal);
            var parser = new NntpArticleParser(LocalFqdn);
            var parse = parser.Parse(retained);
            Assert.True(parse.IsAccepted);
        }

        /// <summary>
        /// Path traversal prepends the local hop once when the incoming Path already starts with the site tracker.
        /// </summary>
        [Fact]
        public async Task First_traversal_does_not_duplicate_existing_leading_tracker()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            harness.EnqueueArticle(ArticleWorkTestArticles.Valid(extraHeaders: "Path: news.usenet.ninja!foo\r\n"));
            var channel = new FakeBackFillerRabbitMqChannel(1);

            Assert.Equal(ArticleWorkOutcome.Success, await harness.ProcessCanonicalAsync(channel));
            var text = Encoding.ASCII.GetString(ReadRetainedArticle(harness));
            Assert.Contains($"Path: {LocalFqdn}!news.usenet.ninja!foo", text, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(text, "news.usenet.ninja"));
        }

        /// <summary>
        /// Path traversal does not duplicate a tracker token that appears later in the upstream Path chain.
        /// </summary>
        [Fact]
        public async Task First_traversal_does_not_duplicate_tracker_that_occurs_later()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            harness.EnqueueArticle(ArticleWorkTestArticles.Valid(extraHeaders: "Path: foo!news.usenet.ninja!bar\r\n"));
            var channel = new FakeBackFillerRabbitMqChannel(1);

            Assert.Equal(ArticleWorkOutcome.Success, await harness.ProcessCanonicalAsync(channel));
            var text = Encoding.ASCII.GetString(ReadRetainedArticle(harness));
            Assert.Contains($"Path: {LocalFqdn}!foo!news.usenet.ninja!bar", text, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(text, "news.usenet.ninja"));
        }

        /// <summary>
        /// Loads retained stuffed bytes via success response <c>articleId</c> and a delivery claim on the canonical message-id.
        /// </summary>
        /// <param name="harness">Running pipeline harness with retention and publish channel.</param>
        /// <returns>Owned copy of retained article bytes (claim disposed before return).</returns>
        private static byte[] ReadRetainedArticle(BackFillerPipelineHarness harness)
        {
            var publication = Assert.Single(harness.PublishChannel.Publications);
            using var document = JsonDocument.Parse(publication.Body);
            var articleIdHex = document.RootElement.GetProperty("articleId").GetString();
            Assert.True(ArticleId.TryParseLowerHex(articleIdHex, out _));
            var claimed = harness.Retention.TryClaimForDelivery(
                Encoding.ASCII.GetBytes(ArticleWorkTestDeliveries.CanonicalMessageId));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            var bytes = claimed.Claim!.ArticleBytes.ToArray();
            claimed.Claim.Dispose();
            return bytes;
        }

        /// <summary>
        /// Counts non-overlapping ordinal occurrences of <paramref name="value"/> in <paramref name="source"/>.
        /// </summary>
        /// <param name="source">Text to scan (for example retained Path header).</param>
        /// <param name="value">Substring to count.</param>
        /// <returns>Number of matches.</returns>
        private static int CountOccurrences(string source, string value)
        {
            var count = 0;
            var index = 0;
            while (true)
            {
                index = source.IndexOf(value, index, StringComparison.Ordinal);
                if (index < 0)
                {
                    return count;
                }

                count++;
                index += value.Length;
            }
        }
    }
}
