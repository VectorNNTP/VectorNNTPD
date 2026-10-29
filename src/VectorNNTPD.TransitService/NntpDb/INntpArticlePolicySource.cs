namespace VectorNNTPD.TransitService.NntpDb
{
    /// <summary>
    /// Supplies the article-size and Path-tracker values captured for one NNTPD article operation.
    /// </summary>
    public interface INntpArticlePolicySource
    {
        /// <summary>Copies the published shared configuration.</summary>
        /// <param name="maxArticleBytes">Published <c>maxartsize</c> in bytes.</param>
        /// <param name="siteName">Published <c>sitename</c>.</param>
        /// <returns><see langword="false"/> when no snapshot has been published.</returns>
        bool TryGetArticlePolicy(out int maxArticleBytes, out string siteName);
    }
}
