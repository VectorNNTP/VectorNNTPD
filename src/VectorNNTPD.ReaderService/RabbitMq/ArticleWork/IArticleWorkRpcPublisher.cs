namespace VectorNNTPD.ReaderService.RabbitMq.ArticleWork
{
    /// <summary>Publishes one article-work RPC request onto the current RabbitMQ generation.</summary>
    internal interface IArticleWorkRpcPublisher
    {
        /// <summary>Gets the NNTPD-owned reply queue name used as AMQP <c>ReplyTo</c>.</summary>
        string ReplyTo { get; }

        /// <summary>
        /// Publishes a compact v1 JSON body with AMQP RPC metadata:
        /// <c>RequestId</c>, <c>CorrelationId</c>, <c>ReplyTo</c>, and
        /// <c>ContentType=application/json</c>. Work requests omit AMQP <c>Expiration</c>
        /// so the broker may buffer ready messages until a BackFiller consumer delivers them.
        /// </summary>
        /// <param name="exchange">Destination fanout exchange.</param>
        /// <param name="routingKey">Routing key matching the declared binding.</param>
        /// <param name="requestId">Logical lookup UUID. Also written to JSON <c>requestId</c>.</param>
        /// <param name="correlationId">Fresh AMQP correlation identity for this publication only.</param>
        /// <param name="body">Canonical compact request JSON.</param>
        /// <param name="cancellationToken">Token used to cancel the publish.</param>
        Task PublishAsync(
            string exchange,
            string routingKey,
            Guid requestId,
            string correlationId,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken);
    }
}
