namespace VectorNNTPD.BackFillerService.Nntp
{
    /// <summary>
    /// Hard NNTP article safety bounds from the old BackFiller acquisition contract.
    /// These are not runtime configuration settings.
    /// </summary>
    internal static class ArticleResourceLimits
    {
        /// <summary>Maximum destuffed ARTICLE payload. Same ceiling as Common article processing.</summary>
        internal const int MaxArticleBytes = VectorNNTPD.Common.Articles.ArticleResourceLimits.MaxArticleBytes;

        /// <summary>
        /// Maximum NNTP status-line length in bytes excluding CRLF.
        /// Old worker used 16 KiB; RFC 3977 §3.2 caps responses at 512 octets including CRLF.
        /// </summary>
        internal const int MaxStatusLineBytes = 16 * 1024;
    }
}
