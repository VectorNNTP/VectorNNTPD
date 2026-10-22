using System.Runtime.ExceptionServices;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Serilog.Debugging;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>Owns one RabbitMQ.Client channel for topology, manual-ack consume, and settlement.</summary>
    /// <remarks>
    /// The channel has one consumer slot. Registration and disposal share a gate so a failed registration
    /// cannot leave a handler attached and dispose cannot close the client channel while registration is in progress.
    /// Acknowledgement and the delivery callback do not take that gate.
    /// </remarks>
    internal sealed class RabbitMqClientManualAckChannel : IRabbitMqManualAckChannel
    {
        /// <summary>
        /// Owned channel with publisher confirms disabled. <see cref="DisposeAsync"/> unsubscribes the consumer,
        /// closes the channel when this dispose owns the close, then disposes it.
        /// </summary>
        private readonly IChannel _channel;

        /// <summary>Serializes <see cref="BasicConsumeAsync"/> and <see cref="DisposeAsync"/>. Not taken by ack, nack, or the delivery callback.</summary>
        private readonly SemaphoreSlim _lifecycle = new(1, 1);

        /// <summary>Consumer registered by a completed <see cref="BasicConsumeAsync"/>. Unsubscribed on dispose.</summary>
        private AsyncEventingBasicConsumer? _consumer;

        /// <summary>Delivery handler from <see cref="BasicConsumeAsync"/>. A null handler drops the delivery without ack or nack.</summary>
        private Func<RabbitMqManualAckDelivery, Task>? _onDelivery;

        /// <summary>
        /// Optional dispose hook. When set, the disposed mark and handler detach run inside the hook before the client channel is closed.
        /// Article Work uses this so settlement and close do not overlap. Not invoked for ack or nack.
        /// </summary>
        private Func<Func<Task>, Task>? _exclusiveDispose;

        /// <summary>Threads waiting to enter <see cref="_lifecycle"/>.</summary>
        private int _lifecycleWaiters;

        /// <summary><c>1</c> after the first <see cref="DisposeAsync"/> has marked the channel disposed.</summary>
        private int _disposed;

        /// <summary><c>1</c> after an unexpected fault has been raised once for this channel.</summary>
        private int _faultRaised;

        /// <summary>
        /// <c>1</c> after this wrapper initiates <see cref="BasicCancelAsync"/> so broker
        /// <see cref="AsyncEventingBasicConsumer.UnregisteredAsync"/> is treated as expected.
        /// </summary>
        private int _applicationCancel;

        /// <summary>
        /// Initializes a new wrapper around an opened channel.
        /// </summary>
        /// <param name="channel">Opened RabbitMQ.Client channel.</param>
        /// <param name="generation">Connection generation the channel belongs to.</param>
        internal RabbitMqClientManualAckChannel(IChannel channel, long generation)
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

        /// <summary>Threads currently waiting to enter consumer registration or disposal.</summary>
        internal int LifecycleWaiters => Volatile.Read(ref _lifecycleWaiters);

        /// <summary>
        /// Runs the disposed mark and handler detach inside <paramref name="exclusiveDispose"/> before the client channel is closed.
        /// </summary>
        /// <param name="exclusiveDispose">Hook that invokes its argument while the caller holds its own gate. Stored until dispose.</param>
        /// <exception cref="ArgumentNullException"><paramref name="exclusiveDispose"/> is null.</exception>
        internal void UseExclusiveDispose(Func<Func<Task>, Task> exclusiveDispose)
        {
            ArgumentNullException.ThrowIfNull(exclusiveDispose);
            Volatile.Write(ref _exclusiveDispose, exclusiveDispose);
        }

        /// <inheritdoc />
        public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _channel.IsOpen;

        /// <inheritdoc />
        public Task ExchangeDeclareAsync(
            string exchange,
            string type,
            bool durable,
            bool autoDelete,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
            ArgumentException.ThrowIfNullOrWhiteSpace(type);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            return _channel.ExchangeDeclareAsync(
                exchange,
                type,
                durable,
                autoDelete,
                ToMutable(arguments),
                cancellationToken: cancellationToken);
        }

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
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            _ = await _channel.QueueDeclareAsync(
                queue,
                durable,
                exclusive,
                autoDelete,
                ToMutable(arguments),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task QueueBindAsync(
            string queue,
            string exchange,
            string routingKey,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queue);
            ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
            ArgumentNullException.ThrowIfNull(routingKey);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            return _channel.QueueBindAsync(
                queue,
                exchange,
                routingKey,
                ToMutable(arguments),
                cancellationToken: cancellationToken);
        }

        /// <inheritdoc />
        public async Task<string> BasicConsumeAsync(
            string queue,
            ushort prefetchCount,
            Func<RabbitMqManualAckDelivery, Task> onDelivery,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queue);
            ArgumentNullException.ThrowIfNull(onDelivery);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            await EnterLifecycleAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
                if (_consumer is not null)
                {
                    throw new InvalidOperationException("RabbitMQ manual-ack channel already has a consumer.");
                }

                if (!_channel.IsOpen)
                {
                    throw new InvalidOperationException("RabbitMQ channel is not open for consume.");
                }

                await _channel.BasicQosAsync(0, prefetchCount, global: false, cancellationToken).ConfigureAwait(false);
                var consumer = new AsyncEventingBasicConsumer(_channel);
                _onDelivery = onDelivery;
                consumer.ReceivedAsync += OnReceivedAsync;
                consumer.UnregisteredAsync += OnConsumerUnregisteredAsync;
                consumer.ShutdownAsync += OnConsumerShutdownAsync;
                try
                {
                    var consumerTag = await _channel
                        .BasicConsumeAsync(queue, autoAck: false, consumer, cancellationToken)
                        .ConfigureAwait(false);
                    _consumer = consumer;
                    return consumerTag;
                }
                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                {
                    _onDelivery = null;
                    DetachConsumerHandlers(consumer);
                    throw;
                }
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        /// <inheritdoc />
        public Task BasicCancelAsync(string consumerTag, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(consumerTag);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            Interlocked.Exchange(ref _applicationCancel, 1);
            return _channel.BasicCancelAsync(consumerTag, cancellationToken: cancellationToken);
        }

        /// <inheritdoc />
        public Task BasicAckAsync(ulong deliveryTag, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            return _channel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken).AsTask();
        }

        /// <inheritdoc />
        public Task BasicNackAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            return _channel.BasicNackAsync(deliveryTag, multiple: false, requeue, cancellationToken).AsTask();
        }

        /// <inheritdoc />
        public Task BasicRejectAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            return _channel.BasicRejectAsync(deliveryTag, requeue, cancellationToken).AsTask();
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await EnterLifecycleAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var close = false;
                Exception? hookFailure = null;

                async Task MarkDisposedAsync()
                {
                    if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    {
                        return;
                    }

                    DetachConsumer();
                    _channel.ChannelShutdownAsync -= OnChannelShutdownAsync;
                    close = true;
                }

                try
                {
                    var exclusiveDispose = Volatile.Read(ref _exclusiveDispose);
                    if (exclusiveDispose is null)
                    {
                        await MarkDisposedAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        await exclusiveDispose(MarkDisposedAsync).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                {
                    // The hook runs the mark inside the caller's gate. A throw after that mark used to
                    // skip the client close, and a later dispose saw the channel as already marked and
                    // skipped the close again. A throw before the mark still has to close the client channel.
                    hookFailure = ex;
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        await MarkDisposedAsync().ConfigureAwait(false);
                    }
                }

                if (close)
                {
                    await CloseChannelAsync().ConfigureAwait(false);
                }

                if (hookFailure is not null)
                {
                    ExceptionDispatchInfo.Capture(hookFailure).Throw();
                }
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        /// <summary>Waits for <see cref="_lifecycle"/> and reports the waiter to <see cref="LifecycleWaiters"/>.</summary>
        /// <param name="cancellationToken">Cancels the wait. A cancelled wait does not hold the gate.</param>
        private async Task EnterLifecycleAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _lifecycleWaiters);
            try
            {
                await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _lifecycleWaiters);
            }
        }

        /// <summary>Unsubscribes the current consumer and drops the delivery handler.</summary>
        private void DetachConsumer()
        {
            if (_consumer is not null)
            {
                DetachConsumerHandlers(_consumer);
                _consumer = null;
            }

            _onDelivery = null;
        }

        /// <summary>Removes delivery and fault handlers from <paramref name="consumer"/>.</summary>
        /// <param name="consumer">Consumer previously attached by <see cref="BasicConsumeAsync"/>.</param>
        private void DetachConsumerHandlers(AsyncEventingBasicConsumer consumer)
        {
            consumer.ReceivedAsync -= OnReceivedAsync;
            consumer.UnregisteredAsync -= OnConsumerUnregisteredAsync;
            consumer.ShutdownAsync -= OnConsumerShutdownAsync;
        }

        /// <summary>Raises <see cref="ChannelFaulted"/> once for an unexpected channel shutdown.</summary>
        /// <param name="sender">Unused.</param>
        /// <param name="eventArgs">Client shutdown details.</param>
        private Task OnChannelShutdownAsync(object sender, ShutdownEventArgs eventArgs)
        {
            TryRaiseChannelFaulted(
                RabbitMqChannelFaultKind.ChannelShutdown,
                FormatShutdownReason(eventArgs));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Raises <see cref="ChannelFaulted"/> when the broker cancels the consumer and this wrapper did not issue cancel.
        /// </summary>
        /// <param name="sender">Unused.</param>
        /// <param name="eventArgs">Cancelled consumer tags from the client.</param>
        private Task OnConsumerUnregisteredAsync(object sender, ConsumerEventArgs eventArgs)
        {
            if (Volatile.Read(ref _applicationCancel) == 1)
            {
                return Task.CompletedTask;
            }

            var tags = eventArgs.ConsumerTags is { Length: > 0 }
                ? string.Join(',', eventArgs.ConsumerTags)
                : "(none)";
            TryRaiseChannelFaulted(
                RabbitMqChannelFaultKind.ConsumerCancelled,
                $"Consumer unregistered by broker (tags={tags}).");
            return Task.CompletedTask;
        }

        /// <summary>Raises <see cref="ChannelFaulted"/> once for a consumer-scoped shutdown.</summary>
        /// <param name="sender">Unused.</param>
        /// <param name="eventArgs">Client shutdown details.</param>
        private Task OnConsumerShutdownAsync(object sender, ShutdownEventArgs eventArgs)
        {
            TryRaiseChannelFaulted(
                RabbitMqChannelFaultKind.ChannelShutdown,
                FormatShutdownReason(eventArgs));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Raises <see cref="ChannelFaulted"/> at most once when disposal has not marked the channel closed.
        /// </summary>
        /// <param name="kind">Fault classification.</param>
        /// <param name="reason">Broker or client reason text.</param>
        private void TryRaiseChannelFaulted(RabbitMqChannelFaultKind kind, string reason)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            if (Interlocked.Exchange(ref _faultRaised, 1) == 1)
            {
                return;
            }

            ChannelFaulted?.Invoke(this, new RabbitMqChannelFaultedEventArgs(kind, reason));
        }

        /// <summary>Formats a client shutdown reason without credentials or payload bytes.</summary>
        /// <param name="eventArgs">Client shutdown details.</param>
        /// <returns>Stable reason text for logs and recovery.</returns>
        private static string FormatShutdownReason(ShutdownEventArgs eventArgs)
        {
            var replyText = string.IsNullOrWhiteSpace(eventArgs.ReplyText)
                ? "(none)"
                : eventArgs.ReplyText;
            return $"Channel shutdown initiator={eventArgs.Initiator} code={eventArgs.ReplyCode} text={replyText}";
        }

        /// <summary>
        /// Closes an open client channel, then disposes it.
        /// Already-closed, interrupted, I/O, disposed, and timeout failures are written to the Serilog self-log.
        /// Any other close failure is propagated after dispose.
        /// </summary>
        private async ValueTask CloseChannelAsync()
        {
            try
            {
                if (_channel.IsOpen)
                {
                    await _channel.CloseAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is AlreadyClosedException
                or OperationInterruptedException
                or IOException
                or ObjectDisposedException
                or TimeoutException)
            {
                SelfLog.WriteLine("RabbitMQ channel close failed and disposal will continue. " + ex);
            }
            finally
            {
                await _channel.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Builds a <see cref="RabbitMqManualAckDelivery"/> and awaits <see cref="_onDelivery"/>. Does not ack or nack.
        /// </summary>
        /// <param name="sender">Unused.</param>
        /// <param name="eventArgs">Client delivery. The body is passed through without copying.</param>
        /// <remarks>
        /// When no handler is installed the delivery is ignored and left unsettled. Handler exceptions propagate to the client consumer.
        /// </remarks>
        private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs eventArgs)
        {
            var handler = _onDelivery;
            if (handler is null)
            {
                return;
            }

            var delivery = new RabbitMqManualAckDelivery(
                eventArgs.DeliveryTag,
                eventArgs.Body,
                eventArgs.BasicProperties.CorrelationId,
                eventArgs.BasicProperties.ReplyTo,
                eventArgs.BasicProperties.ContentType,
                ReadRequestId(eventArgs.BasicProperties.Headers),
                eventArgs.Redelivered,
                eventArgs.RoutingKey,
                eventArgs.Exchange,
                eventArgs.ConsumerTag,
                Generation,
                ReadDeliveryCount(eventArgs.BasicProperties.Headers));
            await handler(delivery).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads the <c>RequestId</c> header as a string, UTF-8 <see cref="byte"/> array, or <see cref="ReadOnlyMemory{T}"/> of bytes.
        /// </summary>
        /// <param name="headers">AMQP headers. <see langword="null"/>, a missing key, or any other value type yields <see langword="null"/>.</param>
        /// <returns>The request id text, or <see langword="null"/> when it is absent or not one of those encodings.</returns>
        private static string? ReadRequestId(IDictionary<string, object?>? headers)
        {
            if (headers is null
                || !headers.TryGetValue("RequestId", out var value)
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

        /// <summary>
        /// Reads the quorum-queue <c>x-delivery-count</c> header as a non-negative integer.
        /// </summary>
        /// <param name="headers">AMQP headers. Missing or unsupported encodings yield <see langword="null"/>.</param>
        /// <returns>The delivery count, or <see langword="null"/> when absent.</returns>
        internal static int? ReadDeliveryCount(IDictionary<string, object?>? headers)
        {
            if (headers is null
                || !headers.TryGetValue("x-delivery-count", out var value)
                || value is null)
            {
                return null;
            }

            return value switch
            {
                int i when i >= 0 => i,
                long l when l >= 0 && l <= int.MaxValue => (int)l,
                short s when s >= 0 => s,
                byte b => b,
                uint ui when ui <= int.MaxValue => (int)ui,
                ulong ul when ul <= int.MaxValue => (int)ul,
                _ => null,
            };
        }

        /// <summary>Copies <paramref name="arguments"/> into a new ordinal dictionary for RabbitMQ.Client.</summary>
        /// <param name="arguments">Declare or bind arguments. Not mutated.</param>
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
