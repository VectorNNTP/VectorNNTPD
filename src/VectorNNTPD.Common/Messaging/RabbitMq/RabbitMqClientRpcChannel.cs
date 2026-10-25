using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>Adapts a RabbitMQ.Client channel to RPC publish/consume.</summary>
    internal sealed class RabbitMqClientRpcChannel : IRabbitMqRpcChannel
    {
        /// <summary>AMQP header name carrying the logical request UUID.</summary>
        internal const string RequestIdPropertyName = "RequestId";

        /// <summary>
        /// Owned channel. <see cref="DisposeAsync"/> disposes it once and does not call <c>CloseAsync</c> first.
        /// </summary>
        private readonly IChannel _channel;

        /// <summary><c>1</c> after the first <see cref="DisposeAsync"/>.</summary>
        private int _disposed;

        /// <summary>Initializes a new wrapper around an opened broker channel.</summary>
        internal RabbitMqClientRpcChannel(IChannel channel, long generation)
        {
            ArgumentNullException.ThrowIfNull(channel);
            _channel = channel;
            Generation = generation;
        }

        /// <inheritdoc />
        public long Generation { get; }

        /// <inheritdoc />
        public async Task QueueDeclareAsync(
            string queue,
            bool durable,
            bool exclusive,
            bool autoDelete,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queue);
            _ = await _channel.QueueDeclareAsync(
                queue,
                durable,
                exclusive,
                autoDelete,
                ToMutable(arguments),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task PublishAsync(
            string exchange,
            string routingKey,
            string correlationId,
            string requestId,
            string replyTo,
            string contentType,
            string expiration,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
            ArgumentNullException.ThrowIfNull(routingKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
            ArgumentException.ThrowIfNullOrWhiteSpace(replyTo);
            ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
            ArgumentNullException.ThrowIfNull(expiration);

            var properties = new BasicProperties
            {
                CorrelationId = correlationId,
                ReplyTo = replyTo,
                ContentType = contentType,
                Headers = CreateRequestIdHeaders(requestId),
            };
            if (!string.IsNullOrWhiteSpace(expiration))
            {
                properties.Expiration = expiration;
            }

            await _channel.BasicPublishAsync(
                exchange,
                routingKey,
                mandatory: false,
                properties,
                body,
                cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<string> ConsumeAsync(
            string queue,
            Func<RabbitMqRpcDelivery, Task> onDelivery,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queue);
            ArgumentNullException.ThrowIfNull(onDelivery);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += (_, eventArgs) =>
            {
                var body = eventArgs.Body.ToArray();
                var delivery = new RabbitMqRpcDelivery(
                    eventArgs.BasicProperties.CorrelationId,
                    ReadRequestId(eventArgs.BasicProperties.Headers),
                    eventArgs.BasicProperties.ContentType,
                    eventArgs.BasicProperties.Expiration,
                    body,
                    Generation);
                return onDelivery(delivery);
            };

            return await _channel.BasicConsumeAsync(
                queue,
                autoAck: true,
                consumer,
                cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await _channel.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>Creates the AMQP header table that carries <see cref="RequestIdPropertyName"/>.</summary>
        internal static Dictionary<string, object?> CreateRequestIdHeaders(string requestId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [RequestIdPropertyName] = requestId,
            };
        }

        /// <summary>Reads the logical request UUID from AMQP headers, if present.</summary>
        internal static string? ReadRequestId(IDictionary<string, object?>? headers)
        {
            if (headers is null
                || !headers.TryGetValue(RequestIdPropertyName, out var value)
                || value is null)
            {
                return null;
            }

            return value switch
            {
                string text => text,
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                ReadOnlyMemory<byte> memory => Encoding.UTF8.GetString(memory.Span),
                _ => null,
            };
        }

        /// <summary>Copies <paramref name="arguments"/> into a new ordinal dictionary for RabbitMQ.Client.</summary>
        /// <param name="arguments">Queue-declare arguments. Not mutated.</param>
        /// <returns><see langword="null"/> when <paramref name="arguments"/> is <see langword="null"/>; otherwise a new dictionary.</returns>
        private static Dictionary<string, object?>? ToMutable(IReadOnlyDictionary<string, object?>? arguments)
        {
            if (arguments is null)
            {
                return null;
            }

            return new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
        }
    }
}
