namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>
    /// Caller-owned AMQP channel for topology declaration, manual-ack consume, and settlement.
    /// </summary>
    /// <remarks>
    /// The channel owner disposes the channel. Disposing a channel must not dispose
    /// the process RabbitMQ connection. Settlement for a delivery must use this same
    /// instance; a replacement channel must not ACK or NACK another channel's tags.
    /// Publisher confirms are not enabled on this channel.
    /// </remarks>
    internal interface IRabbitMqManualAckChannel : IAsyncDisposable
    {
        /// <summary>Gets the connection generation this channel was opened against.</summary>
        long Generation { get; }

        /// <summary>
        /// Gets a value indicating whether the channel can still accept broker operations.
        /// <see langword="false"/> after disposal has marked the channel closed, including while the client close is still running.
        /// </summary>
        bool IsOpen { get; }

        /// <summary>
        /// Raised once when this channel becomes unexpectedly unusable while the process connection may still be open.
        /// Expected application cancel and disposal do not raise this event.
        /// </summary>
        event EventHandler<RabbitMqChannelFaultedEventArgs>? ChannelFaulted;

        /// <summary>
        /// Declares an exchange. Idempotent for compatible existing entities; incompatible
        /// existing entities fail closed (never deleted or mutated).
        /// </summary>
        Task ExchangeDeclareAsync(
            string exchange,
            string type,
            bool durable,
            bool autoDelete,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken);

        /// <summary>
        /// Declares a queue. Idempotent for compatible existing entities; incompatible
        /// existing entities fail closed (never deleted or mutated).
        /// </summary>
        Task QueueDeclareAsync(
            string queue,
            bool durable,
            bool exclusive,
            bool autoDelete,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken);

        /// <summary>Binds <paramref name="queue"/> to <paramref name="exchange"/>.</summary>
        Task QueueBindAsync(
            string queue,
            string exchange,
            string routingKey,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken);

        /// <summary>
        /// Starts the single manual-ack consumer on <paramref name="queue"/> with the given prefetch.
        /// A second call throws <see cref="InvalidOperationException"/>.
        /// A failed or cancelled start detaches the handler before the error propagates.
        /// </summary>
        /// <param name="queue">Queue previously declared for consume.</param>
        /// <param name="prefetchCount">Per-channel prefetch.</param>
        /// <param name="onDelivery">Invoked for each delivery. The callback must settle or the session must retire.</param>
        /// <param name="cancellationToken">Token used to cancel consumer start.</param>
        /// <returns>The broker consumer tag.</returns>
        Task<string> BasicConsumeAsync(
            string queue,
            ushort prefetchCount,
            Func<RabbitMqManualAckDelivery, Task> onDelivery,
            CancellationToken cancellationToken);

        /// <summary>Cancels the consumer tag on this channel.</summary>
        /// <param name="consumerTag">Tag returned by <see cref="BasicConsumeAsync"/>.</param>
        /// <param name="cancellationToken">Token used to cancel the cancel RPC.</param>
        Task BasicCancelAsync(string consumerTag, CancellationToken cancellationToken);

        /// <summary>Acknowledges <paramref name="deliveryTag"/> on this channel only.</summary>
        /// <param name="deliveryTag">Channel-scoped delivery tag.</param>
        /// <param name="cancellationToken">Token used to cancel the ACK.</param>
        Task BasicAckAsync(ulong deliveryTag, CancellationToken cancellationToken);

        /// <summary>
        /// Negatively acknowledges <paramref name="deliveryTag"/> on this channel only via AMQP <c>basic.nack</c>
        /// with <c>multiple=false</c>.
        /// </summary>
        /// <param name="deliveryTag">Channel-scoped delivery tag.</param>
        /// <param name="requeue">Whether the broker should requeue the message.</param>
        /// <param name="cancellationToken">Token used to cancel the NACK.</param>
        /// <remarks>
        /// On RabbitMQ quorum queues from 4.3, <c>basic.nack</c> does not increment <c>x-delivery-count</c>.
        /// Article Work poison-message settlement uses <see cref="BasicRejectAsync"/> instead.
        /// </remarks>
        Task BasicNackAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken);

        /// <summary>
        /// Rejects <paramref name="deliveryTag"/> on this channel only via AMQP <c>basic.reject</c>.
        /// </summary>
        /// <param name="deliveryTag">Channel-scoped delivery tag.</param>
        /// <param name="requeue">Whether the broker should requeue the message.</param>
        /// <param name="cancellationToken">Token used to cancel the reject.</param>
        /// <remarks>
        /// Marks the delivery attempt as failed for quorum-queue poison-message handling so
        /// <c>x-delivery-count</c> increments under RabbitMQ 4.3+ rules.
        /// </remarks>
        Task BasicRejectAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken);
    }
}
