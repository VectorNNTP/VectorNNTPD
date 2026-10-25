namespace VectorNNTPD.BackFillerService.Nntp
{
    /// <summary>
    /// Named NNTP status codes used by provider retrieval.
    /// Values are from RFC 3977, RFC 4642, and RFC 4643; do not scatter raw literals at call sites.
    /// </summary>
    internal static class NntpStatusCode
    {
        /// <summary>RFC 3977 CAPABILITIES success; multiline capability list follows.</summary>
        internal const int CapabilityListFollows = 101;

        /// <summary>RFC 3977 DATE success.</summary>
        internal const int DateFollows = 111;

        /// <summary>RFC 3977 greeting: service available, posting allowed.</summary>
        private const int ServiceReadyPostingAllowed = 200;

        /// <summary>RFC 3977 greeting: service available, posting prohibited.</summary>
        private const int ServiceReadyPostingProhibited = 201;

        /// <summary>RFC 3977 ARTICLE success; multiline article follows.</summary>
        internal const int ArticleFollows = 220;

        /// <summary>RFC 4643 AUTHINFO accepted.</summary>
        internal const int AuthenticationAccepted = 281;

        /// <summary>RFC 4643 AUTHINFO USER: password required.</summary>
        internal const int PasswordRequired = 381;

        /// <summary>RFC 4642 STARTTLS: continue with TLS negotiation.</summary>
        internal const int ContinueWithTlsNegotiation = 382;

        /// <summary>RFC 3977: no newsgroup selected (ARTICLE by number).</summary>
        internal const int NoNewsgroupSelected = 412;

        /// <summary>RFC 3977: current article number invalid.</summary>
        internal const int CurrentArticleInvalid = 420;

        /// <summary>RFC 3977: no article with that number.</summary>
        internal const int NoArticleWithNumber = 423;

        /// <summary>RFC 3977 ARTICLE by Message-ID: no such article.</summary>
        internal const int NoArticleWithMessageId = 430;

        /// <summary>
        /// Provider ARTICLE response treated as a definitive miss for Article Work lookup.
        /// </summary>
        /// <remarks>
        /// Not an RFC 3977 ARTICLE success/failure code for Message-ID retrieval. Some upstream providers
        /// return 451 when the article is unavailable; BackFiller maps that status to
        /// <see cref="ArticleRetrievalKind.ArticleNotFound"/> (same settlement as 430), not provider failure.
        /// </remarks>
        internal const int ArticleUnavailable = 451;

        /// <summary>
        /// RFC 3977 §3.2.1: server terminates the connection after this response.
        /// </summary>
        internal const int ServiceTemporarilyUnavailable = 400;

        /// <summary>
        /// RFC 3977 §3.2.1: internal fault preventing the command (single-line; connection stays open).
        /// </summary>
        internal const int InternalFault = 403;

        /// <summary>RFC 3977/4643: authentication required for the command.</summary>
        private const int AuthenticationRequired = 480;

        /// <summary>RFC 4643: authentication failed or rejected.</summary>
        private const int AuthenticationRejected = 481;

        /// <summary>RFC 4643: authentication commands out of sequence.</summary>
        private const int AuthenticationOutOfSequence = 482;

        /// <summary>RFC 3977: command unknown.</summary>
        internal const int CommandUnknown = 500;

        /// <summary>RFC 3977: command syntax error.</summary>
        internal const int CommandSyntaxError = 501;

        /// <summary>
        /// RFC 3977 §3.2.1: command not permitted on this connection; a new connection with
        /// appropriate authority is required before the facility can be used.
        /// </summary>
        internal const int CommandNotPermitted = 502;

        /// <summary>RFC 3977: program fault / temporary unavailability.</summary>
        internal const int ProgramFault = 503;

        /// <summary>Returns whether <paramref name="code"/> is a usable NNTP greeting.</summary>
        /// <param name="code">NNTP status code.</param>
        /// <returns>
        /// <see langword="true"/> for <see cref="ServiceReadyPostingAllowed"/> or <see cref="ServiceReadyPostingProhibited"/>.
        /// </returns>
        internal static bool IsServiceReadyGreeting(int code) =>
            code is ServiceReadyPostingAllowed or ServiceReadyPostingProhibited;

        /// <summary>Returns whether <paramref name="code"/> is an AUTHINFO credential failure.</summary>
        /// <param name="code">NNTP status code.</param>
        /// <returns>
        /// <see langword="true"/> for <see cref="AuthenticationRequired"/>, <see cref="AuthenticationRejected"/>,
        /// or <see cref="AuthenticationOutOfSequence"/>.
        /// </returns>
        internal static bool IsAuthenticationFailure(int code) =>
            code is AuthenticationRequired or AuthenticationRejected or AuthenticationOutOfSequence;

        /// <summary>Returns whether <paramref name="code"/> is a generic command rejection.</summary>
        /// <param name="code">NNTP status code.</param>
        /// <returns>
        /// <see langword="true"/> for <see cref="CommandUnknown"/>, <see cref="CommandSyntaxError"/>,
        /// <see cref="CommandNotPermitted"/>, or <see cref="ProgramFault"/>.
        /// </returns>
        internal static bool IsCommandRejected(int code) =>
            code is CommandUnknown or CommandSyntaxError or CommandNotPermitted or ProgramFault;

        /// <summary>
        /// Returns whether an ARTICLE status is a single-line command failure that leaves the session synchronized
        /// and reusable for a later ARTICLE (RFC 3977 §§3.2, 3.2.1).
        /// </summary>
        /// <param name="code">NNTP status from the ARTICLE status line.</param>
        /// <returns>
        /// <see langword="true"/> for <see cref="InternalFault"/>, <see cref="CommandUnknown"/>,
        /// <see cref="CommandSyntaxError"/>, or <see cref="ProgramFault"/>.
        /// </returns>
        /// <remarks>
        /// ARTICLE is a mandatory READER command; RFC 3977 §3.2.1 forbids 500 for mandatory commands.
        /// Providers may still return 500 (for example Giganews <c>server error</c>) as a per-request fault.
        /// Unexpected 4xx/5xx are command failures (RFC 3977 §3.2); a single-line response does not desynchronize
        /// the session. <see cref="CommandNotPermitted"/> and <see cref="ServiceTemporarilyUnavailable"/> are
        /// excluded: 502 requires a new connection with appropriate authority, and 400 closes the connection.
        /// </remarks>
        internal static bool IsArticleLevelFailureReusable(int code) =>
            code is InternalFault or CommandUnknown or CommandSyntaxError or ProgramFault;
    }
}
