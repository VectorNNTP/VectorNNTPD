namespace VectorNNTPD.ReaderService.RabbitMq
{
    /// <summary>
    /// Immutable BackFiller-compatible topology for one article-retrieval provider.
    /// </summary>
    /// <param name="Provider">Canonical provider identifier. Casing is preserved.</param>
    /// <param name="ExchangeName">Exchange that receives article-retrieval work for the provider.</param>
    /// <param name="ExchangeType">RabbitMQ exchange type. BackFiller uses fanout.</param>
    /// <param name="ExchangeDurable">Whether the exchange survives broker restart.</param>
    /// <param name="ExchangeAutoDelete">Whether the exchange is auto-deleted when unused.</param>
    /// <param name="QueueName">Queue bound to the provider exchange.</param>
    /// <param name="QueueDurable">Whether the queue survives broker restart.</param>
    /// <param name="QueueExclusive">Whether the queue is exclusive to a single connection.</param>
    /// <param name="QueueAutoDelete">Whether the queue is auto-deleted when unused.</param>
    /// <param name="RoutingKey">Routing key used when binding the queue to the exchange.</param>
    /// <param name="QueueArguments">
    /// Queue arguments applied during declaration. Durable provider work queues require
    /// <c>x-queue-type=quorum</c>.
    /// </param>
    internal sealed record BackfillArticleRetrievalTopologyDefinition(
        string Provider,
        string ExchangeName,
        string ExchangeType,
        bool ExchangeDurable,
        bool ExchangeAutoDelete,
        string QueueName,
        bool QueueDurable,
        bool QueueExclusive,
        bool QueueAutoDelete,
        string RoutingKey,
        IReadOnlyDictionary<string, object?> QueueArguments)
    {
        /// <summary>Projects this BackFiller definition onto the shared declare shape.</summary>
        internal RabbitMqArticleRetrievalEndpoint ToEndpoint() =>
            new(
                ExchangeName,
                ExchangeType,
                ExchangeDurable,
                ExchangeAutoDelete,
                QueueName,
                QueueDurable,
                QueueExclusive,
                QueueAutoDelete,
                RoutingKey,
                QueueArguments);
    }

    /// <summary>
    /// Fixed article-retrieval topology shared with VectorNNTP.BackFiller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Provider identifiers are application constants, not configuration. The twelve names
    /// match BackFiller's backbone set. Entity names are <c>backfiller.{provider}</c>
    /// after <see cref="RabbitMqTopologyNames"/> normalization, for the exchange, queue,
    /// and routing key.
    /// </para>
    /// <para>
    /// Every queue name matches the durable quorum fanout shape BackFiller declares when a
    /// backbone becomes usable (<c>x-queue-type=quorum</c>). NNTPD does not declare or consume
    /// these queues; it publishes sequential ArticleWork RPC requests onto selected provider
    /// queues that already exist and have active consumers.
    /// </para>
    /// </remarks>
    internal static class BackfillArticleRetrievalTopology
    {
        /// <summary>Broker argument that selects the RabbitMQ queue type.</summary>
        internal const string QueueTypeArgumentName = RabbitMqArticleRetrievalEndpoints.QueueTypeArgumentName;

        /// <summary>Required queue type for durable BackFiller ArticleWork provider queues.</summary>
        internal const string QuorumQueueType = RabbitMqArticleRetrievalEndpoints.QuorumQueueType;

        /// <summary>
        /// Canonical BackFiller provider identifiers. Casing is significant and is not normalized.
        /// </summary>
        internal static readonly string[] Providers =
        [
            "Abavia",
            "Altopia",
            "BaseIP",
            "Eweka",
            "Elbracht",
            "Giganews",
            "GTT",
            "Highwinds",
            "ItsHosted",
            "Novia",
            "UExpress",
            "UsenetNode1",
        ];

        /// <summary>Complete article-retrieval topology in provider-list order.</summary>
        internal static IReadOnlyList<BackfillArticleRetrievalTopologyDefinition> Definitions { get; } =
            BuildDefinitions(Providers);

        /// <summary>
        /// Builds topology definitions for the supplied provider identifiers.
        /// </summary>
        /// <param name="providers">Provider names whose exchanges and queues must exist.</param>
        /// <returns>One definition per non-blank unique provider, preserving first-seen order.</returns>
        internal static IReadOnlyList<BackfillArticleRetrievalTopologyDefinition> BuildDefinitions(
            IEnumerable<string> providers)
        {
            ArgumentNullException.ThrowIfNull(providers);

            var uniqueProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var definitions = new List<BackfillArticleRetrievalTopologyDefinition>();

            foreach (var provider in providers)
            {
                if (string.IsNullOrWhiteSpace(provider))
                {
                    continue;
                }

                var canonicalProvider = provider.Trim();
                if (!uniqueProviders.Add(canonicalProvider))
                {
                    continue;
                }

                var endpoint = RabbitMqArticleRetrievalEndpoints.CreateFanoutQuorumBinding(
                    BuildProviderEntityName(canonicalProvider));
                definitions.Add(new BackfillArticleRetrievalTopologyDefinition(
                    Provider: canonicalProvider,
                    ExchangeName: endpoint.ExchangeName,
                    ExchangeType: endpoint.ExchangeType,
                    ExchangeDurable: endpoint.ExchangeDurable,
                    ExchangeAutoDelete: endpoint.ExchangeAutoDelete,
                    QueueName: endpoint.QueueName,
                    QueueDurable: endpoint.QueueDurable,
                    QueueExclusive: endpoint.QueueExclusive,
                    QueueAutoDelete: endpoint.QueueAutoDelete,
                    RoutingKey: endpoint.RoutingKey,
                    QueueArguments: endpoint.QueueArguments));
            }

            return definitions;
        }

        /// <summary>
        /// Builds the NNTPD BackFiller exchange, queue, and routing-key name for a provider.
        /// </summary>
        /// <param name="provider">Provider identifier. Casing and surrounding whitespace are normalized.</param>
        /// <returns>The normalized entity name in the form <c>backfiller.{provider}</c>.</returns>
        internal static string BuildProviderEntityName(string provider) =>
            RabbitMqTopologyNames.Compose("backfiller", provider);
    }
}
