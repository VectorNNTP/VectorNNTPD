using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles.Processing;
using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.NntpDb;
using VectorNNTPD.TransitService.RabbitMq.ArticleWork;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Session.Framing;
using VectorNNTPD.TransitService.Storage;
using VectorNNTPD.TransitService.Transport.Vatp;

namespace VectorNNTPD.TransitService.Session.Commands
{
    /// <summary>
    /// ARTICLE, HEAD, BODY, and STAT as defined by RFC 3977, Sections 6.2.1–6.2.4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Message-id form asks <see cref="IStorageArticleLookupClient"/> first. A positive
    /// StorageServer response is fetched with the existing VATP client
    /// (<see cref="IVatpArticleClient"/>). Only a completed lookup with no positive
    /// response calls ArticleWork. When <see cref="IStorageServerRegistry"/> has no
    /// entries, that client does not publish a StorageServer request and the same miss
    /// continues to ArticleWork. An empty registry is not an article-not-found result.
    /// Transport, malformed, and invalid StorageServer results stay on that path. A validated CanonicalV1
    /// <see cref="ArticleRecord"/> is required before any <c>220</c>/<c>221</c>/<c>222</c>/<c>223</c>
    /// reply. A StorageServer success is served without ingestion. A BackFiller success
    /// admits a Path traversal whose current hop is this NNTPD, and the reply sends
    /// that record. A StorageServer success traverses once on ingress and the reply
    /// sends that record. A failed traversal still sends the fetched bytes.
    /// </para>
    /// <para>
    /// Numeric article lookup and omitted current-article forms are unchanged:
    /// message-id unavailable → <c>430</c>, article number → <c>412</c>/<c>423</c>,
    /// omitted current article → <c>412</c>/<c>420</c>. Temporary transfer failures use
    /// <c>400</c> without closing the connection. Error replies are single-line.
    /// </para>
    /// <para>
    /// An unsuccessful lookup MUST NOT change the selected group or current article
    /// (RFC 3977 §6.2.1.2). There is no current-article pointer yet, so the omitted form
    /// after a successful GROUP is <c>420</c>.
    /// </para>
    /// </remarks>
    internal static class Article
    {
        private static ILogger Logger => NntpCommandLoggers.For(typeof(Article));

        private enum RetrievalKind
        {
            Article,
            Head,
            Body,
            Stat,
        }

        /// <summary>Handles <c>ARTICLE</c> (RFC 3977, Section 6.2.1).</summary>
        public static ValueTask HandleArticleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(Logger, context, "ARTICLE", static (c, ct) => ExecuteAsync(c, RetrievalKind.Article, ct), cancellationToken);

        /// <summary>Handles <c>HEAD</c> (RFC 3977, Section 6.2.2).</summary>
        public static ValueTask HandleHeadAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(Logger, context, "HEAD", static (c, ct) => ExecuteAsync(c, RetrievalKind.Head, ct), cancellationToken);

        /// <summary>Handles <c>BODY</c> (RFC 3977, Section 6.2.3).</summary>
        public static ValueTask HandleBodyAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(Logger, context, "BODY", static (c, ct) => ExecuteAsync(c, RetrievalKind.Body, ct), cancellationToken);

        /// <summary>Handles <c>STAT</c> (RFC 3977, Section 6.2.4).</summary>
        public static ValueTask HandleStatAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(Logger, context, "STAT", static (c, ct) => ExecuteAsync(c, RetrievalKind.Stat, ct), cancellationToken);

        private static async ValueTask ExecuteAsync(
            NntpCommandContext context,
            RetrievalKind kind,
            CancellationToken cancellationToken)
        {
            var argument = context.ArgumentSpan;
            if (argument.IsEmpty)
            {
                await WriteCurrentArticleFailureAsync(context, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (argument[0] == (byte)'<')
            {
                await ExecuteMessageIdAsync(context, kind, cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteNumberLookupFailureAsync(context, cancellationToken).ConfigureAwait(false);
        }

        private static async ValueTask ExecuteMessageIdAsync(
            NntpCommandContext context,
            RetrievalKind kind,
            CancellationToken cancellationToken)
        {
            var resolved = await ResolveByMessageIdAsync(context, cancellationToken).ConfigureAwait(false);
            switch (resolved.Kind)
            {
                case ResolveKind.NotFound:
                    await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.NoArticleWithMessageId,
                        NntpResponseStatus.NoArticleWithMessageId,
                        cancellationToken).ConfigureAwait(false);
                    return;
                case ResolveKind.TemporaryFailure:
                    await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.ServiceTemporarilyUnavailable,
                        NntpResponseStatus.ServiceTemporarilyUnavailable,
                        cancellationToken).ConfigureAwait(false);
                    return;
                case ResolveKind.Success:
                    break;
                default:
                    await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.ServiceTemporarilyUnavailable,
                        NntpResponseStatus.ServiceTemporarilyUnavailable,
                        cancellationToken).ConfigureAwait(false);
                    return;
            }

            var record = resolved.Record;
            if (resolved.Source == RetrievalSource.BackFiller
                && TryAdmitBackFiller(context, in record, out var admitted))
            {
                record = admitted;
            }
            else if (resolved.Source == RetrievalSource.StorageServer
                && TryTraverseStorageIngress(context, in record, out var entered))
            {
                record = entered;
            }

            var messageIdText = Encoding.ASCII.GetString(record.MessageId);
            switch (kind)
            {
                case RetrievalKind.Article:
                    await context.Response.WriteCustomerArticleAsync(
                        record.ArtData,
                        messageIdText,
                        articleNumber: 0,
                        cancellationToken).ConfigureAwait(false);
                    NntpCommandReply.TryNote(context, Logger, "220 0 " + messageIdText);
                    return;
                case RetrievalKind.Head:
                    if (!ArticleWireReconstructor.TrySplitHeadersAndBody(record.ArtData.Span, out var headersSpan, out _))
                    {
                        headersSpan = record.ArtData.Span;
                    }

                    var headers = record.ArtData[..headersSpan.Length];
                    await context.Response.WriteCustomerHeadAsync(
                        headers,
                        messageIdText,
                        articleNumber: 0,
                        cancellationToken).ConfigureAwait(false);
                    NntpCommandReply.TryNote(context, Logger, "221 0 " + messageIdText);
                    return;
                case RetrievalKind.Body:
                    _ = ArticleWireReconstructor.TrySplitHeadersAndBody(record.ArtData.Span, out var headerSpan, out var bodySpan);
                    var body = bodySpan.IsEmpty
                        ? ReadOnlyMemory<byte>.Empty
                        : record.ArtData.Slice(headerSpan.Length, bodySpan.Length);
                    await context.Response.WriteCustomerBodyFromBodyAsync(
                        body,
                        messageIdText,
                        articleNumber: 0,
                        cancellationToken).ConfigureAwait(false);
                    NntpCommandReply.TryNote(context, Logger, "222 0 " + messageIdText);
                    return;
                case RetrievalKind.Stat:
                    await context.Response.WriteCustomerStatAsync(record.MessageId, articleNumber: 0, cancellationToken)
                        .ConfigureAwait(false);
                    NntpCommandReply.TryNote(context, Logger, "223 0 " + messageIdText);
                    return;
                default:
                    await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.ServiceTemporarilyUnavailable,
                        NntpResponseStatus.ServiceTemporarilyUnavailable,
                        cancellationToken).ConfigureAwait(false);
                    return;
            }
        }

        private static async ValueTask<ResolveResult> ResolveByMessageIdAsync(
            NntpCommandContext context,
            CancellationToken cancellationToken)
        {
            var messageId = context.ArgumentMemory;
            var storage = context.Session.StorageArticleLookup;
            if (storage is null)
            {
                return ResolveResult.NotFound();
            }

            var articleId = ArticleId.FromMessageId(messageId.Span);
            var logger = Logger;
            StorageArticleLookupResult fleet;
            try
            {
                fleet = await storage.LookupAsync(articleId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    var articleIdText = articleId.ToLowerHexString();
                    ArticleRetrievalLogMessages.StorageLookupCompleted(
                        logger,
                        "unavailable",
                        articleIdText,
                        Guid.Empty);
                }
                return ResolveResult.Temporary();
            }

            if (fleet.Outcome == StorageArticleLookupOutcome.Found)
            {
                return await FetchPositiveStorageAsync(context, messageId, articleId, fleet, cancellationToken)
                    .ConfigureAwait(false);
            }

            var silence = string.Equals(fleet.Error, StorageLookupSilence, StringComparison.Ordinal);
            if (logger.IsEnabled(LogLevel.Debug))
            {
                var articleIdText = articleId.ToLowerHexString();
                ArticleRetrievalLogMessages.StorageLookupCompleted(
                    logger,
                    silence ? "miss" : "unavailable",
                    articleIdText,
                    fleet.RequestId);
            }
            if (!silence)
            {
                return ResolveResult.Temporary();
            }

            return await ResolveFromArticleWorkAsync(context, messageId, cancellationToken).ConfigureAwait(false);
        }

        private static async ValueTask<ResolveResult> ResolveFromArticleWorkAsync(
            NntpCommandContext context,
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken)
        {
            var rpc = context.Session.ArticleWorkRpc;
            if (rpc is null)
            {
                return ResolveResult.NotFound();
            }

            ArticleWorkRpcResult lookup;
            try
            {
                lookup = await rpc.LookupByMessageIdAsync(messageId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                ArticleWorkRpcLogMessages.ArticleLookupFailed(
                    Logger,
                    ex,
                    Encoding.ASCII.GetString(messageId.Span));
                return ResolveResult.Temporary();
            }

            if (lookup.Outcome is ArticleWorkOutcome.ArticleNotFound or ArticleWorkOutcome.InvalidArticle)
            {
                return ResolveResult.NotFound();
            }

            if (lookup.Outcome != ArticleWorkOutcome.Success
                || lookup.ArticleId is not { } expectedArtId
                || !VatpEndpointFields.IsCanonicalFqdn(lookup.Fqdn)
                || lookup.VatpPort is not int vatpPort
                || !VatpEndpointFields.IsCanonicalPort(vatpPort))
            {
                ArticleRetrievalLogMessages.TransferUnavailable(
                    Logger,
                    lookup.Outcome.ToString(),
                    lookup.Error);
                return ResolveResult.Temporary();
            }

            var vatp = context.Session.VatpArticleClient;
            if (vatp is null)
            {
                ArticleRetrievalLogMessages.TransferUnavailable(
                    Logger,
                    "VatpClientMissing",
                    EndpointDetail(lookup.Fqdn, vatpPort));
                return ResolveResult.Temporary();
            }

            VatpFetchResult fetch;
            try
            {
                fetch = await vatp.FetchArticleAsync(
                    lookup.Fqdn,
                    vatpPort,
                    lookup.RequestId,
                    expectedArtId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                ArticleRetrievalLogMessages.VatpFetchFailed(Logger, ex, lookup.Fqdn, vatpPort, lookup.RequestId);
                return ResolveResult.Temporary();
            }

            return WithSource(MapFetch(fetch, messageId.Span, expectedArtId), RetrievalSource.BackFiller);
        }

        /// <summary>
        /// Silence from the fleet lookup. Matches <c>StorageArticleLookupService</c> when no
        /// positive response arrives before the existing lookup budget.
        /// </summary>
        private const string StorageLookupSilence = "Storage article lookup timed out with no positive response.";

        private static async ValueTask<ResolveResult> FetchPositiveStorageAsync(
            NntpCommandContext context,
            ReadOnlyMemory<byte> messageId,
            ArticleId articleId,
            StorageArticleLookupResult fleet,
            CancellationToken cancellationToken)
        {
            var logger = Logger;
            if (fleet.ArticleId != articleId || !TryBindStorageEndpoint(fleet.Fqdn, fleet.VatpPort, out var host, out var port))
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    var articleIdText = articleId.ToLowerHexString();
                    ArticleRetrievalLogMessages.StorageLookupCompleted(
                        logger,
                        "unavailable",
                        articleIdText,
                        fleet.RequestId);
                }

                ArticleRetrievalLogMessages.TransferUnavailable(Logger, "StorageEndpoint", EndpointDetail(fleet.Fqdn, fleet.VatpPort));
                return ResolveResult.Temporary();
            }

            var vatp = context.Session.VatpArticleClient;
            if (vatp is null)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    var articleIdText = articleId.ToLowerHexString();
                    ArticleRetrievalLogMessages.StorageLookupCompleted(
                        logger,
                        "found",
                        articleIdText,
                        fleet.RequestId);
                }

                ArticleRetrievalLogMessages.TransferUnavailable(Logger, "VatpClientMissing", EndpointDetail(host, port));
                return ResolveResult.Temporary();
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                var articleIdText = articleId.ToLowerHexString();
                ArticleRetrievalLogMessages.StorageLookupCompleted(
                    logger,
                    "found",
                    articleIdText,
                    fleet.RequestId);
            }

            var serverId = fleet.ServerId ?? 0;
            VatpFetchResult fetch;
            try
            {
                fetch = await FetchStorageCandidateAsync(
                    vatp,
                    host,
                    port,
                    fleet.RequestId,
                    articleId,
                    serverId,
                    attempt: 1,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception)
            {
                return ResolveResult.Temporary();
            }

            if (IsAlternateEligible(fetch) && fleet.Alternates is not null)
            {
                // Caller cancellation is terminal even when an alternate is already retained
                // or the same cancel has already closed the lookup window.
                cancellationToken.ThrowIfCancellationRequested();
                var alternate = await fleet.Alternates.WaitForAlternateAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (alternate is { } next)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        fetch = await FetchStorageCandidateAsync(
                            vatp,
                            next.Fqdn,
                            next.VatpPort,
                            fleet.RequestId,
                            articleId,
                            next.ServerId,
                            attempt: 2,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception)
                    {
                        return ResolveResult.Temporary();
                    }
                }
                else
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        var articleIdText = articleId.ToLowerHexString();
                        ArticleRetrievalLogMessages.StorageCandidateExhausted(
                            logger,
                            articleIdText,
                            fleet.RequestId,
                            1);
                    }
                }
            }

            return WithSource(MapFetch(fetch, messageId.Span, articleId), RetrievalSource.StorageServer);
        }

        private static async ValueTask<VatpFetchResult> FetchStorageCandidateAsync(
            IVatpArticleClient vatp,
            string fqdn,
            int vatpPort,
            Guid requestId,
            ArticleId articleId,
            int serverId,
            int attempt,
            CancellationToken cancellationToken)
        {
            var logger = Logger;
            if (logger.IsEnabled(LogLevel.Debug))
            {
                var articleIdText = articleId.ToLowerHexString();
                ArticleRetrievalLogMessages.StorageCandidateSelected(
                    logger,
                    articleIdText,
                    requestId,
                    serverId,
                    attempt);
            }
            try
            {
                var fetch = await vatp.FetchArticleAsync(fqdn, vatpPort, requestId, articleId, cancellationToken)
                    .ConfigureAwait(false);
                if (fetch.Kind != VatpFetchKind.Success && logger.IsEnabled(LogLevel.Debug))
                {
                    var articleIdText = articleId.ToLowerHexString();
                    var kind = fetch.Kind.ToString();
                    var failoverEligible = IsAlternateEligible(fetch);
                    ArticleRetrievalLogMessages.StorageCandidateFailed(
                        logger,
                        articleIdText,
                        requestId,
                        serverId,
                        attempt,
                        kind,
                        fetch.AcceptedDataBytes,
                        failoverEligible);
                }

                return fetch;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                ArticleRetrievalLogMessages.VatpFetchFailed(Logger, ex, fqdn, vatpPort, requestId);
                throw;
            }
        }

        /// <summary>
        /// A second candidate is allowed only when no DATA payload was copied and the failure
        /// is connection, remote transfer, or protocol. Every other result is terminal.
        /// </summary>
        private static bool IsAlternateEligible(VatpFetchResult fetch) =>
            fetch.AcceptedDataBytes == 0
            && fetch.Kind is VatpFetchKind.ConnectionFailure
                or VatpFetchKind.RemoteTransferFailure
                or VatpFetchKind.ProtocolFailure;

        private static bool TryBindStorageEndpoint(string? fqdn, int? vatpPort, out string host, out int port)
        {
            host = string.Empty;
            port = 0;
            if (!VatpEndpointFields.IsCanonicalFqdn(fqdn) || vatpPort is not int parsed || !VatpEndpointFields.IsCanonicalPort(parsed))
            {
                return false;
            }

            host = fqdn;
            port = parsed;
            return true;
        }

        private static string EndpointDetail(string? fqdn, int? vatpPort) =>
            FormattableString.Invariant($"fqdn={fqdn} port={vatpPort}");

        private static ResolveResult MapFetch(
            VatpFetchResult fetch,
            ReadOnlySpan<byte> requestedMessageId,
            ArticleId expectedArtId)
        {
            switch (fetch.Kind)
            {
                case VatpFetchKind.Success:
                    break;
                case VatpFetchKind.Cancelled:
                    throw new OperationCanceledException();
                default:
                    ArticleRetrievalLogMessages.VatpFetchUnsuccessful(
                        Logger,
                        fetch.Kind.ToString(),
                        fetch.Error,
                        fetch.ErrorCode?.ToString());
                    return ResolveResult.Temporary();
            }

            var record = fetch.Record;
            if (record.ParseStatus != ArticleParseStatus.CanonicalV1 || record.ArtSize <= 0)
            {
                ArticleRetrievalLogMessages.VatpFetchUnsuccessful(
                    Logger,
                    "IncompleteOrMalformedArticle",
                    fetch.Error,
                    fetch.ErrorCode?.ToString());
                return ResolveResult.Temporary();
            }

            if (!record.ArtId.Equals(expectedArtId)
                || !NntpArticleIdentity.MatchesRequest(record.MessageId, requestedMessageId))
            {
                ArticleRetrievalLogMessages.IdentityMismatch(Logger, fetch.Error);
                return ResolveResult.NotFound();
            }

            return ResolveResult.Success(record, RetrievalSource.None);
        }

        /// <summary>
        /// Keeps a failure result unchanged and stamps <paramref name="source"/> only on success.
        /// </summary>
        private static ResolveResult WithSource(ResolveResult result, RetrievalSource source) =>
            result.Kind == ResolveKind.Success
                ? ResolveResult.Success(result.Record, source)
                : result;

        /// <summary>
        /// Records this NNTPD as the receiving hop for a StorageServer article.
        /// </summary>
        /// <returns><see langword="false"/> when traversal fails. The caller keeps the fetched record.</returns>
        private static bool TryTraverseStorageIngress(
            NntpCommandContext context,
            in ArticleRecord record,
            out ArticleRecord entered)
        {
            entered = default;
            NntpArticlePolicyCapture.Capture(
                context.Session,
                ArticleResourceLimits.MaxArticleBytes,
                out var maxArticleBytes,
                out var siteNameUtf8);
            var created = siteNameUtf8 is null
                ? ArticleRecordFactory.TryCreate(
                    context.Session.ArticleParser,
                    record.ArtData,
                    ArticlePathMode.Traverse)
                : ArticleRecordFactory.TryCreate(
                    context.Session.ArticleParser,
                    record.ArtData,
                    ArticlePathMode.Traverse,
                    maxArticleBytes,
                    siteNameUtf8);
            if (!created.IsAccepted || created.Record.ArtId != record.ArtId)
            {
                return false;
            }

            entered = created.Record;
            return true;
        }

        /// <summary>
        /// Queues the Path-canonical copy and returns that same record for the current reply.
        /// </summary>
        /// <returns><see langword="false"/> when canonicalization or queue admission fails.</returns>
        private static bool TryAdmitBackFiller(
            NntpCommandContext context,
            in ArticleRecord record,
            out ArticleRecord admitted)
        {
            admitted = default;
            try
            {
                var logger = Logger;
                NntpArticlePolicyCapture.Capture(
                    context.Session,
                    ArticleResourceLimits.MaxArticleBytes,
                    out var maxArticleBytes,
                    out var siteNameUtf8);
                var created = siteNameUtf8 is null
                    ? ArticleRecordFactory.TryCreate(
                        context.Session.ArticleParser,
                        record.ArtData,
                        ArticlePathMode.Traverse)
                    : ArticleRecordFactory.TryCreate(
                        context.Session.ArticleParser,
                        record.ArtData,
                        ArticlePathMode.Traverse,
                        maxArticleBytes,
                        siteNameUtf8);
                if (!created.IsAccepted || created.Record.ArtId != record.ArtId)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        var reason = created.ParseFailure != NntpArticleParseFailureCode.None
                            ? created.ParseFailure.ToString()
                            : created.MaterializeFailure.ToString();
                        var rejectedMessageId = Encoding.ASCII.GetString(record.MessageId);
                        ArticleRetrievalLogMessages.IngestNotAdmitted(
                            logger,
                            reason,
                            rejectedMessageId);
                    }
                    return false;
                }

                admitted = created.Record;
                var messageIdText = Encoding.ASCII.GetString(admitted.MessageId);
                var inbound = ArticleRecordIngress.CreateQueued(
                    messageIdText,
                    in admitted,
                    context.Session.ClientIdentity,
                    DateTimeOffset.UtcNow,
                    InboundArticleProducer.BackFiller);

                var enqueue = context.Session.ArticleIngestion.TryAdmit(inbound);
                if (enqueue == ArticleEnqueueResult.Accepted)
                {
                    // Copy Message-ID octets once for History; do not invent a second History path.
                    var midBytes = record.MessageId.ToArray();
                    context.Session.HistoryDb?.Remember(midBytes);
                    return true;
                }

                admitted = default;
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    var result = enqueue.ToString();
                    ArticleRetrievalLogMessages.IngestNotAdmitted(logger, result, messageIdText);
                }
                return false;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                admitted = default;
                ArticleRetrievalLogMessages.IngestFailed(Logger, ex);
                return false;
            }
        }

        private static ValueTask WriteNumberLookupFailureAsync(
            NntpCommandContext context,
            CancellationToken cancellationToken)
        {
            if (!context.Session.HasSelectedGroup)
            {
                return NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.NoNewsgroupSelected,
                    NntpResponseStatus.NoNewsgroupSelected,
                    cancellationToken);
            }

            return NntpCommandReply.WriteAsync(
                context,
                Logger,
                NntpResponses.NoArticleWithNumber,
                NntpResponseStatus.NoArticleWithNumber,
                cancellationToken);
        }

        private static ValueTask WriteCurrentArticleFailureAsync(
            NntpCommandContext context,
            CancellationToken cancellationToken)
        {
            if (!context.Session.HasSelectedGroup)
            {
                return NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.NoNewsgroupSelected,
                    NntpResponseStatus.NoNewsgroupSelected,
                    cancellationToken);
            }

            return NntpCommandReply.WriteAsync(
                context,
                Logger,
                NntpResponses.CurrentArticleNumberInvalid,
                NntpResponseStatus.CurrentArticleNumberInvalid,
                cancellationToken);
        }

        private enum RetrievalSource
        {
            None = 0,
            StorageServer = 1,
            BackFiller = 2,
        }

        private enum ResolveKind
        {
            NotFound,
            TemporaryFailure,
            Success,
        }

        private readonly struct ResolveResult
        {
            private ResolveResult(ResolveKind kind, ArticleRecord record, RetrievalSource source)
            {
                Kind = kind;
                Record = record;
                Source = source;
            }

            public ResolveKind Kind { get; }

            public ArticleRecord Record { get; }

            public RetrievalSource Source { get; }

            public static ResolveResult NotFound() => new(ResolveKind.NotFound, default, RetrievalSource.None);

            public static ResolveResult Temporary() => new(ResolveKind.TemporaryFailure, default, RetrievalSource.None);

            public static ResolveResult Success(ArticleRecord record, RetrievalSource source) =>
                new(ResolveKind.Success, record, source);
        }
    }
}
