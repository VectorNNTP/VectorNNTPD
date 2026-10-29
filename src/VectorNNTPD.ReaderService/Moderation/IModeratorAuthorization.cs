namespace VectorNNTPD.ReaderService.Moderation
{
    /// <summary>
    /// Authorizes moderated-newsgroup approval from the authenticated NNTP principal,
    /// the configured moderator route, and the claimed <c>Approved:</c> identity.
    /// </summary>
    /// <remarks>
    /// An <c>Approved:</c> header is an assertion. It is trusted only when the
    /// authenticated AUTHINFO principal is configured as the moderator for every
    /// moderated target. Presence of <c>Approved:</c> alone is never sufficient.
    /// This contract is not <c>Control:PgpAuthorities</c>.
    /// </remarks>
    public interface IModeratorAuthorization
    {
        /// <summary>
        /// Returns whether <paramref name="authenticatedUsername"/> is configured as a
        /// moderator for at least one mapping. This is not group authorization.
        /// </summary>
        bool IsAuthenticatedModerator(string? authenticatedUsername);

        /// <summary>
        /// Resolves the first-match moderator route for <paramref name="newsgroup"/>.
        /// </summary>
        bool TryResolve(ReadOnlySpan<byte> newsgroup, out ModeratorIdentity identity);

        /// <summary>
        /// Returns whether the authenticated principal may approve <paramref name="newsgroup"/>
        /// using <paramref name="approvedIdentity"/>.
        /// </summary>
        bool CanApprove(
            string? authenticatedUsername,
            ReadOnlySpan<byte> approvedIdentity,
            ReadOnlySpan<byte> newsgroup);

        /// <summary>
        /// Authorizes every moderated target against the collected <c>Approved:</c> identities.
        /// </summary>
        /// <param name="authenticatedUsername">AUTHINFO principal, or <see langword="null"/> when unauthenticated.</param>
        /// <param name="approvedIdentities">Distinct mailbox identities parsed from <c>Approved:</c>.</param>
        /// <param name="moderatedGroups">Moderated <c>Newsgroups:</c> targets in header order.</param>
        /// <param name="failureDetail">Internal reject detail when authorization fails.</param>
        /// <returns><see langword="true"/> only when every moderated target is authorized.</returns>
        bool TryAuthorizeApproval(
            string? authenticatedUsername,
            ReadOnlySpan<string> approvedIdentities,
            ReadOnlySpan<string> moderatedGroups,
            out string? failureDetail);
    }
}
