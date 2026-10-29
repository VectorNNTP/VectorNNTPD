using RabbitMQ.Client;

namespace VectorNNTPD.ReaderService.RabbitMq
{
    /// <summary>
    /// One declare-only article-retrieval exchange, durable quorum queue, and binding.
    /// </summary>
    /// <param name="ExchangeName">Exchange that receives article-retrieval requests.</param>
    /// <param name="ExchangeType">RabbitMQ exchange type. Article-retrieval uses fanout.</param>
    /// <param name="ExchangeDurable">Whether the exchange survives broker restart.</param>
    /// <param name="ExchangeAutoDelete">Whether the exchange is auto-deleted when unused.</param>
    /// <param name="QueueName">Queue bound to the exchange.</param>
    /// <param name="QueueDurable">Whether the queue survives broker restart.</param>
    /// <param name="QueueExclusive">Whether the queue is exclusive to a single connection.</param>
    /// <param name="QueueAutoDelete">Whether the queue is auto-deleted when unused.</param>
    /// <param name="RoutingKey">Routing key used when binding the queue to the exchange.</param>
    /// <param name="QueueArguments">
    /// Queue arguments applied during declaration. Durable article-retrieval work queues
    /// require <c>x-queue-type=quorum</c>.
    /// </param>
    internal sealed record RabbitMqArticleRetrievalEndpoint(
        string ExchangeName,
        string ExchangeType,
        bool ExchangeDurable,
        bool ExchangeAutoDelete,
        string QueueName,
        bool QueueDurable,
        bool QueueExclusive,
        bool QueueAutoDelete,
        string RoutingKey,
        IReadOnlyDictionary<string, object?> QueueArguments);

    /// <summary>
    /// Shared factory for NNTPD/BackFiller article-retrieval broker entities.
    /// </summary>
    /// <remarks>
    /// Used by BackFiller provider endpoints. Storage fleet lookup uses
    /// <see cref="CacheRequestsTopology"/> (fanout exchange only) and is not declared through
    /// this factory.
    /// </remarks>
    internal static class RabbitMqArticleRetrievalEndpoints
    {
        /// <summary>Broker argument that selects the RabbitMQ queue type.</summary>
        internal const string QueueTypeArgumentName = "x-queue-type";

        /// <summary>Required queue type for every durable article-retrieval work queue.</summary>
        internal const string QuorumQueueType = "quorum";

        /// <summary>
        /// Builds a durable fanout exchange, durable quorum queue, and binding that share
        /// the normalized <paramref name="entityName"/> as the exchange, queue, and routing key.
        /// </summary>
        internal static RabbitMqArticleRetrievalEndpoint CreateFanoutQuorumBinding(string entityName)
        {
            var name = RabbitMqTopologyNames.Normalize(entityName);
            return new RabbitMqArticleRetrievalEndpoint(
                ExchangeName: name,
                ExchangeType: ExchangeType.Fanout,
                ExchangeDurable: true,
                ExchangeAutoDelete: false,
                QueueName: name,
                QueueDurable: true,
                QueueExclusive: false,
                QueueAutoDelete: false,
                RoutingKey: name,
                QueueArguments: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [QueueTypeArgumentName] = QuorumQueueType,
                });
        }
    }

    /// <summary>
    /// NNTPD-owned BackFiller-compatible article-retrieval endpoints declared at topology start.
    /// </summary>
    /// <remarks>
    /// Empty: per-backbone <c>backfiller.*</c> topology is declared by BackFiller.
    /// Storage fleet lookup declares only the <c>cache.requests</c> fanout exchange (no shared queue).
    /// </remarks>
    internal static class ArticleRetrievalTopology
    {
        /// <summary>NNTPD-owned BackFiller-style article-retrieval endpoints declared at startup.</summary>
        internal static IReadOnlyList<RabbitMqArticleRetrievalEndpoint> Required { get; } =
            Array.Empty<RabbitMqArticleRetrievalEndpoint>();
    }
}
