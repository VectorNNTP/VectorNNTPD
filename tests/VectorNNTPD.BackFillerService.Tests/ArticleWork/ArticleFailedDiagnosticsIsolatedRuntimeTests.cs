using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
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
    /// Isolated verification that provider failures still emit Information Event 5321 and preserve
    /// useful <see cref="ArticleWorkHandlerResult.Error"/> text after A5/A6 cleanup.
    /// </summary>
    public sealed class ArticleFailedDiagnosticsIsolatedRuntimeTests
    {
        /// <summary>
        /// Scripted ARTICLE 400 → ProviderFailure result error retains status text; Event 5321 is Information Failed.
        /// </summary>
        [Fact]
        public async Task Isolated_scripted_nntp_provider_failure_emits_serilog_failed_event_5321()
        {
            const string statusText = "Service unavailable";
            const int statusCode = 400;
            var sink = new ListSink();
            using var serilog = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
            using var factory = new SerilogLoggerFactory(serilog, dispose: false);
            var handlerLogger = factory.CreateLogger<ProviderArticleWorkHandler>();

            var nntpFactory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(_ => $"{statusCode} {statusText}\r\n");
            nntpFactory.Enqueue(server);
            await using var registry = new NntpProviderRegistry(
                new StaticBackFillerProviderCatalog(
                [
                    new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, null, null, 0, 1),
                ]),
                nntpFactory,
                NntpSessionOptions.Default with
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                },
                TimeSpan.FromSeconds(2),
                NullLogger<NntpProviderRegistry>.Instance);
            await using var retention = CreateRetention();
            var handler = new ProviderArticleWorkHandler(
                new NntpArticleRetriever(registry),
                retention,
                new NntpArticleParser("backfiller.test"),
                sharedConfiguration: null,
                serverId: 0,
                handlerLogger);

            var result = await handler.HandleAsync(
                Item(ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews"),
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.ProviderFailure, result.Outcome);
            Assert.Equal(statusText, result.Error);
            Assert.Equal(ArticleRetrievalKind.ProviderFailure, handler.LastKind);

            var entry = Assert.Single(sink.Events, static e => e.Level == LogEventLevel.Information);
            var rendered = entry.RenderMessage(CultureInfo.InvariantCulture);
            Assert.Contains("Article processed", rendered, StringComparison.Ordinal);
            Assert.Contains("outcome=\"Failed\"", rendered, StringComparison.Ordinal);
            Assert.Null(entry.Exception);
        }

        /// <summary>
        /// Connect-time IOException message survives into handler result.Error (FormatReason), not type-name-only.
        /// </summary>
        [Fact]
        public async Task Isolated_connect_io_exception_preserves_message_in_handler_error()
        {
            const string transportMessage = "connection reset by peer";
            var sink = new ListSink();
            using var serilog = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
            using var factory = new SerilogLoggerFactory(serilog, dispose: false);
            var handlerLogger = factory.CreateLogger<ProviderArticleWorkHandler>();

            var nntpFactory = new ScriptedNntpTransportFactory
            {
                ConnectException = new IOException(transportMessage),
            };
            await using var registry = new NntpProviderRegistry(
                new StaticBackFillerProviderCatalog(
                [
                    new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, null, null, 0, 1),
                ]),
                nntpFactory,
                NntpSessionOptions.Default with
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                },
                TimeSpan.FromSeconds(2),
                NullLogger<NntpProviderRegistry>.Instance);
            await using var retention = CreateRetention();
            var handler = new ProviderArticleWorkHandler(
                new NntpArticleRetriever(registry),
                retention,
                new NntpArticleParser("backfiller.test"),
                sharedConfiguration: null,
                serverId: 0,
                handlerLogger);

            var result = await handler.HandleAsync(
                Item(ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews"),
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.ProviderFailure, result.Outcome);
            Assert.Equal("IOException: " + transportMessage, result.Error);
            var entry = Assert.Single(sink.Events, static e => e.Level == LogEventLevel.Information);
            Assert.Contains("outcome=\"Failed\"", entry.RenderMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        /// <summary>
        /// A retrieve exception still emits Failed Event 5321 before rethrow (HEAD behaviour: no attached exception).
        /// </summary>
        [Fact]
        public async Task Isolated_retrieve_exception_emits_failed_event_then_rethrows()
        {
            var sink = new ListSink();
            using var serilog = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
            using var factory = new SerilogLoggerFactory(serilog, dispose: false);
            var handlerLogger = factory.CreateLogger<ProviderArticleWorkHandler>();
            await using var retention = CreateRetention();
            var thrown = new IOException("isolated-runtime socket closed");
            var handler = new ProviderArticleWorkHandler(
                new ThrowingRetriever(thrown),
                retention,
                new NntpArticleParser("backfiller.test"),
                sharedConfiguration: null,
                serverId: 0,
                handlerLogger);

            var ex = await Assert.ThrowsAsync<IOException>(
                () => handler.HandleAsync(
                    Item(ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews"),
                    CancellationToken.None).AsTask());

            Assert.Same(thrown, ex);
            var entry = Assert.Single(sink.Events, static e => e.Level == LogEventLevel.Information);
            Assert.Contains("outcome=\"Failed\"", entry.RenderMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            Assert.Null(entry.Exception);
        }

        /// <summary>Builds a minimal work item with a fake settlement lease for isolated-runtime diagnostics tests.</summary>
        /// <param name="messageId">NNTP Message-ID header value.</param>
        /// <param name="backbone">Provider backbone name on the request.</param>
        private static ArticleWorkItem Item(string messageId, string backbone) =>
            new(
                new ArticleWorkRequest(1, Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId), messageId, backbone),
                "corr",
                "reply",
                new ArticleWorkSettlementLease(new FakeBackFillerRabbitMqChannel(1), 1, 1));

        /// <summary>Creates a small in-memory retention authority for Article Work handler tests.</summary>
        private static ArticleRetentionAuthority CreateRetention() =>
            new(
                new BackFillerArticleRetentionRuntimeOptions(1024 * 1024, TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), 256),
                "backfiller.test",
                563,
                TimeProvider.System,
                NullLogger<ArticleRetentionAuthority>.Instance);

        /// <summary><see cref="INntpArticleRetriever"/> that completes with a fault carrying the configured exception.</summary>
        private sealed class ThrowingRetriever(Exception exception) : INntpArticleRetriever
        {
            /// <inheritdoc />
            public Task<ArticleRetrievalResult> RetrieveAsync(
                ArticleWorkItem item,
                int maxArticleBytes,
                Func<ReadOnlyMemory<byte>, ArticleRecordCreateResult> consumePayload,
                CancellationToken cancellationToken) =>
                Task.FromException<ArticleRetrievalResult>(exception);
        }

        /// <summary>Captures Serilog events in memory for assertion.</summary>
        private sealed class ListSink : ILogEventSink
        {
            /// <summary>Events emitted through <see cref="Emit"/>.</summary>
            public List<LogEvent> Events { get; } = new();

            /// <inheritdoc />
            public void Emit(LogEvent logEvent) => Events.Add(logEvent);
        }
    }
}
