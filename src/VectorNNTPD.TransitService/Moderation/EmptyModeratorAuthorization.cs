namespace VectorNNTPD.TransitService.Moderation
{
    /// <summary>
    /// Moderator authorization with no configured routes. Every resolve and approval fails.
    /// </summary>
    public sealed class EmptyModeratorAuthorization : IModeratorAuthorization
    {
        /// <summary>Shared instance.</summary>
        public static EmptyModeratorAuthorization Instance { get; } = new();

        /// <inheritdoc />
        public bool IsAuthenticatedModerator(string? authenticatedUsername)
        {
            _ = authenticatedUsername;
            return false;
        }

        /// <inheritdoc />
        public bool TryResolve(ReadOnlySpan<byte> newsgroup, out ModeratorIdentity identity)
        {
            _ = newsgroup;
            identity = default;
            return false;
        }

        /// <inheritdoc />
        public bool CanApprove(
            string? authenticatedUsername,
            ReadOnlySpan<byte> approvedIdentity,
            ReadOnlySpan<byte> newsgroup)
        {
            _ = authenticatedUsername;
            _ = approvedIdentity;
            _ = newsgroup;
            return false;
        }

        /// <inheritdoc />
        public bool TryAuthorizeApproval(
            string? authenticatedUsername,
            ReadOnlySpan<string> approvedIdentities,
            ReadOnlySpan<string> moderatedGroups,
            out string? failureDetail)
        {
            _ = authenticatedUsername;
            _ = approvedIdentities;
            failureDetail = moderatedGroups.Length == 0
                ? "no moderated groups"
                : "unresolved moderator";
            return false;
        }
    }
}
