using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.TransitService.Transport.Vatp
{
    /// <summary>Fetches canonical articles from BackFiller or StorageServer listeners over VATP.</summary>
    public interface IVatpArticleClient
    {
        /// <summary>
        /// Fetches one article from the explicit VATP endpoint.
        /// </summary>
        /// <param name="fqdn">Lowercase dotted DNS name of the listener.</param>
        /// <param name="vatpPort">TLS listen port in the range 1–65535.</param>
        /// <param name="requestId">Correlation id from ArticleWork or the storage lookup.</param>
        /// <param name="articleId">Expected ArticleId for OPEN binding.</param>
        /// <param name="cancellationToken">Cancellation; sends CANCEL when the connection is usable.</param>
        Task<VatpFetchResult> FetchArticleAsync(
            string fqdn,
            int vatpPort,
            Guid requestId,
            ArticleId articleId,
            CancellationToken cancellationToken);
    }
}
