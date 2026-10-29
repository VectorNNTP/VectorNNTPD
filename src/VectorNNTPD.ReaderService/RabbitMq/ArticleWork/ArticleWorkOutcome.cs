namespace VectorNNTPD.ReaderService.RabbitMq.ArticleWork
{
    /// <summary>Canonical BackFiller v1 article-work response outcomes that NNTPD classifies.</summary>
    public enum ArticleWorkOutcome
    {
        /// <summary>The source located the article and supplied a cache URI.</summary>
        Success = 0,

        /// <summary>The source did not have the article.</summary>
        ArticleNotFound = 1,

        /// <summary>The source rejected the article as invalid.</summary>
        InvalidArticle = 2,

        /// <summary>The source rejected the request as protocol-invalid.</summary>
        InvalidRequest = 3,
    }
}
