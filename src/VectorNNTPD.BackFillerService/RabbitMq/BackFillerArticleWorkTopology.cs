namespace VectorNNTPD.BackFillerService.RabbitMq
{
    /// <summary>
    /// Declares the durable quorum fanout ArticleWork topology for one BackFiller backbone.
    /// </summary>
    /// <remarks>
    /// BackFiller owns per-backbone <c>backfiller.{backbone}</c> exchange, queue, and binding.
    /// Declaration is idempotent and never deletes or mutates incompatible existing entities.
    /// Topology is created when a backbone becomes usable (before Article Work consumers start)
    /// and is retained when the backbone later becomes inactive.
    /// </remarks>
    internal static class BackFillerArticleWorkTopology
    {
        /// <summary>Broker argument that selects the RabbitMQ queue type.</summary>
        internal const string QueueTypeArgumentName = "x-queue-type";

        /// <summary>Required queue type for durable BackFiller ArticleWork provider queues.</summary>
        internal const string QuorumQueueType = "quorum";

        /// <summary>Fanout exchange type used for provider endpoints.</summary>
        private const string FanoutExchangeType = "fanout";

        /// <summary>
        /// Declares the fanout exchange, durable quorum queue, and binding for
        /// <paramref name="backbone"/> using existing name/routing semantics.
        /// </summary>
        /// <param name="channel">Caller-owned manual-ack channel on the current connection generation.</param>
        /// <param name="backbone">Provider backbone label (for example <c>Giganews</c>).</param>
        /// <param name="cancellationToken">Token passed to each exchange, queue, and bind call.</param>
        /// <returns>A task that completes when the exchange, quorum queue, and binding have been declared.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="channel"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="backbone"/> is null or whitespace.</exception>
        /// <remarks>
        /// Exchange name, queue name, and binding routing key are all
        /// <see cref="BackFillerRabbitMqTopology.ComposeProviderEntity(string)"/> of <paramref name="backbone"/>.
        /// The exchange is durable <see cref="FanoutExchangeType"/> with no arguments and is not auto-delete.
        /// The queue is durable, not exclusive, and not auto-delete, with <see cref="QueueTypeArgumentName"/>
        /// set to <see cref="QuorumQueueType"/>. This method does not delete or rewrite an existing entity.
        /// Broker failures, including cancellation, propagate from the channel.
        /// </remarks>
        internal static async Task DeclareProviderEndpointAsync(
            VectorNNTPD.Common.Messaging.RabbitMq.IRabbitMqManualAckChannel channel,
            string backbone,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(channel);
            ArgumentException.ThrowIfNullOrWhiteSpace(backbone);

            var name = BackFillerRabbitMqTopology.ComposeProviderEntity(backbone);
            await channel.ExchangeDeclareAsync(
                    name,
                    FanoutExchangeType,
                    durable: true,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken)
                .ConfigureAwait(false);

            await channel.QueueDeclareAsync(
                    name,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [QueueTypeArgumentName] = QuorumQueueType,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            await channel.QueueBindAsync(
                    name,
                    name,
                    name,
                    arguments: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
