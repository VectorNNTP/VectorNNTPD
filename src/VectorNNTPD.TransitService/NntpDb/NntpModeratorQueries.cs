namespace VectorNNTPD.TransitService.NntpDb
{
    /// <summary>Authoritative NntpDB query for the moderator authorization catalogue.</summary>
    public static class NntpModeratorQueries
    {
        /// <summary>
        /// Enabled <c>nntpmoderators</c> rows in first-match order (<c>moderator_id ASC</c>).
        /// </summary>
        public const string SelectEnabledModerators =
            "SELECT "
            + "moderator_id, group_pattern, moderator_address, account_name "
            + "FROM nntpmoderators "
            + "WHERE is_enabled = 'Y' "
            + "ORDER BY moderator_id";
    }
}
