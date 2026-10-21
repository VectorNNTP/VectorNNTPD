using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.RabbitMq
{
    /// <summary>
    /// Declares the durable fanout Article Work topology for one BackFiller backbone.
    /// </summary>
    /// <remarks>
    /// BackFiller owns per-backbone <c>backfiller.{backbone}</c> exchange, queue, and binding.
    /// Queue type is derived from configured unique <c>RabbitMQ:Hosts</c> count: one host declares a
    /// classic queue; two or more declare a quorum queue with initial group size equal to that count.
    /// Declaration is idempotent and never deletes or mutates incompatible existing entities.
    /// Topology is created when a backbone becomes usable (before Article Work consumers start)
    /// and is retained when the backbone later becomes inactive.
    /// Successful quorum declaration does not prove quorum membership; membership is verified separately.
    /// </remarks>
    internal static class BackFillerArticleWorkTopology
    {
        /// <summary>Broker argument that selects the RabbitMQ queue type.</summary>
        internal const string QueueTypeArgumentName = "x-queue-type";

        /// <summary>Queue type for single-host durable BackFiller Article Work provider queues.</summary>
        internal const string ClassicQueueType = "classic";

        /// <summary>Queue type for multi-host durable BackFiller Article Work provider queues.</summary>
        internal const string QuorumQueueType = "quorum";

        /// <summary>Broker argument for the initial quorum replica group size at queue creation.</summary>
        internal const string QuorumInitialGroupSizeArgumentName = "x-quorum-initial-group-size";

        /// <summary>Broker argument that caps how many times a quorum message may be delivered before dead-letter/drop.</summary>
        internal const string DeliveryLimitArgumentName = "x-delivery-limit";

        /// <summary>Fanout exchange type used for provider endpoints.</summary>
        private const string FanoutExchangeType = "fanout";

        /// <summary>
        /// Declares the fanout exchange, durable provider queue, and binding for
        /// <paramref name="backbone"/> using existing name/routing semantics.
        /// </summary>
        /// <param name="channel">Caller-owned manual-ack channel on the current connection generation.</param>
        /// <param name="backbone">Provider backbone label (for example <c>Giganews</c>).</param>
        /// <param name="runtime">Runtime snapshot supplying Hosts-derived queue topology and delivery limit.</param>
        /// <param name="cancellationToken">Token passed to each exchange, queue, and bind call.</param>
        /// <returns>A task that completes when the exchange, queue, and binding have been declared.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="channel"/> or <paramref name="runtime"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="backbone"/> is null or whitespace.</exception>
        /// <remarks>
        /// Exchange name, queue name, and binding routing key are all
        /// <see cref="BackFillerRabbitMqTopology.ComposeProviderEntity(string)"/> of <paramref name="backbone"/>.
        /// The exchange is durable <see cref="FanoutExchangeType"/> with no arguments and is not auto-delete.
        /// The queue is durable, not exclusive, and not auto-delete. Arguments follow
        /// <see cref="BackFillerRabbitMqRuntimeOptions.QueueTopology"/>:
        /// classic queues set <see cref="QueueTypeArgumentName"/> to <see cref="ClassicQueueType"/> only;
        /// quorum queues also set <see cref="QuorumInitialGroupSizeArgumentName"/> and
        /// <see cref="DeliveryLimitArgumentName"/>. Classic brokers do not accept quorum-only arguments.
        /// This method does not delete or rewrite an existing entity. Inequivalent arguments surface as
        /// broker <c>PRECONDITION_FAILED</c>. Broker failures, including cancellation, propagate from the channel.
        /// </remarks>
        internal static async Task DeclareProviderEndpointAsync(
            VectorNNTPD.Common.Messaging.RabbitMq.IRabbitMqManualAckChannel channel,
            string backbone,
            BackFillerRuntimeOptions runtime,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(channel);
            ArgumentNullException.ThrowIfNull(runtime);
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
                    arguments: BuildQueueArguments(runtime.RabbitMq),
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

        /// <summary>
        /// Builds durable provider-queue arguments from the Hosts-derived topology and delivery limit.
        /// </summary>
        /// <param name="rabbitMq">Projected Article Work RabbitMQ knobs.</param>
        /// <returns>Argument dictionary for <c>Queue.Declare</c>.</returns>
        internal static Dictionary<string, object?> BuildQueueArguments(BackFillerRabbitMqRuntimeOptions rabbitMq)
        {
            ArgumentNullException.ThrowIfNull(rabbitMq);

            var topology = rabbitMq.QueueTopology;
            if (topology.Kind == ArticleWorkQueueKind.Classic)
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [QueueTypeArgumentName] = ClassicQueueType,
                };
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [QueueTypeArgumentName] = QuorumQueueType,
                // RabbitMQ AMQP field tables accept long for these integer arguments.
                [QuorumInitialGroupSizeArgumentName] = (long)topology.QuorumInitialGroupSize!.Value,
                [DeliveryLimitArgumentName] = (long)rabbitMq.DeliveryLimit,
            };
        }
    }
}
