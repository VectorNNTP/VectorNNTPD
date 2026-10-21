namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>
    /// Caller-owned confirm-enabled RabbitMQ channel used for one-way publications.
    /// </summary>
    /// <remarks>
    /// <see cref="RabbitMqService"/> remains the sole TCP connection owner. Completing
    /// <see cref="PublishConfirmedAsync(RabbitMqConfirmedPublication, CancellationToken)"/> means the
    /// broker confirmed. The channel does not retry onto another generation.
    /// </remarks>
    internal interface IRabbitMqPublishChannel : IAsyncDisposable
    {
        /// <summary>Gets the connection generation this channel was opened against.</summary>
        long Generation { get; }

        /// <summary>Gets whether the channel currently reports itself open.</summary>
        bool IsOpen { get; }

        /// <summary>
        /// Raised once when this publish channel becomes unexpectedly unusable while the process connection may still be open.
        /// Disposal does not raise this event.
        /// </summary>
        event EventHandler<RabbitMqChannelFaultedEventArgs>? ChannelFaulted;

        /// <summary>
        /// Publishes one message described by <paramref name="publication"/> and waits for a publisher confirmation.
        /// </summary>
        /// <param name="publication">Publication properties and body.</param>
        /// <param name="cancellationToken">Token used to cancel the publish or confirm wait.</param>
        Task PublishConfirmedAsync(RabbitMqConfirmedPublication publication, CancellationToken cancellationToken);

        /// <summary>
        /// Publishes one persistent mandatory message and waits for a publisher confirmation.
        /// </summary>
        /// <param name="exchange">Destination exchange. Empty string is the default exchange.</param>
        /// <param name="routingKey">Routing key used for the publication.</param>
        /// <param name="messageId">Fresh AMQP MessageId for this publish attempt.</param>
        /// <param name="appId">AMQP AppId; NNTPD uses the generated application FQDN.</param>
        /// <param name="expiration">AMQP per-message expiration in milliseconds.</param>
        /// <param name="body">Application payload bytes.</param>
        /// <param name="cancellationToken">Token used to cancel the publish or confirm wait.</param>
        /// <remarks>
        /// Implemented via <see cref="PublishConfirmedAsync(RabbitMqConfirmedPublication, CancellationToken)"/>
        /// with <c>Persistent=true</c> and <c>Mandatory=true</c>.
        /// </remarks>
        Task PublishConfirmedAsync(
            string exchange,
            string routingKey,
            string messageId,
            string appId,
            string expiration,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken);
    }
}
