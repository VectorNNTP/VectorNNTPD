using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles;
using System.Text;
using VectorNNTPD.Common.Configuration;
using NntpArticleIdentity = VectorNNTPD.Common.Articles.Processing.NntpArticleIdentity;
using System.Globalization;

namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// Retrieves an ARTICLE, builds a CanonicalV1 <see cref="ArticleRecord"/>, then retains it for
    /// NNTP delivery by Message-ID. Article Work Success publishes this BackFiller's FQDN, listener port, and the
    /// lowercase hexadecimal <see cref="ArticleId"/>. Those fields are routing metadata, not a transfer protocol.
    /// </summary>
    /// <remarks>
    /// Does not publish the RPC response or settle the delivery.
    /// The <c>Last*</c> properties are test observations written by <see cref="HandleAsync"/> with no synchronization.
    /// They do not store article bytes. Retained articles live only in <see cref="IArticleRetentionAuthority"/>.
    /// </remarks>
    internal sealed class ProviderArticleWorkHandler : IArticleWorkHandler
    {
        /// <summary>NNTP ARTICLE retriever used for admitted work.</summary>
        private readonly INntpArticleRetriever _retriever;

        /// <summary>Canonical retention authority that admits or rejects the built record.</summary>
        private readonly IArticleRetentionAuthority _retention;

        /// <summary>Parser used to build the CanonicalV1 record from the retrieved article bytes.</summary>
        private readonly NntpArticleParser _parser;

        /// <summary>Published shared configuration. Null in tests that do not load <c>nntpsharedconfig</c>.</summary>
        private readonly INntpSharedConfigurationCatalogue? _sharedConfiguration;

        /// <summary>Server id used with <see cref="NntpSharedConfiguration.DnsSuffix"/> for this article's FQDN. Zero keeps the injected parser.</summary>
        private readonly int _serverId;

        /// <summary>Logger for the single article-processing Information event.</summary>
        private readonly ILogger<ProviderArticleWorkHandler> _logger;

        /// <summary>
        /// Pool for canonical article arrays. Production uses <see cref="ArrayPool{T}.Shared"/>.
        /// </summary>
        private readonly ArrayPool<byte> _canonicalBufferPool;

        /// <summary>Log outcome when the article was retrieved and accepted.</summary>
        private const string OutcomeFound = "Found";

        /// <summary>Log outcome when the provider reports that the article does not exist.</summary>
        private const string OutcomeNotFound = "NotFound";

        /// <summary>Log outcome when the retrieved article fails validation.</summary>
        private const string OutcomeValidationFailed = "ValidationFailed";

        /// <summary>Log outcome for transport and every other terminal failure.</summary>
        private const string OutcomeFailed = "Failed";

        /// <summary>Placeholder already used by Article Work logs when an identity was not resolved.</summary>
        private const string UnresolvedIdentity = "(none)";

        /// <summary>Initializes the handler with the test-host Path identity <c>backfiller.test</c>.</summary>
        /// <param name="retriever">NNTP ARTICLE retriever.</param>
        /// <param name="retention">Canonical retention authority.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="retriever"/> or <paramref name="retention"/> is null.</exception>
        internal ProviderArticleWorkHandler(INntpArticleRetriever retriever, IArticleRetentionAuthority retention)
            : this(retriever, retention, new NntpArticleParser("backfiller.test"))
        {
        }

        /// <summary>
        /// Creates the handler with an explicit Path identity on the CanonicalV1 parser and no shared-configuration catalogue.
        /// </summary>
        /// <param name="retriever">NNTP ARTICLE retriever.</param>
        /// <param name="retention">Canonical retention authority.</param>
        /// <param name="parser">CanonicalV1 parser, including the Path host identity it stamps onto accepted articles.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="retriever"/>, <paramref name="retention"/>, or <paramref name="parser"/> is null.
        /// </exception>
        internal ProviderArticleWorkHandler(
            INntpArticleRetriever retriever,
            IArticleRetentionAuthority retention,
            NntpArticleParser parser)
            : this(retriever, retention, parser, sharedConfiguration: null)
        {
        }

        /// <summary>Initializes the handler with an optional shared-configuration catalogue.</summary>
        /// <param name="retriever">NNTP ARTICLE retriever.</param>
        /// <param name="retention">Canonical retention authority.</param>
        /// <param name="parser">CanonicalV1 parser used when <paramref name="sharedConfiguration"/> is null.</param>
        /// <param name="sharedConfiguration">Published <c>nntpsharedconfig</c>. Captured once per article.</param>
        private ProviderArticleWorkHandler(
            INntpArticleRetriever retriever,
            IArticleRetentionAuthority retention,
            NntpArticleParser parser,
            INntpSharedConfigurationCatalogue? sharedConfiguration)
            : this(retriever, retention, parser, sharedConfiguration, serverId: 0)
        {
        }

        /// <summary>Initializes the handler with the server id used to build the article FQDN from one shared snapshot.</summary>
        /// <param name="retriever">NNTP ARTICLE retriever.</param>
        /// <param name="retention">Canonical retention authority.</param>
        /// <param name="parser">CanonicalV1 parser used when <paramref name="serverId"/> is outside the accepted range.</param>
        /// <param name="sharedConfiguration">Published <c>nntpsharedconfig</c>. Captured once per article.</param>
        /// <param name="serverId">BackFiller server id combined with <c>dnssuffix</c> for the Path hop and retained endpoint.</param>
        /// <param name="logger">Article-processing logger. Null uses <see cref="NullLogger{T}.Instance"/>.</param>
        /// <param name="canonicalBufferPool">
        /// Pool for the canonical article array. Null uses <see cref="ArrayPool{T}.Shared"/>.
        /// </param>
        internal ProviderArticleWorkHandler(
            INntpArticleRetriever retriever,
            IArticleRetentionAuthority retention,
            NntpArticleParser parser,
            INntpSharedConfigurationCatalogue? sharedConfiguration,
            int serverId,
            ILogger<ProviderArticleWorkHandler>? logger = null,
            ArrayPool<byte>? canonicalBufferPool = null)
        {
            ArgumentNullException.ThrowIfNull(retriever);
            ArgumentNullException.ThrowIfNull(retention);
            ArgumentNullException.ThrowIfNull(parser);
            _retriever = retriever;
            _retention = retention;
            _parser = parser;
            _sharedConfiguration = sharedConfiguration;
            _serverId = serverId;
            _logger = logger ?? NullLogger<ProviderArticleWorkHandler>.Instance;
            _canonicalBufferPool = canonicalBufferPool ?? ArrayPool<byte>.Shared;
        }

        /// <summary>Last <see cref="INntpArticleRetriever"/> outcome written by <see cref="HandleAsync"/> without synchronization (tests).</summary>
        internal ArticleRetrievalKind? LastKind { get; private set; }

        /// <summary>Last <see cref="IArticleRetentionAuthority.RetainCanonical"/> kind from the same call (tests).</summary>
        internal ArticleRetentionKind? LastRetentionKind { get; private set; }

        /// <summary>Last Success FQDN returned by retention for routing metadata (tests).</summary>
        internal string? LastFqdn { get; private set; }

        /// <summary>Last Success TLS listener port returned by retention (tests).</summary>
        internal int? LastListenerPort { get; private set; }

        /// <summary>
        /// Retrieves the requested article, accepts a CanonicalV1 record, and retains it for NNTP delivery.
        /// </summary>
        /// <param name="item">Admitted work.</param>
        /// <param name="cancellationToken">
        /// Cancels retrieval. When it is already cancelled, or retrieval throws
        /// <see cref="OperationCanceledException"/> because it is cancelled, the outcome is
        /// <see cref="ArticleWorkOutcome.Cancelled"/> and retention is not attempted.
        /// </param>
        /// <returns>
        /// <see cref="ArticleWorkOutcome.Success"/> with FQDN, listener port, and article id when retention reports the article available.
        /// <see cref="ArticleRetrievalKind.ArticleNotFound"/> and <see cref="ArticleRetrievalKind.InvalidArticle"/> stay terminal.
        /// <see cref="ArticleRetrievalKind.AuthenticationFailure"/> and every other non-retrieved kind become
        /// <see cref="ArticleWorkOutcome.ProviderFailure"/>. Parse failure, Message-ID mismatch, and an article that
        /// <see cref="ArticleWorkDeliveryFraming"/> cannot send as a multiline block become
        /// <see cref="ArticleWorkOutcome.InvalidArticle"/> before retention is called. A retrieved body that cannot be transferred or is not an
        /// owned contiguous buffer becomes <see cref="ArticleWorkOutcome.RetentionRejected"/>.
        /// Success routing fields are null unless retention made the article available.
        /// <see cref="ArticleWorkHandlerResult.Article"/> is always null. The production success path does not allocate
        /// a <see cref="RetrievedArticle"/>; the canonical array is the one produced by
        /// <see cref="ArticleRecordFactory.TryCreate(NntpArticleParser, ReadOnlyMemory{byte}, ArticlePathMode, ArrayPool{byte})"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="item"/> is null.</exception>
        /// <remarks>
        /// <see cref="ArticleRecordFactory.TryCreate(NntpArticleParser, ReadOnlyMemory{byte}, ArticlePathMode, ArrayPool{byte})"/> runs
        /// inside the retriever callback, before that call returns. The callback memory is not used after it returns.
        /// An <see cref="OperationCanceledException"/> whose token is not <paramref name="cancellationToken"/> propagates.
        /// Other exceptions from retrieval, parsing, or retention also propagate. The caller maps those to
        /// <see cref="ArticleWorkOutcome.UnexpectedFailure"/>.
        /// When no shared-configuration catalogue was supplied, retrieval is called with a zero article limit so the
        /// session's <c>NntpSessionOptions.MaxArticleBytes</c> remains in force. A supplied catalogue passes that
        /// snapshot's <c>MaxArticleBytes</c>.
        /// </remarks>
        public async ValueTask<ArticleWorkHandlerResult> HandleAsync(
            ArticleWorkItem item,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            LastRetentionKind = null;
            LastFqdn = null;
            LastListenerPort = null;
            if (cancellationToken.IsCancellationRequested)
            {
                LastKind = ArticleRetrievalKind.Cancelled;
                LogArticleProcessed(item, OutcomeFailed, string.Empty, commandStartedTimestamp: 0);
                return new ArticleWorkHandlerResult(ArticleWorkOutcome.Cancelled, null);
            }

            ArticleRetrievalResult retrieval;
            ArticleRecordCreateResult created = default;
            var consumed = false;
            var maxArticleBytes = 0;
            byte[]? siteNameUtf8 = null;
            var parser = _parser;
            string? endpointFqdn = null;
            if (_sharedConfiguration is not null)
            {
                var shared = _sharedConfiguration.Current;
                maxArticleBytes = shared.MaxArticleBytes;
                siteNameUtf8 = Encoding.UTF8.GetBytes(shared.SiteName);
                if (ServerIdRules.IsInRange(_serverId))
                {
                    endpointFqdn = ApplicationFqdn.Build(BackFillerOptions.ApplicationPrefix, _serverId, shared.DnsSuffix);
                    parser = new NntpArticleParser(endpointFqdn);
                }
            }

            try
            {
                try
                {
                    retrieval = await _retriever.RetrieveAsync(
                            item,
                            maxArticleBytes,
                            memory =>
                            {
                                created = siteNameUtf8 is null
                                    ? ArticleRecordFactory.TryCreate(
                                        parser,
                                        memory,
                                        ArticlePathMode.Traverse,
                                        _canonicalBufferPool)
                                    : ArticleRecordFactory.TryCreate(
                                        parser,
                                        memory,
                                        ArticlePathMode.Traverse,
                                        maxArticleBytes,
                                        siteNameUtf8,
                                        _canonicalBufferPool);
                                consumed = true;
                                return created;
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogArticleProcessed(item, OutcomeFailed, string.Empty, CommandTimestamp(ex));
                    if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    {
                        LastKind = ArticleRetrievalKind.Cancelled;
                        return new ArticleWorkHandlerResult(ArticleWorkOutcome.Cancelled, null);
                    }

                    throw;
                }

                using (retrieval)
                {
                    LastKind = retrieval.Kind;
                    if (retrieval.Kind != ArticleRetrievalKind.ArticleRetrieved)
                    {
                        var mapped = MapRetrieval(retrieval);
                        var (outcome, reason) = RetrievalLog(retrieval);
                        LogArticleProcessed(item, outcome, reason, retrieval.CommandStartedTimestamp);
                        return mapped;
                    }

                    if (!consumed)
                    {
                        LastRetentionKind = ArticleRetentionKind.InvalidPayload;
                        LogArticleProcessed(item, OutcomeFailed, string.Empty, retrieval.CommandStartedTimestamp);
                        return new ArticleWorkHandlerResult(
                            ArticleWorkOutcome.RetentionRejected,
                            "Retrieved article payload could not be transferred into retention.");
                    }

                    if (!created.IsAccepted)
                    {
                        var rejected = created.ParseFailure != NntpArticleParseFailureCode.None
                            ? MapParseFailure(created.ParseFailure)
                            : new ArticleWorkHandlerResult(
                                ArticleWorkOutcome.InvalidArticle,
                                created.MaterializeFailure.ToString());
                        LogArticleProcessed(
                            item,
                            OutcomeValidationFailed,
                            rejected.Error ?? string.Empty,
                            retrieval.CommandStartedTimestamp);
                        return rejected;
                    }

                    var record = created.Record;
                    if (!NntpArticleIdentity.MatchesRequest(record.MessageId, item.Request.MessageId))
                    {
                        LogArticleProcessed(
                            item,
                            OutcomeValidationFailed,
                            "MessageIdMismatch",
                            retrieval.CommandStartedTimestamp);
                        return new ArticleWorkHandlerResult(
                            ArticleWorkOutcome.InvalidArticle,
                            "MessageIdMismatch");
                    }

                    if (!ArticleWorkDeliveryFraming.IsFrameable(record.ArtData.Span))
                    {
                        LogArticleProcessed(
                            item,
                            OutcomeValidationFailed,
                            "NotFrameable",
                            retrieval.CommandStartedTimestamp);
                        return new ArticleWorkHandlerResult(
                            ArticleWorkOutcome.InvalidArticle,
                            "NotFrameable");
                    }

                    LogArticleProcessed(item, OutcomeFound, string.Empty, retrieval.CommandStartedTimestamp);

                    if (!System.Runtime.InteropServices.MemoryMarshal.TryGetArray(record.ArtData, out var segment)
                        || segment.Array is null
                        || segment.Offset != 0
                        || segment.Count != record.ArtSize)
                    {
                        LastRetentionKind = ArticleRetentionKind.InvalidPayload;
                        return new ArticleWorkHandlerResult(
                            ArticleWorkOutcome.RetentionRejected,
                            "Canonical ArtData is not an owned contiguous buffer.");
                    }

                    var retained = _retention.RetainCanonical(
                        item.Request.MessageId,
                        item.Request.RequestId,
                        record,
                        created.SelectedDateHeaderName,
                        endpointFqdn);
                    LastRetentionKind = retained.Kind;
                    LastFqdn = retained.Fqdn;
                    LastListenerPort = retained.ListenerPort;
                    if (retained.IsAvailable)
                    {
                        return new ArticleWorkHandlerResult(
                            ArticleWorkOutcome.Success,
                            null,
                            Article: null,
                            Fqdn: retained.Fqdn,
                            ListenerPort: retained.ListenerPort,
                            ArticleId: record.ArtId);
                    }

                    return new ArticleWorkHandlerResult(
                        ArticleWorkOutcome.RetentionRejected,
                        retained.Kind.ToString(),
                        Article: null,
                        Fqdn: null,
                        ListenerPort: null,
                        ArticleId: null);
                }
            }
            finally
            {
                if (created.IsAccepted)
                {
                    created.Record.ReleaseUnusedCanonicalBuffer();
                }
            }
        }

        /// <summary>
        /// Writes one article-processing Information event. Does not include the article body.
        /// </summary>
        /// <param name="item">Work item whose Message-ID and backbone are logged.</param>
        /// <param name="outcome">Terminal processing outcome.</param>
        /// <param name="reason">Validation failure reason. Empty for every other outcome.</param>
        /// <param name="commandStartedTimestamp">ARTICLE send timestamp. Zero when the command was not sent.</param>
        private void LogArticleProcessed(
            ArticleWorkItem item,
            string outcome,
            string reason,
            long commandStartedTimestamp)
        {
            ArticleWorkLogMessages.ArticleProcessed(
                _logger,
                DisplayIdentity(item.Request.MessageId),
                DisplayIdentity(item.Request.Backbone),
                outcome,
                reason,
                FormatElapsed(commandStartedTimestamp));
        }

        /// <summary>Maps a non-retrieved download to the article-processing outcome and validation reason.</summary>
        /// <param name="retrieval">Download that is not <see cref="ArticleRetrievalKind.ArticleRetrieved"/>.</param>
        /// <returns>
        /// <c>NotFound</c> for a miss, <c>ValidationFailed</c> with <see cref="ArticleRetrievalResult.Reason"/> for an invalid article,
        /// and <c>Failed</c> for every other kind.
        /// </returns>
        private static (string Outcome, string Reason) RetrievalLog(ArticleRetrievalResult retrieval)
        {
            return retrieval.Kind switch
            {
                ArticleRetrievalKind.ArticleNotFound => (OutcomeNotFound, string.Empty),
                ArticleRetrievalKind.InvalidArticle => (OutcomeValidationFailed, retrieval.Reason),
                _ => (OutcomeFailed, string.Empty),
            };
        }

        /// <summary>Reads the ARTICLE send timestamp stored on <paramref name="exception"/>.</summary>
        /// <param name="exception">Exception from retrieval.</param>
        /// <returns>The stored timestamp, or zero when ARTICLE was not sent.</returns>
        private static long CommandTimestamp(Exception exception)
        {
            return exception.Data[ArticleRetrievalResult.CommandStartedTimestampKey] is long timestamp
                ? timestamp
                : 0L;
        }

        /// <summary>Formats elapsed seconds from an ARTICLE send timestamp as <c>0.000</c>.</summary>
        /// <param name="commandStartedTimestamp">Timestamp from <see cref="Stopwatch.GetTimestamp"/>. Zero formats as <c>0.000</c>.</param>
        /// <returns>Invariant three-decimal seconds.</returns>
        private static string FormatElapsed(long commandStartedTimestamp)
        {
            if (commandStartedTimestamp == 0)
            {
                return "0.000";
            }

            var seconds = Stopwatch.GetElapsedTime(commandStartedTimestamp).TotalSeconds;
            if (double.IsNaN(seconds) || seconds < 0)
            {
                seconds = 0;
            }

            return seconds.ToString("0.000", CultureInfo.InvariantCulture);
        }

        /// <summary>Uses the request identity, or the Article Work <c>(none)</c> placeholder when it is missing.</summary>
        /// <param name="value">Message-ID or backbone from the request.</param>
        /// <returns><paramref name="value"/> when it contains a non-whitespace character; otherwise <c>(none)</c>.</returns>
        private static string DisplayIdentity(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? UnresolvedIdentity : value;
        }

        /// <summary>Maps a parse-failure code to a terminal invalid-article result.</summary>
        /// <param name="failureCode">Parser failure code. The result error text is its name.</param>
        /// <returns>
        /// <see cref="ArticleWorkOutcome.InvalidArticle"/> with no success routing fields.
        /// </returns>
        private static ArticleWorkHandlerResult MapParseFailure(NntpArticleParseFailureCode failureCode)
        {
            return new ArticleWorkHandlerResult(
                ArticleWorkOutcome.InvalidArticle,
                failureCode.ToString());
        }

        /// <summary>Maps a non-retrieved retrieval result to an Article Work outcome.</summary>
        /// <param name="retrieval">Retrieval that is not <see cref="ArticleRetrievalKind.ArticleRetrieved"/>.</param>
        /// <returns>
        /// Article-not-found and invalid-article stay terminal and copy <see cref="ArticleRetrievalResult.Reason"/>.
        /// Cancelled carries a null error. Authentication failure and every other kind become
        /// <see cref="ArticleWorkOutcome.ProviderFailure"/> with that reason.
        /// </returns>
        private static ArticleWorkHandlerResult MapRetrieval(ArticleRetrievalResult retrieval)
        {
            return retrieval.Kind switch
            {
                ArticleRetrievalKind.ArticleNotFound => new ArticleWorkHandlerResult(
                    ArticleWorkOutcome.ArticleNotFound,
                    retrieval.Reason),
                ArticleRetrievalKind.InvalidArticle => new ArticleWorkHandlerResult(
                    ArticleWorkOutcome.InvalidArticle,
                    retrieval.Reason),
                ArticleRetrievalKind.Cancelled => new ArticleWorkHandlerResult(
                    ArticleWorkOutcome.Cancelled,
                    null),
                _ => new ArticleWorkHandlerResult(ArticleWorkOutcome.ProviderFailure, retrieval.Reason),
            };
        }
    }
}
