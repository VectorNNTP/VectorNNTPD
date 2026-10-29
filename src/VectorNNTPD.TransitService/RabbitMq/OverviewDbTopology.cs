namespace VectorNNTPD.TransitService.RabbitMq
{
    /// <summary>
    /// Broker topology for the one-way OverviewDB ingest handoff.
    /// </summary>
    /// <remarks>
    /// Publishes through the default exchange with routing key
    /// <see cref="QueueName"/>. No dedicated exchange, bind, reply queue, or RPC
    /// topology is declared.
    /// </remarks>
    internal static class OverviewDbTopology
    {
        /// <summary>
        /// Durable quorum queue consumed independently by OverviewDB.
        /// </summary>
        /// <remarks>
        /// Declared with <c>x-queue-type=quorum</c>. Per-message AMQP expiration is
        /// supported on the current broker; there is no queue-wide <c>x-message-ttl</c>.
        /// </remarks>
        internal const string QueueName = "overviewdb.queue";

        /// <summary>Default-exchange routing key; equal to <see cref="QueueName"/>.</summary>
        internal const string RoutingKey = QueueName;

        /// <summary>AMQP default exchange (empty name).</summary>
        internal const string DefaultExchange = "";

        /// <summary>Broker argument that selects the RabbitMQ queue type.</summary>
        internal const string QueueTypeArgumentName = RabbitMqArticleRetrievalEndpoints.QueueTypeArgumentName;

        /// <summary>Required queue type for the OverviewDB ingest queue.</summary>
        internal const string QuorumQueueType = RabbitMqArticleRetrievalEndpoints.QuorumQueueType;

        /// <summary>Queue arguments applied during declaration.</summary>
        internal static IReadOnlyDictionary<string, object?> QueueArguments { get; } =
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [QueueTypeArgumentName] = QuorumQueueType,
            };

        /// <summary>Per-message AMQP expiration in milliseconds (<c>expiration</c> shortstr).</summary>
        /// <remarks>
        /// The broker interprets this as milliseconds. RabbitMQ discards expired
        /// messages when they reach the head of the queue; Ready counts can still
        /// include expired messages queued behind a non-expired head. This is not a
        /// queue-wide <c>x-message-ttl</c>. Value restored from the OverviewDB handoff
        /// design: <c>2000</c>.
        /// </remarks>
        internal const string ExpirationMilliseconds = "2000";

        /// <summary>
        /// AMQP mandatory flag. Unroutable publications are returned to the publisher
        /// instead of being confirmed and dropped.
        /// </summary>
        internal const bool Mandatory = true;
    }
}
