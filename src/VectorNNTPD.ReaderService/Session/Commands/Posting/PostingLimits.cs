namespace VectorNNTPD.ReaderService.Session.Commands.Posting
{
    /// <summary>Fixed POST header and newsgroup bounds (distinct from <c>Nntpd:MaxArticleSize</c>).</summary>
    internal static class PostingLimits
    {
        /// <summary>Maximum destuffed header block including the blank separator line.</summary>
        public const int MaxHeaderBlockBytes = 64 * 1024;

        /// <summary>Maximum unfolded single header (name + colon + value).</summary>
        public const int MaxSingleHeaderBytes = 8192;

        /// <summary>Maximum header fields in one article.</summary>
        public const int MaxHeaderCount = 256;

        /// <summary>Maximum distinct newsgroups in <c>Newsgroups:</c>.</summary>
        public const int MaxNewsgroups = 12;

        /// <summary>Maximum distinct groups in <c>Followup-To:</c>.</summary>
        public const int MaxFollowupToGroups = 12;

        /// <summary>Articles whose <c>Date:</c> is more than this far in the future are rejected.</summary>
        public static readonly TimeSpan MaxDateSkewFuture = TimeSpan.FromHours(24);

        /// <summary>Articles whose <c>Date:</c> is older than this relative to injection time are rejected.</summary>
        public static readonly TimeSpan MaxDateAge = TimeSpan.FromDays(14);
    }
}
