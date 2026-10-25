namespace VectorNNTPD.BackFillerService.Nntp
{
    /// <summary>
    /// Outcome of one ARTICLE retrieval.
    /// An owned payload is present only for the exact-array <see cref="ArticleRetrievalKind.ArticleRetrieved"/> path.
    /// The callback success path leaves <see cref="Article"/> null after the caller has already consumed the bytes.
    /// </summary>
    /// <param name="Kind">Retrieval classification.</param>
    /// <param name="StatusCode">NNTP status when the server produced one.</param>
    /// <param name="Reason">Diagnostic text. Never a secret.</param>
    /// <param name="Article">Owned payload when retrieval succeeded.</param>
    /// <param name="SessionReusable">Whether the leased session may return to the idle pool.</param>
    internal sealed record ArticleRetrievalResult(
        ArticleRetrievalKind Kind,
        int? StatusCode,
        string Reason,
        RetrievedArticle? Article,
        bool SessionReusable) : IDisposable
    {
        /// <summary>
        /// <see cref="System.Diagnostics.Stopwatch"/> timestamp key stored on an exception that leaves download after ARTICLE was sent.
        /// </summary>
        internal const string CommandStartedTimestampKey = "VectorNNTPD.BackFillerService.ArticleCommandStartedTimestamp";

        /// <summary>
        /// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> captured immediately before ARTICLE was written.
        /// Zero when the command was not sent.
        /// </summary>
        internal long CommandStartedTimestamp { get; set; }

        /// <summary>
        /// Stable identity of the physical NNTP connection that performed this retrieval.
        /// </summary>
        /// <remarks>
        /// <see cref="Guid.Empty"/> when no session was leased (cancelled before acquire, missing pool, or connect failure
        /// before a session existed). Otherwise equals <see cref="NntpProviderSession.ConnectionId"/> for that lease.
        /// </remarks>
        internal Guid ConnectionId { get; set; }

        /// <summary>
        /// Exception.Data key for <see cref="ConnectionId"/> when download throws after a session was leased.
        /// </summary>
        internal const string ConnectionIdKey = "VectorNNTPD.BackFillerService.NntpConnectionId";

        /// <summary>Disposes the owned <see cref="Article"/> when one is present.</summary>
        public void Dispose()
        {
            Article?.Dispose();
        }

        /// <summary>Creates a successful retrieval that owns <paramref name="article"/>.</summary>
        /// <param name="statusCode">NNTP status stored on the result.</param>
        /// <param name="reason">Diagnostic text stored on the result.</param>
        /// <param name="article">Payload owned by the result.</param>
        /// <returns>
        /// A result with <see cref="ArticleRetrievalKind.ArticleRetrieved"/>, <see cref="SessionReusable"/> true,
        /// and <paramref name="article"/>.
        /// </returns>
        internal static ArticleRetrievalResult Retrieved(int statusCode, string reason, RetrievedArticle article) =>
            new(ArticleRetrievalKind.ArticleRetrieved, statusCode, reason, article, SessionReusable: true);

        /// <summary>
        /// Creates a successful retrieval whose payload was already consumed.
        /// </summary>
        /// <param name="statusCode">NNTP status stored on the result.</param>
        /// <param name="reason">Diagnostic text stored on the result.</param>
        /// <returns>
        /// A result with <see cref="ArticleRetrievalKind.ArticleRetrieved"/>, a null <see cref="Article"/>,
        /// and <see cref="SessionReusable"/> true.
        /// </returns>
        /// <remarks>
        /// The production ARTICLE path uses this after the synchronous callback has built the canonical record.
        /// The reader scratch is not attached to the result.
        /// </remarks>
        internal static ArticleRetrievalResult Retrieved(int statusCode, string reason) =>
            new(ArticleRetrievalKind.ArticleRetrieved, statusCode, reason, Article: null, SessionReusable: true);

        /// <summary>Creates a non-payload result.</summary>
        /// <param name="kind">Retrieval classification stored on the result.</param>
        /// <param name="statusCode">NNTP status when the server produced one; otherwise null.</param>
        /// <param name="reason">Diagnostic text stored on the result.</param>
        /// <param name="sessionReusable">Whether the leased session may return to the idle pool.</param>
        /// <returns>
        /// A result whose <see cref="Article"/> is null and whose other fields are the supplied values.
        /// </returns>
        internal static ArticleRetrievalResult Failed(
            ArticleRetrievalKind kind,
            int? statusCode,
            string reason,
            bool sessionReusable) =>
            new(kind, statusCode, reason, Article: null, sessionReusable);
    }
}
