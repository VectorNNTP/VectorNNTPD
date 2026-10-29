namespace VectorNNTPD.ReaderService.RabbitMq.ArticleWork
{
    /// <summary>Structured article-work RPC result returned to NNTP command callers.</summary>
    /// <remarks>
    /// This type does not contain article bytes. Callers that need the article dial
    /// <see cref="Fqdn"/> and <see cref="VatpPort"/>, then OPEN with <see cref="ArticleId"/>.
    /// </remarks>
    /// <param name="Outcome">Classified RPC outcome.</param>
    /// <param name="RequestId">Application request identity used for the lookup.</param>
    /// <param name="MessageId">Message-ID that was queried.</param>
    /// <param name="Backbone">Winning source backbone when a wire response completed the operation.</param>
    /// <param name="Fqdn">Success-only BackFiller FQDN from the winning response.</param>
    /// <param name="VatpPort">Success-only TLS VATP listen port from the winning response.</param>
    /// <param name="ArticleId">Success-only CanonicalV1 ArtId from the winning response.</param>
    /// <param name="Error">Failure detail when the classified outcome is not success.</param>
    /// <param name="SourceExchange">Exchange that produced the winning response, when applicable.</param>
    public sealed record ArticleWorkRpcResult(
        ArticleWorkOutcome Outcome,
        Guid RequestId,
        string MessageId,
        string? Backbone,
        string? Fqdn,
        int? VatpPort,
        VectorNNTPD.Common.Articles.ArticleId? ArticleId,
        string? Error,
        string? SourceExchange)
    {
        /// <summary>Builds an overall not-found result after the lookup deadline elapses.</summary>
        internal static ArticleWorkRpcResult NotFound(Guid requestId, string messageId, string error) =>
            new(
                ArticleWorkOutcome.ArticleNotFound,
                requestId,
                messageId,
                Backbone: null,
                Fqdn: null,
                VatpPort: null,
                ArticleId: null,
                error,
                SourceExchange: null);
    }
}
