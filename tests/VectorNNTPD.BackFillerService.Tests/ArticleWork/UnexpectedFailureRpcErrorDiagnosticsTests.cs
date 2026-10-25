using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork
{
    /// <summary>
    /// Regression for UnexpectedFailure error text when a handler exception escapes classification.
    /// Handler UnexpectedFailure ACKs and publishes a correlated failure reply (not broker requeue).
    /// </summary>
    public sealed class UnexpectedFailureRpcErrorDiagnosticsTests
    {
        /// <summary>
        /// A thrown exception with a useful message yields type+message on the published UnexpectedFailure reply.
        /// </summary>
        [Fact]
        public async Task Escaped_handler_exception_preserves_type_and_message_in_prepared_error()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher();
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler { Throw = new IOException("socket closed during ARTICLE") },
                publisher,
                1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.Equal("IOException: socket closed during ARTICLE", pipeline.LastEscapedHandlerFailureError);
            var intent = Assert.Single(publisher.Published);
            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, intent.Outcome);
            Assert.Equal("IOException: socket closed during ARTICLE", intent.Error);
            var settlement = Assert.Single(channel.Settlements);
            Assert.True(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
        }

        /// <summary>
        /// Whitespace-only exception messages fall back to a non-empty type-name reason on the reply.
        /// </summary>
        [Fact]
        public async Task Escaped_handler_exception_with_blank_message_uses_type_name_fallback()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher();
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler { Throw = new InvalidOperationException("   ") },
                publisher,
                1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.Equal(nameof(InvalidOperationException), pipeline.LastEscapedHandlerFailureError);
            Assert.False(string.IsNullOrWhiteSpace(pipeline.LastEscapedHandlerFailureError));
            Assert.Equal(nameof(InvalidOperationException), Assert.Single(publisher.Published).Error);
        }

        /// <summary>
        /// A useful inner exception is preserved on the published UnexpectedFailure error.
        /// </summary>
        [Fact]
        public async Task Escaped_handler_exception_preserves_useful_inner_in_prepared_error()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher();
            var inner = new SocketException((int)SocketError.ConnectionReset);
            var outer = new IOException("transport reset", inner);
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler { Throw = outer },
                publisher,
                1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.Equal(
                "IOException: transport reset <- " + nameof(SocketException) + ": " + inner.Message,
                pipeline.LastEscapedHandlerFailureError);
            Assert.Equal(pipeline.LastEscapedHandlerFailureError, Assert.Single(publisher.Published).Error);
            Assert.True(Assert.Single(channel.Settlements).Acknowledge);
            Assert.False(Assert.Single(channel.Settlements).Requeue);
        }

        /// <summary>
        /// When the outer message already embeds the inner text, the prepared error does not repeat it.
        /// </summary>
        [Fact]
        public async Task Escaped_handler_exception_does_not_repeat_embedded_inner_message()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher();
            var inner = new SocketException((int)SocketError.ConnectionReset);
            var outer = new IOException("Unable to read data from the transport connection: " + inner.Message, inner);
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler { Throw = outer },
                publisher,
                1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.Equal(
                "IOException: Unable to read data from the transport connection: " + inner.Message,
                pipeline.LastEscapedHandlerFailureError);
            Assert.DoesNotContain(" <- ", pipeline.LastEscapedHandlerFailureError, StringComparison.Ordinal);
            Assert.Equal(pipeline.LastEscapedHandlerFailureError, Assert.Single(publisher.Published).Error);
        }

        /// <summary>
        /// Host cancellation remains Cancelled with null prepared UnexpectedFailure error and no publication.
        /// </summary>
        [Fact]
        public async Task Cancellation_does_not_enter_unexpected_failure_error_path()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher();
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.Success },
                publisher,
                1024);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                cts.Token);

            Assert.Equal(ArticleWorkOutcome.Cancelled, outcome);
            Assert.Null(pipeline.LastEscapedHandlerFailureError);
            Assert.Empty(publisher.Published);
            Assert.True(Assert.Single(channel.Settlements).Requeue);
        }

        /// <summary>
        /// ProviderFailure keeps its handler error, does not use the escaped-exception path, ACKs, and publishes a failure reply.
        /// </summary>
        [Fact]
        public async Task Provider_failure_acks_and_publishes_correlated_failure_without_escaped_path()
        {
            const string providerError = "IOException: connection reset by peer";
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher();
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler
                {
                    Outcome = ArticleWorkOutcome.ProviderFailure,
                    Error = providerError,
                },
                publisher,
                1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.ProviderFailure, outcome);
            Assert.Null(pipeline.LastEscapedHandlerFailureError);
            var intent = Assert.Single(publisher.Published);
            Assert.Equal(ArticleWorkOutcome.ProviderFailure, intent.Outcome);
            Assert.Equal(providerError, intent.Error);
            var settlement = Assert.Single(channel.Settlements);
            Assert.True(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
        }

        /// <summary>
        /// Publishable terminal failures still serialize the handler error into the confirmed RPC JSON body.
        /// </summary>
        [Fact]
        public async Task InvalidArticle_published_rpc_preserves_handler_error_text()
        {
            const string errorText = "ProviderFailure status=400: Service unavailable";
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher();
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler
                {
                    Outcome = ArticleWorkOutcome.InvalidArticle,
                    Error = errorText,
                },
                publisher,
                1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.InvalidArticle, outcome);
            var intent = Assert.Single(publisher.Published);
            Assert.Equal(errorText, intent.Error);
            using var document = JsonDocument.Parse(ArticleWorkResponseWireProtocol.SerializeV1(intent));
            Assert.Equal("InvalidArticle", document.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(errorText, document.RootElement.GetProperty("error").GetString());
            Assert.True(Assert.Single(channel.Settlements).Acknowledge);
        }

        /// <summary>
        /// Production handler rethrow through the delivery pipeline preserves FormatReason on the prepared error.
        /// </summary>
        [Fact]
        public async Task Isolated_pipeline_retrieve_exception_prepares_format_reason_error()
        {
            await using var retention = CreateRetention();
            var thrown = new IOException("isolated pipeline socket closed");
            var handler = new ProviderArticleWorkHandler(
                new ThrowingRetriever(thrown),
                retention,
                new NntpArticleParser("backfiller.test"),
                sharedConfiguration: null,
                serverId: 0,
                NullLogger<ProviderArticleWorkHandler>.Instance);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher();
            var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.Equal("IOException: isolated pipeline socket closed", pipeline.LastEscapedHandlerFailureError);
            Assert.Equal(pipeline.LastEscapedHandlerFailureError, Assert.Single(publisher.Published).Error);
            Assert.True(Assert.Single(channel.Settlements).Acknowledge);
            Assert.False(Assert.Single(channel.Settlements).Requeue);
        }

        /// <summary>
        /// Production retrieve throw with a distinct inner exception preserves that context on the prepared error.
        /// </summary>
        [Fact]
        public async Task Isolated_pipeline_retrieve_exception_preserves_distinct_inner()
        {
            await using var retention = CreateRetention();
            var inner = new SocketException((int)SocketError.TimedOut);
            var thrown = new IOException("isolated retrieve failed", inner);
            var handler = new ProviderArticleWorkHandler(
                new ThrowingRetriever(thrown),
                retention,
                new NntpArticleParser("backfiller.test"),
                sharedConfiguration: null,
                serverId: 0,
                NullLogger<ProviderArticleWorkHandler>.Instance);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher();
            var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.Equal(
                "IOException: isolated retrieve failed <- " + nameof(SocketException) + ": " + inner.Message,
                pipeline.LastEscapedHandlerFailureError);
            Assert.Equal(pipeline.LastEscapedHandlerFailureError, Assert.Single(publisher.Published).Error);
        }

        /// <summary>Creates a small in-memory retention authority for RPC error diagnostics tests.</summary>
        private static ArticleRetentionAuthority CreateRetention() =>
            new(
                new BackFillerArticleRetentionRuntimeOptions(1024 * 1024, TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), 256),
                "backfiller.test",
                563,
                TimeProvider.System,
                NullLogger<ArticleRetentionAuthority>.Instance);

        /// <summary><see cref="INntpArticleRetriever"/> that synchronously throws the configured exception.</summary>
        private sealed class ThrowingRetriever : INntpArticleRetriever
        {
            /// <summary>Exception propagated from <see cref="RetrieveAsync"/>.</summary>
            private readonly Exception _exception;

            /// <summary>Stores the exception to throw on retrieve.</summary>
            /// <param name="exception">Fault surfaced to the Article Work pipeline.</param>
            internal ThrowingRetriever(Exception exception)
            {
                _exception = exception;
            }

            /// <inheritdoc />
            public Task<ArticleRetrievalResult> RetrieveAsync(
                ArticleWorkItem item,
                int maxArticleBytes,
                Func<ReadOnlyMemory<byte>, ArticleRecordCreateResult> consumePayload,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(item);
                ArgumentNullException.ThrowIfNull(consumePayload);
                throw _exception;
            }
        }
    }
}
