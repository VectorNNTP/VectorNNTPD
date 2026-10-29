using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.ReaderService.Storage
{
    /// <summary>
    /// Sends one CanonicalV1 article to one StorageServer.
    /// The client keeps one TLS connection per target and does not dial for every article.
    /// </summary>
    public interface IArticlePlacementClient
    {
        /// <summary>Places <paramref name="record"/> on <paramref name="target"/>. One attempt.</summary>
        ValueTask<ArticlePlacementResult> PlaceAsync(
            ArticleRecord record,
            StorageServerFleetEntry target,
            CancellationToken cancellationToken);
    }
}
