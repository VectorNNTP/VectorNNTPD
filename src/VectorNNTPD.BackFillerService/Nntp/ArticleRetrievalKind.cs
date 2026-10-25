namespace VectorNNTPD.BackFillerService.Nntp
{
    /// <summary>Classification of one upstream retrieval attempt.</summary>
    internal enum ArticleRetrievalKind
    {
        /// <summary>ARTICLE succeeded and produced an owned destuffed payload.</summary>
        ArticleRetrieved = 0,

        /// <summary>Provider reported that the article does not exist or is unavailable (430 or ARTICLE 451).</summary>
        ArticleNotFound = 1,

        /// <summary>
        /// Destuffed ARTICLE bytes are not a usable article: the payload is empty or has no header/body separator,
        /// or it exceeded <see cref="NntpSessionOptions.MaxArticleBytes"/>.
        /// Distinct from transport, timeout, and protocol failures classified as <see cref="ProviderFailure"/>.
        /// </summary>
        InvalidArticle = 2,

        /// <summary>Transport, timeout, protocol, or remote rejection that is not a miss.</summary>
        ProviderFailure = 3,

        /// <summary>AUTHINFO failed or credentials are half-configured. Maps to ProviderFailure disposition.</summary>
        AuthenticationFailure = 4,

        /// <summary>Caller or shutdown cancellation.</summary>
        Cancelled = 5,
    }
}
