
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// Result of handling an already-validated work item.
    /// </summary>
    /// <param name="Outcome">Processing outcome. Must not be <see cref="ArticleWorkOutcome.InvalidRequest"/>.</param>
    /// <param name="Error">Optional diagnostic reason. Never a secret.</param>
    /// <param name="Article">Owned retrieved payload when not transferred into retention. Caller/pipeline must dispose of it.</param>
    /// <param name="Fqdn">Success BackFiller FQDN when retention admitted or already held the article.</param>
    /// <param name="ListenerPort">Success TLS listener port when retention admitted or already held the article.</param>
    /// <param name="ArticleId">Success CanonicalV1 ArtId when retention admitted or already held the article.</param>
    internal readonly record struct ArticleWorkHandlerResult(
        ArticleWorkOutcome Outcome,
        string? Error,
        Nntp.RetrievedArticle? Article = null,
        string? Fqdn = null,
        int? ListenerPort = null,
        ArticleId? ArticleId = null);

    /// <summary>
    /// Processes admitted Article Work. Retrieval and retention happen here; publication and ACK do not.
    /// </summary>
    internal interface IArticleWorkHandler
    {
        /// <summary>
        /// Handles one validated work item.
        /// </summary>
        /// <param name="item">Admitted work.</param>
        /// <param name="cancellationToken">Processing cancellation. Distinct from host shutdown only when linked that way by the caller.</param>
        /// <returns>The processing outcome used for disposition.</returns>
        ValueTask<ArticleWorkHandlerResult> HandleAsync(ArticleWorkItem item, CancellationToken cancellationToken);
    }
}
