namespace VectorNNTPD.ReaderService.Newsgroups
{
    /// <summary>
    /// LIST ACTIVE posting-status octet as stored in <c>nntpgroups.posting_status</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supported values are the fixed MySQL enum <c>y</c>, <c>n</c>, <c>m</c>, <c>x</c>,
    /// <c>j</c>. Meanings follow RFC 3977 §7.6.3 and RFC 6048 §3.1:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>y</c> — posting is permitted.</description></item>
    /// <item><description><c>n</c> — posting is not permitted.</description></item>
    /// <item><description><c>m</c> — postings are forwarded to the newsgroup moderator.</description></item>
    /// <item><description><c>x</c> — postings and articles from peers are not permitted.</description></item>
    /// <item><description><c>j</c> — articles from peers are permitted, but nothing is locally filed.</description></item>
    /// </list>
    /// <para>
    /// RFC 6048's <c>=&lt;newsgroup&gt;</c> alias form is intentionally unsupported.
    /// There is no enum member for <c>=</c>. Unknown octets are rejected; they are
    /// never coerced.
    /// </para>
    /// </remarks>
    public enum NewsgroupPostingStatus : byte
    {
        /// <summary>Posting is permitted (<c>y</c>). RFC 3977 §7.6.3.</summary>
        Allowed = (byte)'y',

        /// <summary>Posting is prohibited (<c>n</c>). RFC 3977 §7.6.3.</summary>
        Prohibited = (byte)'n',

        /// <summary>Postings are forwarded to the moderator (<c>m</c>). RFC 3977 §7.6.3.</summary>
        Moderated = (byte)'m',

        /// <summary>
        /// Posting and articles from peers are not permitted (<c>x</c>). RFC 6048 §3.1.
        /// Distinct from <see cref="Prohibited"/>; not equivalent to <c>n</c>.
        /// </summary>
        NoPostingOrPeerArticles = (byte)'x',

        /// <summary>
        /// Articles from peers are permitted, but nothing is locally filed (<c>j</c>). RFC 6048 §3.1.
        /// Distinct from <see cref="Allowed"/> and <see cref="Prohibited"/>.
        /// This is not a catch-all junk group; the octet is the RFC 6048 <c>j</c> status.
        /// </summary>
        PeerOnly = (byte)'j',
    }

    /// <summary>Validation helpers for <see cref="NewsgroupPostingStatus"/> octets.</summary>
    public static class NewsgroupPostingStatusOctets
    {
        /// <summary>
        /// Returns whether <paramref name="value"/> is one of the five supported status octets.
        /// </summary>
        /// <param name="value">Candidate status octet.</param>
        /// <returns><see langword="true"/> for <c>y</c>/<c>n</c>/<c>m</c>/<c>x</c>/<c>j</c>.</returns>
        public static bool IsSupported(byte value) =>
            value is (byte)'y' or (byte)'n' or (byte)'m' or (byte)'x' or (byte)'j';
    }
}
