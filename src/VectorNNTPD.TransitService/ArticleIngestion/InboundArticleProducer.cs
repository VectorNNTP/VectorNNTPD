namespace VectorNNTPD.TransitService.ArticleIngestion
{
    /// <summary>Identifies which ingest command produced an <see cref="InboundArticle"/>.</summary>
    public enum InboundArticleProducer
    {
        /// <summary>
        /// TAKETHIS. Ingress builds <see cref="VectorNNTPD.Common.Articles.ArticleRecord"/> before admission;
        /// <see cref="InboundArticle.Payload"/> aliases canonical ArtData.
        /// </summary>
        TakeThis = 0,

        /// <summary>
        /// IHAVE. Ingress destuffs stuffed wire and builds
        /// <see cref="VectorNNTPD.Common.Articles.ArticleRecord"/> before admission.
        /// <see cref="InboundArticle.Payload"/> aliases canonical ArtData.
        /// </summary>
        IHave = 1,

        /// <summary>
        /// POST. Ingress builds <see cref="VectorNNTPD.Common.Articles.ArticleRecord"/> after existing POST
        /// receive/validate/normalize and before admission.
        /// <see cref="InboundArticle.Payload"/> aliases canonical ArtData.
        /// </summary>
        Post = 2,

        /// <summary>
        /// BackFiller VATP retrieval. The CanonicalV1 <see cref="VectorNNTPD.Common.Articles.ArticleRecord"/>
        /// is already validated; <see cref="InboundArticle.Payload"/> aliases ArtData with no destuff/parse.
        /// </summary>
        BackFiller = 3,
    }
}
