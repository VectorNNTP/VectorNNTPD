namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>
    /// Caller-owned RabbitMQ channel used by NNTPD article-work RPC publish and consume.
    /// </summary>
    /// <remarks>
    /// <see cref="RabbitMqService"/> remains the sole TCP connection owner. The RPC service
    /// owns this channel's lifetime and must not dispose the connection.
    /// </remarks>
    internal interface IRabbitMqRpcChannel : IAsyncDisposable
    {
        /// <summary>Gets the connection generation this channel was opened against.</summary>
        long Generation { get; }

        /// <summary>Declares a queue using RabbitMQ's idempotent declare semantics.</summary>
        /// <param name="queue">Queue name to declare.</param>
        /// <param name="durable"><see langword="true"/> when the queue survives broker restart.</param>
        /// <param name="exclusive"><see langword="true"/> when the queue is exclusive to one connection.</param>
        /// <param name="autoDelete"><see langword="true"/> when the queue is deleted when unused.</param>
        /// <param name="arguments">Optional queue arguments; <see langword="null"/> when none apply.</param>
        /// <param name="cancellationToken">Token used to cancel the declaration.</param>
        Task QueueDeclareAsync(
            string queue,
            bool durable,
            bool exclusive,
            bool autoDelete,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken);

        /// <summary>
        /// Publishes one AMQP message with RPC properties. Publication failure must be surfaced
        /// to the caller; the channel does not retry onto another generation.
        /// </summary>
        /// <param name="exchange">Destination exchange.</param>
        /// <param name="routingKey">Routing key used for the publication.</param>
        /// <param name="correlationId">AMQP correlation identifier for this publication.</param>
        /// <param name="requestId">Logical lookup UUID carried as the AMQP <c>RequestId</c> property.</param>
        /// <param name="replyTo">NNTPD reply-queue name.</param>
        /// <param name="contentType">AMQP content type. Article-work uses <c>application/json</c>.</param>
        /// <param name="expiration">
        /// AMQP expiration in milliseconds, or empty/whitespace to omit the property.
        /// Article-work requests omit expiration so ready work may buffer; other RPCs may supply a TTL.
        /// </param>
        /// <param name="body">Application payload bytes.</param>
        /// <param name="cancellationToken">Token used to cancel the publish.</param>
        Task PublishAsync(
            string exchange,
            string routingKey,
            string correlationId,
            string requestId,
            string replyTo,
            string contentType,
            string expiration,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken);

        /// <summary>Starts a shared consumer on <paramref name="queue"/>.</summary>
        /// <param name="queue">Queue to consume.</param>
        /// <param name="onDelivery">Callback invoked for each delivery.</param>
        /// <param name="cancellationToken">Token used to cancel consumer start.</param>
        /// <returns>The broker consumer tag.</returns>
        Task<string> ConsumeAsync(
            string queue,
            Func<RabbitMqRpcDelivery, Task> onDelivery,
            CancellationToken cancellationToken);
    }

    /// <summary>One consumed RPC delivery.</summary>
    /// <param name="CorrelationId">AMQP correlation identifier, when present.</param>
    /// <param name="RequestId">AMQP <c>RequestId</c> property, when present.</param>
    /// <param name="ContentType">AMQP content type, when present.</param>
    /// <param name="Expiration">AMQP expiration in milliseconds, when present. Article-work responses use <c>1000</c>.</param>
    /// <param name="Body">Application payload bytes. The consumer may copy this before returning.</param>
    /// <param name="Generation">Connection generation of the consumer channel that received the delivery.</param>
    internal readonly record struct RabbitMqRpcDelivery(
        string? CorrelationId,
        string? RequestId,
        string? ContentType,
        string? Expiration,
        ReadOnlyMemory<byte> Body,
        long Generation);
}
