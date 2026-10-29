using VectorNNTPD.TransitService.Networking.Proxy;

namespace VectorNNTPD.TransitService.Moderation
{
    /// <summary>
    /// Proto-article offered to a moderator before Injection-Info / Injection-Date are added.
    /// </summary>
    /// <remarks>
    /// RFC 5537 §3.5 step 7 and §3.5.1: forwarding happens after Message-ID/Date are present
    /// and before injection trace headers. The payload is the stuffed proto-article (IHAVE
    /// wire representation without the terminator), not a finalized injected article.
    /// </remarks>
    public sealed class ModerationSubmission
    {
        /// <summary>Gets the stuffed proto-article (terminator omitted).</summary>
        public required ReadOnlyMemory<byte> ProtoArticle { get; init; }

        /// <summary>Gets the article Message-ID (supplied or synthesized).</summary>
        public required string MessageId { get; init; }

        /// <summary>Gets the syntax-validated <c>Newsgroups:</c> targets.</summary>
        public required string[] Newsgroups { get; init; }

        /// <summary>
        /// Gets the leftmost moderated group — the RFC 5537 §3.5.1 forwarding target.
        /// </summary>
        public required string TargetModeratedGroup { get; init; }

        /// <summary>Gets the resolved moderator mailbox for <see cref="TargetModeratedGroup"/>.</summary>
        public required string ModeratorAddress { get; init; }

        /// <summary>Gets the submitting AUTHINFO principal, or <see langword="null"/> when unauthenticated.</summary>
        public string? AuthenticatedUsername { get; init; }

        /// <summary>Gets the session client identity of the submitting poster.</summary>
        public required ConnectionClientIdentity Sender { get; init; }
    }

    /// <summary>Outcome of <see cref="IModerationSubmissionService.SubmitAsync"/>.</summary>
    public enum ModerationSubmissionStatus
    {
        /// <summary>The proto-article was accepted for moderator delivery.</summary>
        Accepted = 0,

        /// <summary>No delivery mechanism is configured or available.</summary>
        Unavailable = 1,

        /// <summary>Delivery was attempted and failed.</summary>
        Failed = 2,
    }

    /// <summary>Result of one moderation submission attempt.</summary>
    /// <param name="Status">Acceptance / availability / failure.</param>
    /// <param name="Detail">Internal detail (not sent on the NNTP wire).</param>
    public readonly record struct ModerationSubmissionResult(
        ModerationSubmissionStatus Status,
        string Detail);
}
