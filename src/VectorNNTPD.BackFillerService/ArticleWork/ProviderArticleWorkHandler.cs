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

        /// <summary>
        /// Fallback parser factory used when no shared catalogue is configured or the server id is
        /// outside <see cref="ServerIdRules"/>. Must return a parser for the current runtime FQDN.
        /// </summary>
        private readonly Func<NntpArticleParser> _fallbackParser;

        /// <summary>Published shared configuration. Null when the handler keeps the injected parser and a zero article limit.</summary>
        private readonly INntpSharedConfigurationCatalogue? _sharedConfiguration;

        /// <summary>Server id used with <see cref="NntpSharedConfiguration.DnsSuffix"/> for this article's FQDN. Zero keeps the injected parser.</summary>
        private readonly int _serverId;

        /// <summary>Logger for the single article-processing Information event.</summary>
        private readonly ILogger<ProviderArticleWorkHandler> _logger;

        /// <summary>
        /// Pool for canonical article arrays. Production uses <see cref="ArrayPool{T}.Shared"/>.
        /// </summary>
        private readonly ArrayPool<byte> _canonicalBufferPool;

        /// <summary>
        /// Parser, UTF-8 site name, article limit, and endpoint FQDN taken from one shared snapshot.
        /// Null until the first article that has a catalogue.
        /// </summary>
        /// <remarks>
        /// <c>nntpsharedconfig</c> has no generation. The snapshot identity is
        /// <see cref="NntpSharedConfiguration.MaxArticleBytes"/>, <see cref="NntpSharedConfiguration.SiteName"/>,
        /// and <see cref="NntpSharedConfiguration.DnsSuffix"/> read together from one
        /// <see cref="INntpSharedConfigurationCatalogue.Current"/>.
        /// Replaced by a volatile write. The fast path does not take <see cref="_sharedArticleGate"/>.
        /// </remarks>
        private SharedArticlePreparation? _sharedArticleState;

        /// <summary>
        /// Serializes a rebuild of <see cref="_sharedArticleState"/>.
        /// Not taken when the published preparation already matches the snapshot just read.
        /// </summary>
        private readonly Lock _sharedArticleGate = new();

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
        /// <param name="sharedConfiguration">
        /// Published <c>nntpsharedconfig</c>. Read on each article. Derived parser state is reused until that snapshot's
        /// article limit, site name, or DNS suffix changes.
        /// </param>
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
        /// <param name="sharedConfiguration">
        /// Published <c>nntpsharedconfig</c>. Read on each article. Derived parser state is reused until that snapshot's
        /// article limit, site name, or DNS suffix changes.
        /// </param>
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
            : this(
                retriever,
                retention,
                () => parser,
                sharedConfiguration,
                serverId,
                logger,
                canonicalBufferPool)
        {
            ArgumentNullException.ThrowIfNull(parser);
        }

        /// <summary>Initializes the handler with a live fallback-parser factory (host DI).</summary>
        /// <param name="retriever">NNTP ARTICLE retriever.</param>
        /// <param name="retention">Canonical retention authority.</param>
        /// <param name="fallbackParser">
        /// Returns the CanonicalV1 parser for the current runtime FQDN when the server id is outside
        /// <see cref="ServerIdRules"/> or when no shared catalogue is configured.
        /// </param>
        /// <param name="sharedConfiguration">Published <c>nntpsharedconfig</c>, or null.</param>
        /// <param name="serverId">BackFiller server id combined with <c>dnssuffix</c> for the Path hop and retained endpoint.</param>
        /// <param name="logger">Article-processing logger. Null uses <see cref="NullLogger{T}.Instance"/>.</param>
        /// <param name="canonicalBufferPool">
        /// Pool for the canonical article array. Null uses <see cref="ArrayPool{T}.Shared"/>.
        /// </param>
        internal ProviderArticleWorkHandler(
            INntpArticleRetriever retriever,
            IArticleRetentionAuthority retention,
            Func<NntpArticleParser> fallbackParser,
            INntpSharedConfigurationCatalogue? sharedConfiguration,
            int serverId,
            ILogger<ProviderArticleWorkHandler>? logger = null,
            ArrayPool<byte>? canonicalBufferPool = null)
        {
            ArgumentNullException.ThrowIfNull(retriever);
            ArgumentNullException.ThrowIfNull(retention);
            ArgumentNullException.ThrowIfNull(fallbackParser);
            _retriever = retriever;
            _retention = retention;
            _fallbackParser = fallbackParser;
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

        /// <summary>Parser from the cached shared-configuration preparation, or null when none has been published (tests).</summary>
        internal NntpArticleParser? CachedParser => Volatile.Read(ref _sharedArticleState)?.Parser;

        /// <summary>UTF-8 site name from that same preparation, or null when none has been published (tests).</summary>
        internal byte[]? CachedSiteNameUtf8 => Volatile.Read(ref _sharedArticleState)?.SiteNameUtf8;

        /// <summary>
        /// Endpoint FQDN from that same preparation.
        /// Null when none has been published or <see cref="ServerIdRules.IsInRange"/> rejected the server id (tests).
        /// </summary>
        internal string? CachedEndpointFqdn => Volatile.Read(ref _sharedArticleState)?.EndpointFqdn;

        /// <summary>Article byte limit from that same preparation. Null when none has been published (tests).</summary>
        internal int? CachedMaxArticleBytes
        {
            get
            {
                var state = Volatile.Read(ref _sharedArticleState);
                return state is null ? null : state.MaxArticleBytes;
            }
        }

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
        /// session's <c>NntpSessionOptions.MaxArticleBytes</c> remains in force, and the injected parser is used.
        /// A supplied catalogue passes one snapshot's <c>MaxArticleBytes</c>, UTF-8 site name, and, when
        /// <see cref="ServerIdRules.IsInRange"/> accepts the server id, the FQDN and parser built from that same snapshot.
        /// Those derived objects are reused until the snapshot's article limit, site name, or DNS suffix changes.
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
            var parser = _fallbackParser();
            string? endpointFqdn = null;
            if (_sharedConfiguration is not null)
            {
                var prepared = ResolveSharedArticlePreparation();
                maxArticleBytes = prepared.MaxArticleBytes;
                siteNameUtf8 = prepared.SiteNameUtf8;
                parser = prepared.Parser;
                endpointFqdn = prepared.EndpointFqdn;
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

                    if (!record.Framing.IsFrameable)
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
                ElapsedSeconds(commandStartedTimestamp));
        }

        /// <summary>
        /// Returns the preparation for <see cref="INntpSharedConfigurationCatalogue.Current"/>, rebuilding it when that snapshot's
        /// article limit, site name, or DNS suffix differs from the published preparation.
        /// </summary>
        /// <returns>One immutable preparation. Every field comes from the snapshot read for that rebuild.</returns>
        /// <exception cref="InvalidOperationException"><see cref="INntpSharedConfigurationCatalogue.Current"/> has not been published.</exception>
        /// <remarks>
        /// The fast path reads the catalogue and the published reference without <see cref="_sharedArticleGate"/>.
        /// A miss takes the gate, reads the catalogue again, and publishes only a preparation built from that second read.
        /// An exception from FQDN construction or parser construction leaves the previous preparation in place and propagates.
        /// </remarks>
        private SharedArticlePreparation ResolveSharedArticlePreparation()
        {
            var shared = _sharedConfiguration!.Current;
            var current = Volatile.Read(ref _sharedArticleState);
            if (current is not null && current.Matches(shared))
            {
                return current;
            }

            lock (_sharedArticleGate)
            {
                shared = _sharedConfiguration.Current;
                current = Volatile.Read(ref _sharedArticleState);
                if (current is not null && current.Matches(shared))
                {
                    return current;
                }

                var built = SharedArticlePreparation.Create(shared, _serverId, _fallbackParser());
                Volatile.Write(ref _sharedArticleState, built);
                return built;
            }
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

        /// <summary>Returns elapsed seconds from an ARTICLE send timestamp.</summary>
        /// <param name="commandStartedTimestamp">Timestamp from <see cref="Stopwatch.GetTimestamp"/>. Zero returns zero.</param>
        /// <returns>Non-negative seconds. Not formatted.</returns>
        private static double ElapsedSeconds(long commandStartedTimestamp)
        {
            if (commandStartedTimestamp == 0)
            {
                return 0d;
            }

            var seconds = Stopwatch.GetElapsedTime(commandStartedTimestamp).TotalSeconds;
            if (double.IsNaN(seconds) || seconds < 0)
            {
                return 0d;
            }

            return seconds;
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

        /// <summary>
        /// Immutable parser inputs derived from one <see cref="NntpSharedConfiguration"/> value.
        /// </summary>
        /// <remarks>
        /// <see cref="Matches"/> compares the three fields that feed this instance. A later snapshot that differs in any
        /// of them causes a new instance. The parser, site-name bytes, limit, and endpoint on one instance are not updated.
        /// </remarks>
        private sealed class SharedArticlePreparation
        {
            /// <summary>Stores derived fields that were built together from one snapshot.</summary>
            /// <param name="maxArticleBytes">Article limit from that snapshot.</param>
            /// <param name="siteName">Site name from that snapshot.</param>
            /// <param name="dnsSuffix">DNS suffix from that snapshot.</param>
            /// <param name="siteNameUtf8">UTF-8 encoding of <paramref name="siteName"/>.</param>
            /// <param name="endpointFqdn">FQDN built from that snapshot, or null when the server id is outside <see cref="ServerIdRules"/>.</param>
            /// <param name="parser">Parser for <paramref name="endpointFqdn"/>, or the injected parser when the endpoint is null.</param>
            private SharedArticlePreparation(
                int maxArticleBytes,
                string siteName,
                string dnsSuffix,
                byte[] siteNameUtf8,
                string? endpointFqdn,
                NntpArticleParser parser)
            {
                MaxArticleBytes = maxArticleBytes;
                SiteName = siteName;
                DnsSuffix = dnsSuffix;
                SiteNameUtf8 = siteNameUtf8;
                EndpointFqdn = endpointFqdn;
                Parser = parser;
            }

            /// <summary>Article limit copied from the snapshot.</summary>
            internal int MaxArticleBytes { get; }

            /// <summary>Site name copied from the snapshot. Compared ordinally by <see cref="Matches"/>.</summary>
            private string SiteName { get; }

            /// <summary>DNS suffix copied from the snapshot. Compared ordinally by <see cref="Matches"/>.</summary>
            private string DnsSuffix { get; }

            /// <summary>UTF-8 encoding of <see cref="SiteName"/> from the same snapshot.</summary>
            internal byte[] SiteNameUtf8 { get; }

            /// <summary>Application FQDN for the same snapshot. Null when the server id is outside <see cref="ServerIdRules"/>.</summary>
            internal string? EndpointFqdn { get; }

            /// <summary>Parser for <see cref="EndpointFqdn"/>, or the injected parser when the server id is outside <see cref="ServerIdRules"/>.</summary>
            internal NntpArticleParser Parser { get; }

            /// <summary>Builds site-name bytes, and the FQDN and parser when <paramref name="serverId"/> is in range, from one snapshot.</summary>
            /// <param name="shared">Snapshot that supplies the limit, site name, and DNS suffix.</param>
            /// <param name="serverId">Handler server id. Outside <see cref="ServerIdRules"/> keeps <paramref name="fallbackParser"/> and a null endpoint.</param>
            /// <param name="fallbackParser">Injected parser used when <paramref name="serverId"/> is outside the accepted range.</param>
            /// <returns>A preparation whose fields belong to <paramref name="shared"/>.</returns>
            /// <exception cref="ArgumentException"><paramref name="serverId"/> is in range and <see cref="NntpSharedConfiguration.DnsSuffix"/> is not a usable suffix.</exception>
            /// <exception cref="ArgumentNullException"><paramref name="shared"/> has a null site name, or <paramref name="fallbackParser"/> is null.</exception>
            internal static SharedArticlePreparation Create(
                in NntpSharedConfiguration shared,
                int serverId,
                NntpArticleParser fallbackParser)
            {
                ArgumentNullException.ThrowIfNull(fallbackParser);
                var siteNameUtf8 = Encoding.UTF8.GetBytes(shared.SiteName);
                string? endpointFqdn = null;
                var parser = fallbackParser;
                if (ServerIdRules.IsInRange(serverId))
                {
                    endpointFqdn = ApplicationFqdn.Build(BackFillerOptions.ApplicationPrefix, serverId, shared.DnsSuffix);
                    parser = new NntpArticleParser(endpointFqdn);
                }

                return new SharedArticlePreparation(
                    shared.MaxArticleBytes,
                    shared.SiteName,
                    shared.DnsSuffix,
                    siteNameUtf8,
                    endpointFqdn,
                    parser);
            }

            /// <summary>Returns whether <paramref name="shared"/> has the same article limit, site name, and DNS suffix as this instance.</summary>
            /// <param name="shared">Snapshot to compare. Other fields are not part of the derived state.</param>
            /// <returns><see langword="true"/> when a rebuild would produce the same derived inputs.</returns>
            internal bool Matches(in NntpSharedConfiguration shared)
            {
                return MaxArticleBytes == shared.MaxArticleBytes
                    && string.Equals(SiteName, shared.SiteName, StringComparison.Ordinal)
                    && string.Equals(DnsSuffix, shared.DnsSuffix, StringComparison.Ordinal);
            }
        }
    }
}
