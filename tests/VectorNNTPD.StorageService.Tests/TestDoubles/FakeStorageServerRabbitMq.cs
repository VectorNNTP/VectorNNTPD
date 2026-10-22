using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.StorageService.Tests.TestDoubles
{

    /// <summary>Minimal offline RabbitMQ factory for StorageServer host composition tests.</summary>
    internal sealed class FakeStorageServerRabbitMqConnectionFactory : IRabbitMqConnectionFactory
    {
        /// <summary>Gets the number of successful connects.</summary>
        public int ConnectCount { get; private set; }

        /// <inheritdoc />
        public Task<IRabbitMqConnection> ConnectAsync(
            RabbitMqOptions options,
            string connectionName,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            var host = options.Hosts is { Length: > 0 } ? options.Hosts[0]! : "127.0.0.1";
            return Task.FromResult<IRabbitMqConnection>(
                new FakeStorageServerRabbitMqConnection(
                    host,
                    options.Port ?? 5672,
                    string.IsNullOrWhiteSpace(options.VirtualHost) ? "/" : options.VirtualHost.Trim(),
                    connectionName));
        }
    }

    internal sealed class FakeStorageServerRabbitMqConnection : IRabbitMqConnection
    {
        public FakeStorageServerRabbitMqConnection(string host, int port, string virtualHost, string clientProvidedName)
        {
            Host = host;
            Port = port;
            VirtualHost = virtualHost;
            ClientProvidedName = clientProvidedName;
        }

        public bool IsOpen { get; set; } = true;

        public string Host { get; }

        public int Port { get; }

        public string VirtualHost { get; }

        public string ClientProvidedName { get; }

        public event EventHandler<RabbitMqConnectionLostEventArgs>? ConnectionLost
        {
            add { }
            remove { }
        }

        public event EventHandler<RabbitMqConnectionBlockedEventArgs>? ConnectionBlocked
        {
            add { }
            remove { }
        }

        public event EventHandler? ConnectionUnblocked
        {
            add { }
            remove { }
        }

        public Task<IRabbitMqTopologyChannel> CreateTopologyChannelAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IRabbitMqTopologyChannel>(new NoOpTopologyChannel());
        }

        public Task<IRabbitMqRpcChannel> CreateRpcChannelAsync(long generation, CancellationToken cancellationToken) =>
            throw new NotSupportedException("StorageServer host tests do not open RPC channels.");

        public Task<IRabbitMqManualAckChannel> CreateManualAckChannelAsync(long generation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = new NoOpManualAckChannel(generation);
            return Task.FromResult<IRabbitMqManualAckChannel>(channel);
        }

        public Task<IRabbitMqPublishChannel> CreatePublishChannelAsync(long generation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IRabbitMqPublishChannel>(new NoOpPublishChannel(generation));
        }

        public Task<IRabbitMqAsyncConfirmPublishChannel> CreateAsyncConfirmPublishChannelAsync(
            long generation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("StorageServer host tests do not open async-confirm channels.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class NoOpTopologyChannel : IRabbitMqTopologyChannel
    {
        public Task ExchangeDeclareAsync(
            string exchange,
            string type,
            bool durable,
            bool autoDelete,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task QueueDeclareAsync(
            string queue,
            bool durable,
            bool exclusive,
            bool autoDelete,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task QueueBindAsync(
            string queue,
            string exchange,
            string routingKey,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<RabbitMqQueueStats> QueueDeclarePassiveAsync(string queue, CancellationToken cancellationToken) =>
            Task.FromResult(new RabbitMqQueueStats(MessageCount: 0, ConsumerCount: 0));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class NoOpPublishChannel(long generation) : IRabbitMqPublishChannel
    {
        public long Generation { get; } = generation;

        public bool IsOpen { get; set; } = true;

        public event EventHandler<RabbitMqChannelFaultedEventArgs>? ChannelFaulted
        {
            add { }
            remove { }
        }

        public Task PublishConfirmedAsync(
            string exchange,
            string routingKey,
            string messageId,
            string appId,
            string expiration,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PublishConfirmedAsync(
            RabbitMqConfirmedPublication publication,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class NoOpManualAckChannel(long generation) : IRabbitMqManualAckChannel
    {
        public long Generation { get; } = generation;

        public bool IsOpen { get; set; } = true;

        public event EventHandler<RabbitMqChannelFaultedEventArgs>? ChannelFaulted
        {
            add { }
            remove { }
        }

        public Task ExchangeDeclareAsync(
            string exchange,
            string type,
            bool durable,
            bool autoDelete,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task QueueDeclareAsync(
            string queue,
            bool durable,
            bool exclusive,
            bool autoDelete,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task QueueBindAsync(
            string queue,
            string exchange,
            string routingKey,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string> BasicConsumeAsync(
            string queue,
            ushort prefetchCount,
            Func<RabbitMqManualAckDelivery, Task> onDelivery,
            CancellationToken cancellationToken) =>
            Task.FromResult("noop-manual-ack-consumer");

        public Task BasicCancelAsync(string consumerTag, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task BasicAckAsync(ulong deliveryTag, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task BasicNackAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task BasicRejectAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            return ValueTask.CompletedTask;
        }
    }
}
