namespace VectorNNTPD.ReaderService.Session.Commands.Posting
{
    /// <summary>
    /// Newsgroup existence, posting-authorization, and moderation boundary used by POST.
    /// </summary>
    /// <remarks>
    /// Production sessions that have an <see cref="VectorNNTPD.ReaderService.Newsgroups.INewsgroupCatalogue"/>
    /// use <see cref="CatalogueNewsgroupPostingPolicy"/>. <see cref="SyntaxOnlyNewsgroupPostingPolicy"/>
    /// remains the stand-in when no catalogue is attached (tests).
    /// </remarks>
    public interface INewsgroupPostingPolicy
    {
        /// <summary>
        /// Evaluates posting authorization for already syntax-validated newsgroup names.
        /// </summary>
        /// <param name="groups">Distinct, syntactically valid newsgroup names.</param>
        /// <returns>
        /// Catalogue classification. Does not re-validate group-name syntax and does not
        /// treat <c>Approved:</c> as authorization.
        /// </returns>
        NewsgroupPostingEvaluation Evaluate(ReadOnlySpan<string> groups);
    }

    /// <summary>Result of <see cref="INewsgroupPostingPolicy.Evaluate"/>.</summary>
    /// <param name="Status">Whether a catalogue decision was available.</param>
    /// <param name="Failure">Set when posting is rejected by policy.</param>
    /// <param name="ModeratedGroups">
    /// Moderated targets in <c>Newsgroups:</c> order when
    /// <see cref="NewsgroupCatalogStatus.RequiresModeration"/>; otherwise empty.
    /// </param>
    public readonly record struct NewsgroupPostingEvaluation(
        NewsgroupCatalogStatus Status,
        PostingFailure? Failure,
        string[]? ModeratedGroups = null)
    {
        /// <summary>Gets moderated targets, or an empty array when none were classified.</summary>
        public string[] ModeratedGroupNames => ModeratedGroups ?? [];
    }

    /// <summary>Availability of newsgroup existence/moderation state.</summary>
    public enum NewsgroupCatalogStatus
    {
        /// <summary>
        /// Group existence and moderation state are not available. This is not a confirmed
        /// existence match and must not be logged or treated as such.
        /// </summary>
        CatalogUnavailable = 0,

        /// <summary>Policy rejected posting to one or more groups.</summary>
        Rejected = 1,

        /// <summary>
        /// Every target exists in the captured snapshot and permits local POST
        /// (<c>y</c>). This is a confirmed catalogue decision.
        /// </summary>
        Accepted = 2,

        /// <summary>
        /// At least one target is moderated (<c>m</c>) and every other target permits
        /// local POST (<c>y</c>). Approval authorization or moderator forwarding is required.
        /// </summary>
        RequiresModeration = 3,
    }

    /// <summary>
    /// POST newsgroup policy used when no catalogue is attached: syntax-valid groups
    /// are accepted without asserting that the groups exist or that moderation state
    /// is known.
    /// </summary>
    /// <remarks>
    /// Production sessions with a catalogue use <see cref="CatalogueNewsgroupPostingPolicy"/>.
    /// This stand-in does not consult the catalogue and does not treat <c>Approved:</c>
    /// as authorization to post to a moderated group.
    /// </remarks>
    public sealed class SyntaxOnlyNewsgroupPostingPolicy : INewsgroupPostingPolicy
    {
        /// <summary>Shared instance.</summary>
        public static SyntaxOnlyNewsgroupPostingPolicy Instance { get; } = new();

        /// <inheritdoc />
        public NewsgroupPostingEvaluation Evaluate(ReadOnlySpan<string> groups)
        {
            _ = groups;
            return new NewsgroupPostingEvaluation(NewsgroupCatalogStatus.CatalogUnavailable, Failure: null);
        }
    }
}
