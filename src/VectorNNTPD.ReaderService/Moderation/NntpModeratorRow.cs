namespace VectorNNTPD.ReaderService.Moderation
{
    /// <summary>One enabled <c>nntpmoderators</c> row after mapping.</summary>
    public sealed class NntpModeratorRow
    {
        /// <summary>Initializes a mapped moderator row.</summary>
        public NntpModeratorRow(long moderatorId, string groupPattern, string moderatorAddress, string accountName)
        {
            ArgumentNullException.ThrowIfNull(groupPattern);
            ArgumentNullException.ThrowIfNull(moderatorAddress);
            ModeratorId = moderatorId;
            GroupPattern = groupPattern;
            ModeratorAddress = moderatorAddress;
            AccountName = accountName ?? string.Empty;
        }

        /// <summary>Gets <c>moderator_id</c>. Snapshot order follows this value ascending.</summary>
        public long ModeratorId { get; }

        /// <summary>Gets the RFC 3977 wildmat matched against the newsgroup name.</summary>
        public string GroupPattern { get; }

        /// <summary>Gets the routing / Approved mailbox or INN <c>%s</c> template.</summary>
        public string ModeratorAddress { get; }

        /// <summary>Gets the AUTHINFO username principal, or empty for routing-only.</summary>
        public string AccountName { get; }
    }
}
