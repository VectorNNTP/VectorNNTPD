using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
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
    /// Regression coverage for <see cref="ProviderArticleWorkHandler"/> completion logging: one structured information event per handled item,
    /// without article body or transport detail on the information path.
    /// </summary>
    public sealed class ArticleProcessingLogTests
    {
        /// <summary>
        /// Expected <see cref="Microsoft.Extensions.Logging.EventId"/> for the single article-processed completion log line.
        /// </summary>
        private const int ArticleProcessedEventId = 5321;

        /// <summary>
        /// Successful retention emits exactly one information event with message id, backbone, outcome <c>Found</c>, and elapsed time from NNTP command start.
        /// </summary>
        [Fact]
        public async Task Found_logs_one_information_event_with_identity_outcome_and_elapsed()
        {
            var started = Stopwatch.GetTimestamp() - (long)Math.Round(1.25 * Stopwatch.Frequency);
            var logger = new CollectingLogger<ProviderArticleWorkHandler>();
            await using var retention = CreateRetention();
            var handler = CreateHandler(
                logger,
                retention,
                new ScriptedRetriever(ArticleWorkTestArticles.Valid(), ArticleRetrievalResult.Retrieved(220, "follows"), started));

            var result = await handler.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews"), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
            var entry = AssertSingleCompletion(logger);
            AssertCompletion(entry, ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews", "Found");
            var seconds = ElapsedSeconds(entry.Message);
            Assert.InRange(seconds, 1.25, 5.0);
            Assert.DoesNotContain("body", entry.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Provider not-found maps to outcome <c>NotFound</c> in one information completion event; no duplicate information logs.
        /// </summary>
        [Fact]
        public async Task NotFound_logs_one_information_event()
        {
            var logger = new CollectingLogger<ProviderArticleWorkHandler>();
            await using var retention = CreateRetention();
            var handler = CreateHandler(
                logger,
                retention,
                new ScriptedRetriever(
                    payload: null,
                    ArticleRetrievalResult.Failed(ArticleRetrievalKind.ArticleNotFound, 430, "no such article", sessionReusable: true),
                    commandStartedTimestamp: Stopwatch.GetTimestamp()));

            var result = await handler.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId, "Eweka"), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
            var entry = AssertSingleCompletion(logger);
            AssertCompletion(entry, ArticleWorkTestDeliveries.CanonicalMessageId, "Eweka", "NotFound");
        }

        /// <summary>
        /// Parse/validation failure logs outcome <c>ValidationFailed</c> with the machine reason (not raw wire text) in one information event.
        /// </summary>
        [Fact]
        public async Task ValidationFailure_logs_one_information_event_with_the_parser_reason()
        {
            var logger = new CollectingLogger<ProviderArticleWorkHandler>();
            await using var retention = CreateRetention();
            var handler = CreateHandler(
                logger,
                retention,
                new ScriptedRetriever(
                    ArticleWorkTestArticles.InvalidMessageId(),
                    ArticleRetrievalResult.Retrieved(220, "follows"),
                    Stopwatch.GetTimestamp()));

            var result = await handler.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews"), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.InvalidArticle, result.Outcome);
            Assert.Equal("InvalidMessageId", result.Error);
            var entry = AssertSingleCompletion(logger);
            AssertCompletion(entry, ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews", "ValidationFailed");
            Assert.Contains("reason=InvalidMessageId", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("malformed-id", entry.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Provider transport failure logs outcome <c>Failed</c> with the retrieval diagnostic reason and connection id.
        /// </summary>
        [Fact]
        public async Task TransportFailure_logs_one_information_event_with_failed()
        {
            var connectionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var logger = new CollectingLogger<ProviderArticleWorkHandler>();
            await using var retention = CreateRetention();
            var handler = CreateHandler(
                logger,
                retention,
                new ScriptedRetriever(
                    payload: null,
                    ArticleRetrievalResult.Failed(ArticleRetrievalKind.ProviderFailure, 400, "connection reset", sessionReusable: false),
                    Stopwatch.GetTimestamp(),
                    connectionId));

            var result = await handler.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews"), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.ProviderFailure, result.Outcome);
            var entry = AssertSingleCompletion(logger);
            AssertCompletion(entry, ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews", "Failed");
            Assert.Contains("reason=connection reset", entry.Message, StringComparison.Ordinal);
            Assert.Contains("statusCode=400", entry.Message, StringComparison.Ordinal);
            Assert.Contains($"connectionId={connectionId:D}", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("ValidationFailed", entry.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Blank request identities render as <c>(none)</c> placeholders; elapsed still appears when command timing is absent.
        /// </summary>
        [Fact]
        public async Task Unresolved_identity_uses_the_existing_none_placeholder()
        {
            var logger = new CollectingLogger<ProviderArticleWorkHandler>();
            await using var retention = CreateRetention();
            var handler = CreateHandler(
                logger,
                retention,
                new ScriptedRetriever(
                    payload: null,
                    ArticleRetrievalResult.Failed(ArticleRetrievalKind.ProviderFailure, null, "down", sessionReusable: false),
                    commandStartedTimestamp: 0));

            var result = await handler.HandleAsync(Item(" ", ""), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.ProviderFailure, result.Outcome);
            var entry = AssertSingleCompletion(logger);
            AssertCompletion(entry, "(none)", "(none)", "Failed");
            // The logger message may render a short "s" or the full word "seconds" for the elapsed suffix.
            Assert.Matches(@"elapsed=0\.000 (?:s|seconds)", entry.Message);
        }

        /// <summary>
        /// Live NNTP retrieval keeps TX/RX debug wire logs on the registry logger while completion stays on the handler logger as a single information event without body text.
        /// </summary>
        [Fact]
        public async Task Live_article_keeps_debug_wire_logging_and_emits_one_completion_event()
        {
            var wire = new CollectingLogger<NntpProviderRegistry>();
            var completion = new CollectingLogger<ProviderArticleWorkHandler>();
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(_ => ArticleWorkTestArticles.ArticleResponse(ArticleWorkTestArticles.Valid()));
            factory.Enqueue(server);
            await using var registry = new NntpProviderRegistry(
                new StaticBackFillerProviderCatalog(
                [
                    new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, null, null, 0, 1),
                ]),
                factory,
                NntpSessionOptions.Default with
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                },
                TimeSpan.FromSeconds(2),
                wire);
            await using var retention = CreateRetention();
            var handler = new ProviderArticleWorkHandler(
                new NntpArticleRetriever(registry),
                retention,
                new NntpArticleParser("backfiller.test"),
                sharedConfiguration: null,
                serverId: 0,
                completion);

            var result = await handler.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews"), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
            var entry = AssertSingleCompletion(completion);
            AssertCompletion(entry, ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews", "Found");
            Assert.Contains(
                wire.Entries,
                static candidate => candidate.Level == LogLevel.Debug
                    && candidate.Message.Contains("TX: ARTICLE <12345@example.invalid>", StringComparison.Ordinal));
            Assert.Contains(
                wire.Entries,
                static candidate => candidate.Level == LogLevel.Debug
                    && candidate.Message.Contains("RX: ARTICLE payload complete", StringComparison.Ordinal));
            Assert.DoesNotContain(
                wire.Entries,
                static candidate => candidate.Message.Contains("Article processed", StringComparison.Ordinal));
            Assert.DoesNotContain(
                wire.Entries,
                static candidate => candidate.Message.Contains("NNTP provider retrieval failed", StringComparison.Ordinal));
            Assert.DoesNotContain("body", entry.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A non-invariant culture still renders elapsed seconds with a dot and three fractional digits, and the structured value stays a number.
        /// </summary>
        [Fact]
        public async Task Elapsed_stays_invariant_three_decimal_seconds_for_success_and_failure()
        {
            var originalCulture = CultureInfo.CurrentCulture;
            var originalUiCulture = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            try
            {
                var foundLogger = new CollectingLogger<ProviderArticleWorkHandler>();
                await using var retention = CreateRetention();
                var started = Stopwatch.GetTimestamp() - (long)Math.Round(1.25 * Stopwatch.Frequency);
                var found = CreateHandler(
                    foundLogger,
                    retention,
                    new ScriptedRetriever(ArticleWorkTestArticles.Valid(), ArticleRetrievalResult.Retrieved(220, "follows"), started));
                var foundResult = await found.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews"), CancellationToken.None);
                Assert.Equal(ArticleWorkOutcome.Success, foundResult.Outcome);
                var foundEntry = AssertSingleCompletion(foundLogger);
                AssertCompletion(foundEntry, ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews", "Found");

                var failedLogger = new CollectingLogger<ProviderArticleWorkHandler>();
                var failed = CreateHandler(
                    failedLogger,
                    retention,
                    new ScriptedRetriever(
                        payload: null,
                        ArticleRetrievalResult.Failed(ArticleRetrievalKind.ProviderFailure, null, "down", sessionReusable: false),
                        commandStartedTimestamp: 0));
                var failedResult = await failed.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews"), CancellationToken.None);
                Assert.Equal(ArticleWorkOutcome.ProviderFailure, failedResult.Outcome);
                var failedEntry = AssertSingleCompletion(failedLogger);
                AssertCompletion(failedEntry, ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews", "Failed");
                Assert.Equal(0d, ElapsedValue(failedEntry));
                Assert.Contains("elapsed=0.000 ", failedEntry.Message, StringComparison.Ordinal);
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
                CultureInfo.CurrentUICulture = originalUiCulture;
            }
        }

        /// <summary>
        /// Serilog renders the Information completion event with the same three-decimal elapsed text and a numeric Elapsed property.
        /// </summary>
        [Fact]
        public async Task Serilog_renders_information_completion_with_numeric_elapsed_seconds()
        {
            var sink = new ListSink();
            using var serilog = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
            using var factory = new SerilogLoggerFactory(serilog, dispose: false);
            var logger = factory.CreateLogger<ProviderArticleWorkHandler>();
            await using var retention = CreateRetention();
            var started = Stopwatch.GetTimestamp() - (long)Math.Round(1.5 * Stopwatch.Frequency);
            var handler = new ProviderArticleWorkHandler(
                new ScriptedRetriever(
                    payload: null,
                    ArticleRetrievalResult.Failed(ArticleRetrievalKind.ArticleNotFound, 430, "no such article", sessionReusable: true),
                    started),
                retention,
                new NntpArticleParser("backfiller.test"),
                sharedConfiguration: null,
                serverId: 0,
                logger);

            var result = await handler.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId, "Eweka"), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
            var entry = Assert.Single(sink.Events);
            Assert.Equal(LogEventLevel.Information, entry.Level);
            var rendered = entry.RenderMessage(CultureInfo.InvariantCulture);
            Assert.Contains("Article processed", rendered, StringComparison.Ordinal);
            Assert.Contains("NotFound", rendered, StringComparison.Ordinal);
            Assert.Matches(ElapsedPattern(), rendered);
            Assert.True(entry.Properties.TryGetValue("Elapsed", out var elapsedProperty));
            var scalar = Assert.IsType<ScalarValue>(elapsedProperty);
            Assert.InRange(Assert.IsType<double>(scalar.Value), 1.5d, 5d);

            sink.Events.Clear();
            var found = new ProviderArticleWorkHandler(
                new ScriptedRetriever(ArticleWorkTestArticles.Valid(), ArticleRetrievalResult.Retrieved(220, "follows"), started),
                retention,
                new NntpArticleParser("backfiller.test"),
                sharedConfiguration: null,
                serverId: 0,
                logger);
            var foundResult = await found.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews"), CancellationToken.None);
            Assert.Equal(ArticleWorkOutcome.Success, foundResult.Outcome);
            var foundEntry = Assert.Single(sink.Events);
            Assert.Equal(LogEventLevel.Information, foundEntry.Level);
            var foundRendered = foundEntry.RenderMessage(CultureInfo.InvariantCulture);
            Assert.Contains("outcome=\"Found\"", foundRendered, StringComparison.Ordinal);
            Assert.Matches(ElapsedPattern(), foundRendered);
            Assert.True(foundEntry.Properties.TryGetValue("Elapsed", out var foundElapsed));
            Assert.InRange(Assert.IsType<double>(Assert.IsType<ScalarValue>(foundElapsed).Value), 1.5d, 5d);
        }

        /// <summary>
        /// Builds a handler with a fixed local parser identity and the supplied retriever/retention for logging assertions.
        /// </summary>
        /// <param name="logger">Collects completion logs from the handler under test.</param>
        /// <param name="retention">Authority used when a retrieved article is retained.</param>
        /// <param name="retriever">Scripted or live retrieval seam.</param>
        /// <returns>A configured <see cref="ProviderArticleWorkHandler"/>.</returns>
        private static ProviderArticleWorkHandler CreateHandler(
            CollectingLogger<ProviderArticleWorkHandler> logger,
            ArticleRetentionAuthority retention,
            INntpArticleRetriever retriever)
        {
            return new ProviderArticleWorkHandler(
                retriever,
                retention,
                new NntpArticleParser("backfiller.test"),
                sharedConfiguration: null,
                serverId: 0,
                logger);
        }

        /// <summary>
        /// Creates a small in-memory retention authority with production-like listener metadata for handler tests.
        /// </summary>
        /// <returns>An <see cref="ArticleRetentionAuthority"/> disposed by callers.</returns>
        private static ArticleRetentionAuthority CreateRetention() =>
            new(
                new BackFillerArticleRetentionRuntimeOptions(1024 * 1024, TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), 256),
                "backfiller.test",
                563,
                TimeProvider.System,
                NullLogger<ArticleRetentionAuthority>.Instance);

        /// <summary>
        /// Minimal work item with a fake settlement lease; not used to assert RabbitMQ settlement in these tests.
        /// </summary>
        /// <param name="messageId">Requested NNTP message-id.</param>
        /// <param name="backbone">Provider backbone name carried on the request.</param>
        /// <returns>An <see cref="ArticleWorkItem"/> for handler invocation.</returns>
        private static ArticleWorkItem Item(string messageId, string backbone)
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            return new ArticleWorkItem(
                new ArticleWorkRequest(1, Guid.NewGuid(), messageId, backbone),
                "corr",
                "reply",
                new ArticleWorkSettlementLease(channel, 1, 1));
        }

        /// <summary>
        /// Asserts exactly one information log uses <see cref="ArticleProcessedEventId"/> and no other information events were emitted.
        /// </summary>
        /// <param name="logger">Logger populated by the handler under test.</param>
        /// <returns>The sole completion log entry.</returns>
        private static CollectedLog AssertSingleCompletion(CollectingLogger<ProviderArticleWorkHandler> logger)
        {
            var information = logger.Entries.Where(static entry => entry.Level == LogLevel.Information).ToArray();
            var entry = Assert.Single(information);
            Assert.Equal(ArticleProcessedEventId, entry.EventId.Id);
            Assert.DoesNotContain(
                logger.Entries,
                static candidate => candidate.Level == LogLevel.Information && candidate.EventId.Id != ArticleProcessedEventId);
            return entry;
        }

        /// <summary>
        /// Asserts structured completion message fields for identity, outcome label, and elapsed suffix pattern.
        /// </summary>
        /// <param name="entry">Collected completion log line.</param>
        /// <param name="messageId">Expected message-id token in the message.</param>
        /// <param name="backbone">Expected backbone token in the message.</param>
        /// <param name="outcome">Expected outcome label (for example <c>Found</c> or <c>NotFound</c>).</param>
        private static void AssertCompletion(CollectedLog entry, string messageId, string backbone, string outcome)
        {
            Assert.Contains($"messageId={messageId}", entry.Message, StringComparison.Ordinal);
            Assert.Contains($"backbone={backbone}", entry.Message, StringComparison.Ordinal);
            Assert.Contains($"outcome={outcome}", entry.Message, StringComparison.Ordinal);
            Assert.Matches(ElapsedPattern(), entry.Message);
            var elapsed = ElapsedValue(entry);
            Assert.True(elapsed >= 0d);
            Assert.False(double.IsNaN(elapsed));
            Assert.Contains(
                $"elapsed={elapsed.ToString("0.000", CultureInfo.InvariantCulture)} ",
                entry.Message,
                StringComparison.Ordinal);
        }

        /// <summary>Reads the numeric <c>Elapsed</c> property from the source-generated logger state.</summary>
        /// <param name="entry">Completion entry captured from <see cref="CollectingLogger{T}"/>.</param>
        /// <returns>Elapsed seconds stored on the event.</returns>
        /// <exception cref="Xunit.Sdk.XunitException">The state has no numeric <c>Elapsed</c> property, or that property is not a <see cref="double"/>.</exception>
        private static double ElapsedValue(CollectedLog entry)
        {
            foreach (var item in Assert.IsAssignableFrom<System.Collections.IEnumerable>(entry.State))
            {
                if (item is KeyValuePair<string, object?> nullable && nullable.Key == "Elapsed")
                {
                    return Assert.IsType<double>(nullable.Value);
                }

                if (item is KeyValuePair<string, object> pair && pair.Key == "Elapsed")
                {
                    return Assert.IsType<double>(pair.Value);
                }
            }

            throw new Xunit.Sdk.XunitException("ArticleProcessed logger state has no numeric Elapsed property.");
        }

        /// <summary>
        /// Parses the <c>elapsed=</c> seconds field from a completion message for range assertions.
        /// </summary>
        /// <param name="message">Rendered completion log text.</param>
        /// <returns>Elapsed seconds from the structured field.</returns>
        private static double ElapsedSeconds(string message)
        {
            var match = ElapsedPattern().Match(message);
            Assert.True(match.Success);
            return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Regex matching the completion log elapsed suffix (<c>s</c> or <c>seconds</c>).
        /// </summary>
        /// <returns>A culture-invariant pattern for elapsed extraction and assertion.</returns>
        private static Regex ElapsedPattern() => new(@"elapsed=(\d+\.\d{3}) (?:s|seconds)", RegexOptions.CultureInvariant);

        /// <summary>In-memory Serilog sink for one completion-event assertion.</summary>
        private sealed class ListSink : ILogEventSink
        {
            /// <summary>Events passed to <see cref="Emit"/>.</summary>
            internal List<LogEvent> Events { get; } = [];

            /// <inheritdoc />
            public void Emit(LogEvent logEvent)
            {
                ArgumentNullException.ThrowIfNull(logEvent);
                Events.Add(logEvent);
            }
        }

        /// <summary>
        /// Fake retriever that optionally invokes the payload callback once, then returns a fixed <see cref="ArticleRetrievalResult"/>.
        /// Does not perform NNTP I/O, honor <paramref name="maxArticleBytes"/> limits, or observe cancellation.
        /// </summary>
        private sealed class ScriptedRetriever : INntpArticleRetriever
        {
            /// <summary>Destuffed article bytes passed to <c>consumePayload</c> when non-null.</summary>
            private readonly byte[]? _payload;

            /// <summary>Result returned after optional payload consumption; timestamp may be overwritten.</summary>
            private readonly ArticleRetrievalResult _result;

            /// <summary>Stopwatch timestamp applied to the returned result's command-start field.</summary>
            private readonly long _commandStartedTimestamp;

            /// <summary>
            /// Captures scripted retrieval inputs for elapsed and outcome logging tests.
            /// </summary>
            /// <param name="payload">Article bytes for the callback, or null to skip consumption (failures).</param>
            /// <param name="result">Base retrieval result (kind, code, session reuse).</param>
            /// <param name="commandStartedTimestamp">Value assigned to <see cref="ArticleRetrievalResult.CommandStartedTimestamp"/> on return.</param>
            /// <summary>Optional physical connection identity stamped onto the returned result.</summary>
            private readonly Guid _connectionId;

            internal ScriptedRetriever(
                byte[]? payload,
                ArticleRetrievalResult result,
                long commandStartedTimestamp,
                Guid connectionId = default)
            {
                _payload = payload;
                _result = result;
                _commandStartedTimestamp = commandStartedTimestamp;
                _connectionId = connectionId;
            }

            /// <summary>
            /// Invokes <paramref name="consumePayload"/> when payload is present, stamps command start time, and completes synchronously.
            /// </summary>
            /// <param name="item">Work item (required non-null).</param>
            /// <param name="maxArticleBytes">Ignored by this fake.</param>
            /// <param name="consumePayload">Parser/materialization callback (required non-null).</param>
            /// <param name="cancellationToken">Ignored by this fake.</param>
            /// <returns>The configured <see cref="ArticleRetrievalResult"/>.</returns>
            public Task<ArticleRetrievalResult> RetrieveAsync(
                ArticleWorkItem item,
                int maxArticleBytes,
                Func<ReadOnlyMemory<byte>, ArticleRecordCreateResult> consumePayload,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(item);
                ArgumentNullException.ThrowIfNull(consumePayload);
                if (_payload is not null)
                {
                    _ = consumePayload(_payload);
                }

                _result.CommandStartedTimestamp = _commandStartedTimestamp;
                _result.ConnectionId = _connectionId;
                return Task.FromResult(_result);
            }
        }
    }
}
