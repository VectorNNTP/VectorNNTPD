using System.Threading.RateLimiting;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using VectorNNTPD.Common.Core;

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

        /// <summary>
        /// Sole owner of the outstanding-confirm <see cref="ThrottlingRateLimiter"/>.
        /// The RabbitMQ.Client channel receives only a <see cref="RabbitMqBorrowedRateLimiter"/> facade.
        /// </summary>
        private readonly RateLimiter _outstandingConfirmationsRateLimiter;

        /// <summary><c>1</c> after the first <see cref="DisposeAsync"/>.</summary>
        private int _disposed;

        /// <summary><c>1</c> after an unexpected fault has been raised once for this channel.</summary>
        private int _faultRaised;

        /// <summary>
        /// Initializes a new wrapper around an opened confirm-enabled channel and the outstanding-confirm limiter
        /// supplied at channel creation.
        /// </summary>
        /// <param name="channel">Opened confirm-tracked RabbitMQ channel.</param>
        /// <param name="generation">Connection generation for this channel.</param>
        /// <param name="outstandingConfirmationsRateLimiter">
        /// Owned outstanding-confirm limiter (not the borrowed facade passed to <see cref="CreateChannelOptions"/>).
        /// </param>
        private RabbitMqClientPublishChannel(
            IChannel channel,
            long generation,
            RateLimiter outstandingConfirmationsRateLimiter)
        {
            ArgumentNullException.ThrowIfNull(channel);
            ArgumentNullException.ThrowIfNull(outstandingConfirmationsRateLimiter);
            _channel = channel;
            _outstandingConfirmationsRateLimiter = outstandingConfirmationsRateLimiter;
            Generation = generation;
            _channel.ChannelShutdownAsync += OnChannelShutdownAsync;
        }

        /// <summary>
        /// Opens a confirm-tracked channel with an explicit outstanding-confirm limiter owned by the returned wrapper.
        /// </summary>
        /// <remarks>
        /// <para>
        /// RabbitMQ.Client 7.2.2 disposes the limiter instance stored on <see cref="CreateChannelOptions"/>, so the
        /// client receives a <see cref="RabbitMqBorrowedRateLimiter"/> facade. The real
        /// <see cref="ThrottlingRateLimiter"/> is owned here and released with sync <see cref="IDisposable.Dispose"/>
        /// from <see cref="DisposeAsync"/> (RateLimiter's <c>DisposeAsync</c> ends in <c>Dispose(false)</c> and would
        /// not release the inner <c>ConcurrencyLimiter</c>).
        /// </para>
        /// <para>
        /// Construction uses <see cref="DisposableOwnership{T}"/> / <see cref="AsyncDisposableOwnership{T}"/> so
        /// failure paths dispose via <c>using</c> / <c>await using</c>, and success transfers ownership into this
        /// wrapper—matching <see cref="RabbitMqClientConnectionFactory.ConnectAsync"/>.
        /// </para>
        /// </remarks>
        /// <param name="connection">Open RabbitMQ connection used to create the channel.</param>
        /// <param name="generation">Connection generation stamped on the wrapper.</param>
        /// <param name="cancellationToken">Cancels channel open.</param>
        /// <returns>Wrapper that owns both the channel and the outstanding-confirm limiter.</returns>
        internal static async Task<RabbitMqClientPublishChannel> CreateConfirmTrackedAsync(
            IConnection connection,
            long generation,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(connection);

            using var limiterOwnership = new DisposableOwnership<ThrottlingRateLimiter>(
                new ThrottlingRateLimiter(RabbitMqPublisherConfirmLimits.MaxOutstandingConfirmations));
            var options = new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true,
                outstandingPublisherConfirmationsRateLimiter: new RabbitMqBorrowedRateLimiter(
                    limiterOwnership.Resource));
            var channel = await connection
                .CreateChannelAsync(options: options, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await using var channelOwnership = new AsyncDisposableOwnership<IChannel>(channel);
            var publishChannel = new RabbitMqClientPublishChannel(
                channelOwnership.Resource,
                generation,
                limiterOwnership.Resource);
            channelOwnership.Transfer();
            limiterOwnership.Transfer();
            return publishChannel;
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
                // With publisherConfirmationTrackingEnabled, BasicPublishAsync completes only after
                // confirm or return; IsReturn is the correlated mandatory-unroutable outcome.
                if (ex.IsReturn)
                {
                    throw new RabbitMqPublicationUnroutableException(ex);
                }

                throw new InvalidOperationException(
                    "RabbitMQ negatively acknowledged the publication.",
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
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
            }

            // Dispose the channel first so in-flight confirm tracking finishes; the channel only holds a
            // borrowed facade and does not dispose this limiter.
            await _channel.DisposeAsync().ConfigureAwait(false);
            // ThrottlingRateLimiter only releases managed state from Dispose(true); DisposeAsync uses Dispose(false).
            _outstandingConfirmationsRateLimiter.Dispose();
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
