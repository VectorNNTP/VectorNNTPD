using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;

namespace VectorNNTPD.StorageService.Listener
{

    /// <summary>
    /// Boundary between the VATP OPEN path and article bytes.
    /// </summary>
    /// <remarks>
    /// Spool files are not an OPEN index. The host registration still rejects every OPEN.
    /// RequestId is correlation metadata. Null and rejecting implementations remain for tests.
    /// </remarks>
    public interface IStorageArticleOpenBoundary
    {
        /// <summary>
        /// Attempts to open a transfer for the given RequestId and ArticleId.
        /// </summary>
        /// <param name="requestId">Client OPEN RequestId. Not consulted as a storage key.</param>
        /// <param name="articleId">Client OPEN ArticleId.</param>
        /// <returns>
        /// Accepted result carrying the canonical record, or a rejected result with no record.
        /// </returns>
        StorageArticleOpenResult TryOpen(Guid requestId, in ArticleId articleId);
    }

    /// <summary>Result of an article-open attempt.</summary>
    /// <param name="Accepted">Whether OPEN can be served.</param>
    /// <param name="Reason">Optional human-readable rejection reason (ASCII-safe, no secrets).</param>
    /// <param name="Record">Canonical record when <paramref name="Accepted"/> is true; otherwise default.</param>
    /// <param name="SelectedDateHeaderName">Date-family header used to build VATP META when accepted.</param>
    public readonly record struct StorageArticleOpenResult(
        bool Accepted,
        string? Reason = null,
        ArticleRecord Record = default,
        NntpArticleHeaderName SelectedDateHeaderName = NntpArticleHeaderName.Unknown)
    {
        /// <summary>Rejected open result. Does not carry an article record.</summary>
        public static StorageArticleOpenResult Rejected(string? reason = null) => new(false, reason);

        /// <summary>Accepted open result. The session sends META and DATA from <paramref name="record"/>.</summary>
        public static StorageArticleOpenResult Opened(
            ArticleRecord record,
            NntpArticleHeaderName selectedDateHeaderName) =>
            new(true, null, record, selectedDateHeaderName);
    }

    /// <summary>
    /// Default open boundary that always rejects OPEN requests.
    /// </summary>
    public sealed class RejectingStorageArticleOpenBoundary : IStorageArticleOpenBoundary
    {
        /// <inheritdoc />
        public StorageArticleOpenResult TryOpen(Guid requestId, in ArticleId articleId)
        {
            _ = requestId;
            _ = articleId;
            return StorageArticleOpenResult.Rejected("storage-not-implemented");
        }
    }

    /// <summary>
    /// Null-object open boundary that always rejects OPEN requests.
    /// </summary>
    public sealed class NullStorageArticleOpenBoundary : IStorageArticleOpenBoundary
    {
        /// <summary>Shared singleton instance.</summary>
        public static NullStorageArticleOpenBoundary Instance { get; } = new();

        /// <inheritdoc />
        public StorageArticleOpenResult TryOpen(Guid requestId, in ArticleId articleId)
        {
            _ = requestId;
            _ = articleId;
            return StorageArticleOpenResult.Rejected("storage-not-implemented");
        }
    }
}
