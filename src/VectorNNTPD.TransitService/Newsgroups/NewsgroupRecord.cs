namespace VectorNNTPD.TransitService.Newsgroups
{
    /// <summary>
    /// One immutable newsgroup entry owned by a <see cref="NewsgroupSnapshot"/>.
    /// </summary>
    /// <remarks>
    /// Precomputed UTF-8 wire lines include CRLF and, when required, multiline
    /// dot-stuffing. Command handlers must not re-encode these buffers.
    /// </remarks>
    public sealed class NewsgroupRecord
    {
        internal NewsgroupRecord(
            string groupName,
            byte[] groupNameBytes,
            string groupDescription,
            ulong countHigh,
            ulong countLow,
            NewsgroupPostingStatus postingStatus,
            ulong estimatedCount,
            byte[] listActiveLine,
            byte[] listCountsLine,
            byte[] listNewsgroupsLine,
            byte[] groupSelectedLine)
        {
            GroupName = groupName;
            GroupNameBytes = groupNameBytes;
            GroupDescription = groupDescription;
            CountHigh = countHigh;
            CountLow = countLow;
            PostingStatus = postingStatus;
            EstimatedCount = estimatedCount;
            ListActiveLine = listActiveLine;
            ListCountsLine = listCountsLine;
            ListNewsgroupsLine = listNewsgroupsLine;
            GroupSelectedLine = groupSelectedLine;
        }

        /// <summary>Gets the original group name as stored in the database.</summary>
        public string GroupName { get; }

        /// <summary>Gets the original group name as UTF-8 octets used for lookup and wire output.</summary>
        public ReadOnlyMemory<byte> GroupNameBytes { get; }

        /// <summary>Gets the original group description (may be empty).</summary>
        public string GroupDescription { get; }

        /// <summary>Gets the unsigned high water mark.</summary>
        public ulong CountHigh { get; }

        /// <summary>Gets the unsigned low water mark.</summary>
        public ulong CountLow { get; }

        /// <summary>Gets the database posting status (<c>y</c>/<c>n</c>/<c>m</c>/<c>x</c>/<c>j</c>).</summary>
        public NewsgroupPostingStatus PostingStatus { get; }

        /// <summary>
        /// Gets the GROUP article-count estimate derived from the water marks
        /// (RFC 3977 §6.1.1: high − low + 1, or 0 when the group is empty).
        /// </summary>
        public ulong EstimatedCount { get; }

        /// <summary>Gets the precomputed LIST ACTIVE line including CRLF.</summary>
        public ReadOnlyMemory<byte> ListActiveLine { get; }

        /// <summary>
        /// Gets the precomputed LIST COUNTS line including CRLF
        /// (<c>group high low estimated status</c>).
        /// </summary>
        public ReadOnlyMemory<byte> ListCountsLine { get; }

        /// <summary>Gets the precomputed LIST NEWSGROUPS line including CRLF.</summary>
        public ReadOnlyMemory<byte> ListNewsgroupsLine { get; }

        /// <summary>Gets the precomputed <c>211 number low high group</c> line including CRLF.</summary>
        public ReadOnlyMemory<byte> GroupSelectedLine { get; }
    }
}
