using VectorNNTPD.Common.Messaging.Cache;

namespace VectorNNTPD.ReaderService.RabbitMq
{
    /// <summary>
    /// Constants for the StorageServer fleet article-presence lookup path.
    /// </summary>
    /// <remarks>
    /// <c>cache.requests</c> is a fanout exchange only. There is no shared quorum work queue
    /// and no BackFiller provider semantics. See <see cref="CacheRequestsTopology"/>.
    /// </remarks>
    internal static class StorageArticleRetrievalTopology
    {
        /// <summary>Fanout exchange name for fleet article-presence lookups.</summary>
        internal const string ExchangeName = CacheFleetTopology.RequestsExchangeName;

        /// <summary>Alias for <see cref="ExchangeName"/> used by older ArticleWork test fixtures.</summary>
        internal const string EntityName = ExchangeName;

        /// <summary>
        /// Historical JSON <c>backbone</c> label used only in ArticleWork tests that assert
        /// Storage is not a BackFiller provider. Not a BackFiller backbone identifier.
        /// </summary>
        internal const string Backbone = "Storage";
    }
}
