using VectorNNTPD.ReaderService.Moderation;
using VectorNNTPD.ReaderService.Networking.Proxy;

namespace VectorNNTPD.ReaderService.Session.Commands.Posting
{
    /// <summary>What the POST command should do with a completed receive.</summary>
    internal enum StreamingPostDisposition
    {
        /// <summary>Article is ready for normal History Peek / TryAdmit injection.</summary>
        Inject = 0,

        /// <summary>
        /// Unapproved moderated proto-article; caller must submit for moderation and must
        /// not Peek, TryAdmit, or Remember.
        /// </summary>
        SubmitForModeration = 1,
    }

    /// <summary>Outcome of one streaming POST article receive.</summary>
    internal enum StreamingPostReadStatus
    {
        /// <summary>Terminator seen; destuffed article is ready for the caller-selected disposition.</summary>
        Completed = 0,

        /// <summary>Peer disconnected or cancelled before the terminating dot line.</summary>
        Incomplete = 1,

        /// <summary>Destuffed article exceeded <c>Nntpd:MaxArticleSize</c>; remaining bytes were drained.</summary>
        TooLarge = 2,

        /// <summary>Header parse or POST validation failed; remaining bytes were drained when needed.</summary>
        Rejected = 3,
    }

    /// <summary>Inputs required to stream one POST article into a destuffed article buffer.</summary>
    internal sealed class StreamingPostReadOptions
    {
        /// <summary>Gets the destuffed POST size limit (<c>Nntpd:MaxArticleSize</c>).</summary>
        public required int MaxArticleSize { get; init; }

        /// <summary>Gets the clock used for the single injection timestamp.</summary>
        public required TimeProvider Time { get; init; }

        /// <summary>Gets the newsgroup posting policy.</summary>
        public required INewsgroupPostingPolicy NewsgroupPolicy { get; init; }

        /// <summary>Gets the server injection identity (<c>nntpd{ServerId:00}.{DnsSuffix}</c>).</summary>
        public required string InjectionIdentity { get; init; }

        /// <summary>Gets the session client identity used only inside protected <c>X-Trace</c>.</summary>
        public required ConnectionClientIdentity ClientIdentity { get; init; }

        /// <summary>Gets the configured <c>mail-complaints-to</c> mailbox.</summary>
        public required string MailComplaintsTo { get; init; }

        /// <summary>Gets the <c>X-Trace</c> protector, or <see langword="null"/> when unset.</summary>
        public IPostingTraceProtector? TraceProtector { get; init; }

        /// <summary>
        /// Gets whether this receive may accept a well-formed <c>Control: cancel</c> header.
        /// Ordinary posters remain <see langword="false"/>.
        /// </summary>
        public bool ControlCancelPermitted { get; init; }

        /// <summary>
        /// Gets the AUTHINFO username captured at POST admission, or <see langword="null"/>
        /// when the session is unauthenticated. Never taken from article headers.
        /// </summary>
        public string? AuthenticatedUsername { get; init; }

        /// <summary>Gets the moderator authorization table used for <c>Approved:</c> decisions.</summary>
        public IModeratorAuthorization ModeratorAuthorization { get; init; } = EmptyModeratorAuthorization.Instance;
    }

    /// <summary>Result of <see cref="StreamingPostArticleReader.ReadAsync"/>.</summary>
    /// <param name="Status">Receive / validation status.</param>
    /// <param name="Failure">Set when <paramref name="Status"/> is <see cref="StreamingPostReadStatus.Rejected"/> or TooLarge.</param>
    /// <param name="Wire">Destuffed article (terminator omitted) when Completed; otherwise empty.</param>
    /// <param name="MessageId">Article Message-ID when known.</param>
    /// <param name="Newsgroups">Validated newsgroup names when known.</param>
    /// <param name="DestuffedSize">Logical destuffed client article size (stuffing dots excluded).</param>
    /// <param name="InjectionUtc">Single captured UTC timestamp written into server-owned headers.</param>
    /// <param name="Disposition">Inject or submit-for-moderation once the terminator is seen.</param>
    /// <param name="ModeratorAddress">Resolved leftmost moderator mailbox on the moderation path.</param>
    /// <param name="TargetModeratedGroup">Leftmost moderated group on the moderation path.</param>
    /// <param name="ApprovedIdentities">Parsed <c>Approved:</c> identities when present.</param>
    internal readonly record struct StreamingPostReadResult(
        StreamingPostReadStatus Status,
        PostingFailure Failure,
        ReadOnlyMemory<byte> Wire,
        string? MessageId,
        string[] Newsgroups,
        int DestuffedSize,
        DateTimeOffset InjectionUtc,
        StreamingPostDisposition Disposition = StreamingPostDisposition.Inject,
        string? ModeratorAddress = null,
        string? TargetModeratedGroup = null,
        string[]? ApprovedIdentities = null);
}
