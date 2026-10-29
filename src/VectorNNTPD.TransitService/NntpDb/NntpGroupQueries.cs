namespace VectorNNTPD.TransitService.NntpDb
{
    /// <summary>Authoritative NntpDB queries used by the newsgroup catalogue.</summary>
    public static class NntpGroupQueries
    {
        /// <summary>
        /// Complete newsgroup catalogue source. Column set is exactly the fields
        /// required by LIST ACTIVE / LIST NEWSGROUPS / GROUP metadata.
        /// </summary>
        public const string SelectNewsgroups =
            """
            SELECT
                group_name,
                group_description,
                count_high,
                count_low,
                posting_status
            FROM nntpgroups
            """;
    }
}
