using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using System.Text;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork
{
    /// <summary>
    /// Provider retrieval buffer ownership: canonical materialization must copy bytes before the NNTP buffer is reused.
    /// </summary>
    public sealed class ProviderArticleWorkOwnershipTests
    {
        /// <summary>
        /// Retained canonical bytes remain valid after the retriever mutates the source array post-callback; retention uses a distinct buffer.
        /// </summary>
        [Fact]
        public async Task CanonicalArtData_RemainsValidAfterTheSourceBufferIsReused()
        {
            var pristine = ArticleWorkTestArticles.Valid();
            var source = pristine.ToArray();
            var parser = new NntpArticleParser("backfiller.test");
            var expected = ArticleRecordFactory.TryCreate(parser, pristine, ArticlePathMode.Traverse);
            Assert.True(expected.IsAccepted);
            var expectedBytes = expected.Record.ArtData.ToArray();
            await using var retention = CreateRetention();
            var retriever = new OwnedBufferRetriever(source, overwriteAfterCallback: true);
            var handler = new ProviderArticleWorkHandler(retriever, retention, parser);

            var requestId = Guid.NewGuid();
            var result = await handler.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId, requestId), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
            Assert.Null(result.Article);
            var claimed = retention.TryClaimForDelivery(Encoding.ASCII.GetBytes(ArticleWorkTestDeliveries.CanonicalMessageId));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            Assert.True(MemoryMarshal.TryGetArray(claimed.Claim!.ArticleBytes, out var retained));
            Assert.NotNull(retained.Array);
            Assert.NotSame(source, retained.Array);
            Assert.Equal(expectedBytes.Length, retained.Count);
            Assert.True(retained.Array.AsSpan(0, retained.Count).SequenceEqual(expectedBytes));
            Assert.All(source, static value => Assert.Equal(0xFF, value));
            claimed.Claim.Dispose();
            Assert.Equal(ArticleRetentionKind.Retained, handler.LastRetentionKind);
            Assert.Equal("backfiller.test", handler.LastFqdn);
            Assert.Equal(563, handler.LastListenerPort);
            Assert.True(retriever.Invoked);
        }

        /// <summary>
        /// Message-id mismatch is invalid-article without retention even when the source buffer is overwritten after parse.
        /// </summary>
        [Fact]
        public async Task MessageIdMismatch_UsesTheCanonicalRecordAfterTheSourceIsReused()
        {
            var source = ArticleWorkTestArticles.Valid();
            var parser = new NntpArticleParser("backfiller.test");
            await using var retention = CreateRetention();
            var retriever = new OwnedBufferRetriever(source, overwriteAfterCallback: true);
            var handler = new ProviderArticleWorkHandler(retriever, retention, parser);

            var result = await handler.HandleAsync(Item("<other@example.invalid>"), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.InvalidArticle, result.Outcome);
            Assert.Equal("MessageIdMismatch", result.Error);
            Assert.All(source, static value => Assert.Equal(0xFF, value));
            Assert.Equal(0, retention.RetainedCount);
        }

        /// <summary>
        /// Not-found and cancelled retrieval skip the payload callback entirely (Invoked remains false).
        /// </summary>
        [Fact]
        public async Task FailureAndCancellation_DoNotRequireThePayloadCallback()
        {
            await using var retention = CreateRetention();
            var notFound = new OwnedBufferRetriever(
                [],
                failure: ArticleRetrievalResult.Failed(
                    ArticleRetrievalKind.ArticleNotFound,
                    430,
                    "no such article",
                    sessionReusable: true));
            var handler = new ProviderArticleWorkHandler(notFound, retention, new NntpArticleParser("backfiller.test"));

            var missing = await handler.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId), CancellationToken.None);
            Assert.Equal(ArticleWorkOutcome.ArticleNotFound, missing.Outcome);
            Assert.False(notFound.Invoked);

            var cancelledRetriever = new OwnedBufferRetriever(ArticleWorkTestArticles.Valid());
            var cancelledHandler = new ProviderArticleWorkHandler(
                cancelledRetriever,
                retention,
                new NntpArticleParser("backfiller.test"));
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            var cancelled = await cancelledHandler.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId), cts.Token);
            Assert.Equal(ArticleWorkOutcome.Cancelled, cancelled.Outcome);
            Assert.False(cancelledRetriever.Invoked);
        }

        /// <summary>
        /// In-memory retention authority with test listener metadata for handler ownership tests.
        /// </summary>
        /// <returns><see cref="ArticleRetentionAuthority"/> disposed by callers.</returns>
        private static ArticleRetentionAuthority CreateRetention() =>
            new(
                new BackFillerArticleRetentionRuntimeOptions(1024 * 1024, TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), 256),
                "backfiller.test",
                563,
                TimeProvider.System,
                NullLogger<ArticleRetentionAuthority>.Instance);

        /// <summary>
        /// Minimal work item with fake settlement lease (not asserting broker settlement here).
        /// </summary>
        /// <param name="messageId">Requested message-id.</param>
        /// <param name="requestId">Optional request guid; random when null.</param>
        /// <returns><see cref="ArticleWorkItem"/> for handler calls.</returns>
        private static ArticleWorkItem Item(string messageId, Guid? requestId = null)
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            return new ArticleWorkItem(
                new ArticleWorkRequest(1, requestId ?? Guid.NewGuid(), messageId, "audit"),
                "corr",
                "reply",
                new ArticleWorkSettlementLease(channel, 1, 1));
        }

        /// <summary>
        /// Simulates provider buffer reuse after <c>consumePayload</c> returns. Does not perform real NNTP or honor cancellation when failing fast.
        /// </summary>
        private sealed class OwnedBufferRetriever : INntpArticleRetriever
        {
            /// <summary>Mutable buffer passed to the payload callback.</summary>
            private readonly byte[] _payload;

            /// <summary>When set, returned immediately without invoking the callback.</summary>
            private readonly ArticleRetrievalResult? _failure;

            /// <summary>When true, fills payload with 0xFF after callback to simulate in-place reuse.</summary>
            private readonly bool _overwriteAfterCallback;

            /// <summary>
            /// Configures payload buffer, optional post-callback overwrite, or immediate failure result.
            /// </summary>
            /// <param name="payload">Article bytes supplied to the handler callback.</param>
            /// <param name="overwriteAfterCallback">Whether to poison the buffer after callback returns.</param>
            /// <param name="failure">Optional failure result bypassing payload delivery.</param>
            internal OwnedBufferRetriever(
                byte[] payload,
                bool overwriteAfterCallback = false,
                ArticleRetrievalResult? failure = null)
            {
                _payload = payload;
                _overwriteAfterCallback = overwriteAfterCallback;
                _failure = failure;
            }

            /// <summary>True after the payload callback runs (false for immediate failure paths).</summary>
            public bool Invoked { get; private set; }

            /// <summary>
            /// Delivers payload to <paramref name="consumePayload"/> unless constructed with a failure; may overwrite buffer afterward.
            /// </summary>
            /// <param name="item">Work item (not validated by this fake).</param>
            /// <param name="maxArticleBytes">Ignored.</param>
            /// <param name="consumePayload">Parser/materialization callback.</param>
            /// <param name="cancellationToken">Not observed before callback; cancellation tests use handler-level cancellation.</param>
            /// <returns>Retrieved or failed result synchronously.</returns>
            public Task<ArticleRetrievalResult> RetrieveAsync(
                ArticleWorkItem item,
                int maxArticleBytes,
                Func<ReadOnlyMemory<byte>, ArticleRecordCreateResult> consumePayload,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(consumePayload);
                if (_failure is not null)
                {
                    return Task.FromResult(_failure);
                }

                Invoked = true;
                _ = consumePayload(_payload);
                if (_overwriteAfterCallback)
                {
                    _payload.AsSpan().Fill(0xFF);
                }

                return Task.FromResult(ArticleRetrievalResult.Retrieved(220, "article follows"));
            }
        }
    }
}
