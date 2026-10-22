using System.Globalization;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>
    /// RabbitMQ.Client publish channel with confirms enabled and library tracking disabled.
    /// </summary>
    internal sealed class RabbitMqClientAsyncConfirmPublishChannel : IRabbitMqAsyncConfirmPublishChannel
    {
        /// <summary>
        /// Owned channel opened with publisher confirms enabled and confirmation tracking disabled.
        /// <see cref="DisposeAsync"/> closes it when open, swallowing close errors, then disposes it.
        /// </summary>
        private readonly IChannel _channel;

        /// <summary><c>1</c> after the first <see cref="DisposeAsync"/>.</summary>
        private int _disposed;

        /// <summary>Initializes a new wrapper around an opened async-confirm channel.</summary>
        internal RabbitMqClientAsyncConfirmPublishChannel(IChannel channel, long generation)
        {
            ArgumentNullException.ThrowIfNull(channel);
            _channel = channel;
            Generation = generation;
        }

        /// <inheritdoc />
        public long Generation { get; }

        /// <inheritdoc />
        public bool IsOpen => _channel.IsOpen;

        /// <inheritdoc />
        public event AsyncEventHandler<BasicAckEventArgs> BasicAcksAsync
        {
            add => _channel.BasicAcksAsync += value;
            remove => _channel.BasicAcksAsync -= value;
        }

        /// <inheritdoc />
        public event AsyncEventHandler<BasicNackEventArgs> BasicNacksAsync
        {
            add => _channel.BasicNacksAsync += value;
            remove => _channel.BasicNacksAsync -= value;
        }

        /// <inheritdoc />
        public event AsyncEventHandler<BasicReturnEventArgs> BasicReturnAsync
        {
            add => _channel.BasicReturnAsync += value;
            remove => _channel.BasicReturnAsync -= value;
        }

        /// <inheritdoc />
        public ValueTask<ulong> GetNextPublishSequenceNumberAsync(CancellationToken cancellationToken) =>
            _channel.GetNextPublishSequenceNumberAsync(cancellationToken);

        /// <inheritdoc />
        public async Task PublishAsync(
            string exchange,
            string routingKey,
            string messageId,
            string appId,
            string expiration,
            ulong publishSequenceNumber,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(exchange);
            ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            ArgumentException.ThrowIfNullOrWhiteSpace(appId);
            ArgumentException.ThrowIfNullOrWhiteSpace(expiration);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (!_channel.IsOpen)
            {
                throw new InvalidOperationException("RabbitMQ publish channel is not open.");
            }

            var properties = CreateHandoffProperties(messageId, appId, expiration, publishSequenceNumber);
            await _channel.BasicPublishAsync(
                    exchange,
                    routingKey,
                    mandatory: true,
                    basicProperties: properties,
                    body: body,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

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

            await _channel.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Builds persistent AMQP properties for a handoff publish. Confirms are not waited here.
        /// </summary>
        /// <param name="messageId">AMQP <c>message-id</c>.</param>
        /// <param name="appId">AMQP <c>app-id</c>.</param>
        /// <param name="expiration">Per-message TTL in milliseconds, as an AMQP short string.</param>
        /// <param name="publishSequenceNumber">
        /// Value written to <see cref="Constants.PublishSequenceNumberHeader"/> as invariant decimal digits.
        /// A <see cref="ulong"/> table value is rejected by RabbitMQ.Client 7.2.2 wire formatting.
        /// </param>
        /// <returns>Properties passed to <c>BasicPublishAsync</c>. Mandatory routing is set by the caller, not here.</returns>
        internal static BasicProperties CreateHandoffProperties(
            string messageId,
            string appId,
            string expiration,
            ulong publishSequenceNumber)
        {
            return new BasicProperties
            {
                MessageId = messageId,
                AppId = appId,
                Expiration = expiration,
                DeliveryMode = DeliveryModes.Persistent,
                Persistent = true,
                Headers = new Dictionary<string, object?>
                {
                    // RabbitMQ.Client 7.2.2 rejects UInt64 as an AMQP table value
                    // (WireFormattingException). Invariant decimal string is supported
                    // on write and is what HandleReturn parses for values that do not
                    // fit in Int64; broker Basic.Return typically echoes longstr as byte[].
                    [Constants.PublishSequenceNumberHeader] =
                        FormatPublishSequenceNumberHeader(publishSequenceNumber),
                },
            };
        }

        /// <summary>
        /// Formats a publish sequence number for <see cref="Constants.PublishSequenceNumberHeader"/>.
        /// </summary>
        /// <remarks>
        /// Must be an AMQP-supported table value. <see cref="ulong"/> is not supported by
        /// RabbitMQ.Client 7.2.2 wire formatting; invariant decimal digits are lossless for
        /// the full <see cref="ulong"/> range and parseable on Basic.Return.
        /// </remarks>
        internal static string FormatPublishSequenceNumberHeader(ulong publishSequenceNumber) =>
            publishSequenceNumber.ToString(CultureInfo.InvariantCulture);
    }
}
