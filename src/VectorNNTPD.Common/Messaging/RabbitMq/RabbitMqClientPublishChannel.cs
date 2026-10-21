using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>
    /// Confirm-enabled RabbitMQ.Client publish channel for one-way handoffs.
    /// </summary>
    /// <remarks>
    /// Created with <c>publisherConfirmationsEnabled</c> and
    /// <c>publisherConfirmationTrackingEnabled</c>. Completing
    /// <see cref="PublishConfirmedAsync(RabbitMqConfirmedPublication, CancellationToken)"/> means the
    /// broker confirmed. A completed <c>BasicPublishAsync</c> without those options is not a confirmation.
    /// </remarks>
    internal sealed class RabbitMqClientPublishChannel : IRabbitMqPublishChannel
    {
        /// <summary>
        /// Owned confirm-enabled channel. <see cref="DisposeAsync"/> closes it when open, swallowing close errors, then disposes it.
        /// </summary>
        private readonly IChannel _channel;

        /// <summary><c>1</c> after the first <see cref="DisposeAsync"/>.</summary>
        private int _disposed;

        /// <summary><c>1</c> after an unexpected fault has been raised once for this channel.</summary>
        private int _faultRaised;

        /// <summary>Initializes a new wrapper around an opened confirm-enabled channel.</summary>
        internal RabbitMqClientPublishChannel(IChannel channel, long generation)
        {
            ArgumentNullException.ThrowIfNull(channel);
            _channel = channel;
            Generation = generation;
            _channel.ChannelShutdownAsync += OnChannelShutdownAsync;
        }

        /// <inheritdoc />
        public long Generation { get; }

        /// <inheritdoc />
        public event EventHandler<RabbitMqChannelFaultedEventArgs>? ChannelFaulted;

        /// <inheritdoc />
        public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _channel.IsOpen;

        /// <inheritdoc />
        public Task PublishConfirmedAsync(
            string exchange,
            string routingKey,
            string messageId,
            string appId,
            string expiration,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(exchange);
            ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            ArgumentException.ThrowIfNullOrWhiteSpace(appId);
            ArgumentException.ThrowIfNullOrWhiteSpace(expiration);

            return PublishConfirmedAsync(
                new RabbitMqConfirmedPublication(
                    Exchange: exchange,
                    RoutingKey: routingKey,
                    MessageId: messageId,
                    AppId: appId,
                    CorrelationId: null,
                    ContentType: null,
                    RequestIdHeader: null,
                    ExpirationMilliseconds: expiration,
                    Persistent: true,
                    Mandatory: true,
                    Body: body),
                cancellationToken);
        }

        /// <inheritdoc />
        public async Task PublishConfirmedAsync(
            RabbitMqConfirmedPublication publication,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(publication);
            ArgumentNullException.ThrowIfNull(publication.Exchange);
            ArgumentException.ThrowIfNullOrWhiteSpace(publication.RoutingKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(publication.MessageId);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (!_channel.IsOpen)
            {
                throw new InvalidOperationException("RabbitMQ publish channel is not open.");
            }

            var properties = CreateProperties(publication);

            try
            {
                await _channel.BasicPublishAsync(
                        publication.Exchange,
                        publication.RoutingKey,
                        mandatory: publication.Mandatory,
                        basicProperties: properties,
                        body: publication.Body,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PublishException ex)
            {
                throw new InvalidOperationException(
                    ex.IsReturn
                        ? "RabbitMQ returned the publication as unroutable."
                        : "RabbitMQ negatively acknowledged the publication.",
                    ex);
            }

            if (!_channel.IsOpen)
            {
                throw new InvalidOperationException("RabbitMQ publish channel closed after publication.");
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _channel.ChannelShutdownAsync -= OnChannelShutdownAsync;

            try
            {
                if (_channel.IsOpen)
                {
                    await _channel.CloseAsync().ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
            }

            await _channel.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>Raises <see cref="ChannelFaulted"/> once for an unexpected channel shutdown.</summary>
        /// <param name="sender">Unused.</param>
        /// <param name="eventArgs">Client shutdown details.</param>
        private Task OnChannelShutdownAsync(object sender, ShutdownEventArgs eventArgs)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return Task.CompletedTask;
            }

            if (Interlocked.Exchange(ref _faultRaised, 1) == 1)
            {
                return Task.CompletedTask;
            }

            var replyText = string.IsNullOrWhiteSpace(eventArgs.ReplyText)
                ? "(none)"
                : eventArgs.ReplyText;
            ChannelFaulted?.Invoke(
                this,
                new RabbitMqChannelFaultedEventArgs(
                    RabbitMqChannelFaultKind.ChannelShutdown,
                    $"Channel shutdown initiator={eventArgs.Initiator} code={eventArgs.ReplyCode} text={replyText}"));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Builds AMQP properties for a confirmed publication.
        /// </summary>
        internal static BasicProperties CreateProperties(RabbitMqConfirmedPublication publication)
        {
            ArgumentNullException.ThrowIfNull(publication);
            var properties = new BasicProperties
            {
                MessageId = publication.MessageId,
                DeliveryMode = publication.Persistent ? DeliveryModes.Persistent : DeliveryModes.Transient,
                Persistent = publication.Persistent,
            };

            if (!string.IsNullOrWhiteSpace(publication.ExpirationMilliseconds))
            {
                properties.Expiration = publication.ExpirationMilliseconds;
            }

            if (!string.IsNullOrWhiteSpace(publication.ContentEncoding))
            {
                properties.ContentEncoding = publication.ContentEncoding;
            }

            if (publication.Timestamp is { } timestamp)
            {
                properties.Timestamp = new AmqpTimestamp(timestamp.ToUnixTimeSeconds());
            }

            if (!string.IsNullOrWhiteSpace(publication.AppId))
            {
                properties.AppId = publication.AppId;
            }

            if (!string.IsNullOrWhiteSpace(publication.CorrelationId))
            {
                properties.CorrelationId = publication.CorrelationId;
            }

            if (!string.IsNullOrWhiteSpace(publication.ContentType))
            {
                properties.ContentType = publication.ContentType;
            }

            if (!string.IsNullOrWhiteSpace(publication.RequestIdHeader))
            {
                properties.Headers = new Dictionary<string, object?>
                {
                    ["RequestId"] = publication.RequestIdHeader,
                };
            }

            return properties;
        }

        /// <summary>
        /// Builds the AMQP properties sent on OverviewDB-shaped publications.
        /// </summary>
        /// <param name="messageId">Fresh UUID string for this publish attempt.</param>
        /// <param name="appId">Generated application FQDN.</param>
        /// <param name="expiration">Per-message TTL in milliseconds, as an AMQP string.</param>
        /// <returns>The properties instance passed to <c>BasicPublishAsync</c>.</returns>
        internal static BasicProperties CreateHandoffProperties(string messageId, string appId, string expiration)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            ArgumentException.ThrowIfNullOrWhiteSpace(appId);
            ArgumentException.ThrowIfNullOrWhiteSpace(expiration);
            return CreateProperties(
                new RabbitMqConfirmedPublication(
                    Exchange: string.Empty,
                    RoutingKey: "placeholder",
                    MessageId: messageId,
                    AppId: appId,
                    CorrelationId: null,
                    ContentType: null,
                    RequestIdHeader: null,
                    ExpirationMilliseconds: expiration,
                    Persistent: true,
                    Mandatory: true,
                    Body: ReadOnlyMemory<byte>.Empty));
        }
    }
}
