using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.StorageService.Tests.TestDoubles
{

    /// <summary>Recording RabbitMQ factory for StorageServer advertisement publisher tests.</summary>
    internal sealed class RecordingStorageServerRabbitMqConnectionFactory : IRabbitMqConnectionFactory
    {
        public RecordingStorageServerRabbitMqConnection? LastConnection { get; private set; }

        public int ConnectCount { get; private set; }

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
            var connection = new RecordingStorageServerRabbitMqConnection(
                host,
                options.Port ?? 5672,
                string.IsNullOrWhiteSpace(options.VirtualHost) ? "/" : options.VirtualHost.Trim(),
                connectionName);
            LastConnection = connection;
            return Task.FromResult<IRabbitMqConnection>(connection);
        }
    }

    internal sealed class RecordingStorageServerRabbitMqConnection : IRabbitMqConnection
    {
        public RecordingStorageServerRabbitMqConnection(string host, int port, string virtualHost, string clientProvidedName)
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

        public List<RecordingTopologyChannel> TopologyChannels { get; } = [];

        public List<RecordingManualAckChannel> ManualAckChannels { get; } = [];

        public List<RecordingPublishChannel> PublishChannels { get; } = [];

        /// <summary>Invoked before a confirmed publish is recorded. May throw or wait.</summary>
        public Func<RabbitMqConfirmedPublication, CancellationToken, Task>? BeforePublish { get; set; }

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
            var channel = new RecordingTopologyChannel();
            TopologyChannels.Add(channel);
            return Task.FromResult<IRabbitMqTopologyChannel>(channel);
        }

        public Task<IRabbitMqRpcChannel> CreateRpcChannelAsync(long generation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IRabbitMqManualAckChannel> CreateManualAckChannelAsync(long generation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = new RecordingManualAckChannel(generation);
            ManualAckChannels.Add(channel);
            return Task.FromResult<IRabbitMqManualAckChannel>(channel);
        }

        public Task<IRabbitMqPublishChannel> CreatePublishChannelAsync(long generation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = new RecordingPublishChannel(generation, this);
            PublishChannels.Add(channel);
            return Task.FromResult<IRabbitMqPublishChannel>(channel);
        }

        public Task<IRabbitMqAsyncConfirmPublishChannel> CreateAsyncConfirmPublishChannelAsync(
            long generation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class RecordingTopologyChannel : IRabbitMqTopologyChannel
    {
        public List<(string Name, string Type, bool Durable, bool AutoDelete)> Exchanges { get; } = [];

        public List<(string Name, bool Durable, bool Exclusive, bool AutoDelete)> Queues { get; } = [];

        public int DisposeCount { get; private set; }

        public Task ExchangeDeclareAsync(
            string exchange,
            string type,
            bool durable,
            bool autoDelete,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exchanges.Add((exchange, type, durable, autoDelete));
            return Task.CompletedTask;
        }

        public Task QueueDeclareAsync(
            string queue,
            bool durable,
            bool exclusive,
            bool autoDelete,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queues.Add((queue, durable, exclusive, autoDelete));
            return Task.CompletedTask;
        }

        public Task QueueBindAsync(
            string queue,
            string exchange,
            string routingKey,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<RabbitMqQueueStats> QueueDeclarePassiveAsync(string queue, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RabbitMqQueueStats(MessageCount: 0, ConsumerCount: 0));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class RecordingPublishChannel : IRabbitMqPublishChannel
    {
        private readonly RecordingStorageServerRabbitMqConnection? _connection;

        public RecordingPublishChannel(long generation, RecordingStorageServerRabbitMqConnection? connection = null)
        {
            Generation = generation;
            _connection = connection;
        }

        public long Generation { get; }

        public bool IsOpen { get; set; } = true;

        public event EventHandler<RabbitMqChannelFaultedEventArgs>? ChannelFaulted
        {
            add { }
            remove { }
        }

        public int DisposeCount { get; private set; }

        public List<RabbitMqConfirmedPublication> Publications { get; } = [];

        public Task PublishConfirmedAsync(
            string exchange,
            string routingKey,
            string messageId,
            string appId,
            string expiration,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken) =>
            PublishConfirmedAsync(
                new RabbitMqConfirmedPublication(
                    exchange,
                    routingKey,
                    messageId,
                    appId,
                    CorrelationId: null,
                    ContentType: null,
                    RequestIdHeader: null,
                    expiration,
                    Persistent: true,
                    Mandatory: true,
                    body),
                cancellationToken);

        public async Task PublishConfirmedAsync(
            RabbitMqConfirmedPublication publication,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(publication);
            cancellationToken.ThrowIfCancellationRequested();
            if (_connection?.BeforePublish is not null)
            {
                await _connection.BeforePublish(publication, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Publications.Add(publication with { Body = publication.Body.ToArray() });
        }

        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class RecordingManualAckChannel : IRabbitMqManualAckChannel
    {
        public RecordingManualAckChannel(long generation) => Generation = generation;

        public long Generation { get; }

        public bool IsOpen { get; set; } = true;

        public event EventHandler<RabbitMqChannelFaultedEventArgs>? ChannelFaulted
        {
            add { }
            remove { }
        }

        public List<(string Name, string Type, bool Durable, bool AutoDelete)> Exchanges { get; } = [];

        public List<(string Name, bool Durable, bool Exclusive, bool AutoDelete)> Queues { get; } = [];

        public List<(string Queue, string Exchange, string RoutingKey)> Bindings { get; } = [];

        public List<ulong> Acks { get; } = [];

        public Func<RabbitMqManualAckDelivery, Task>? DeliveryHandler { get; private set; }

        public string? ConsumedQueue { get; private set; }

        public int DisposeCount { get; private set; }

        public Task ExchangeDeclareAsync(
            string exchange,
            string type,
            bool durable,
            bool autoDelete,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exchanges.Add((exchange, type, durable, autoDelete));
            return Task.CompletedTask;
        }

        public Task QueueDeclareAsync(
            string queue,
            bool durable,
            bool exclusive,
            bool autoDelete,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queues.Add((queue, durable, exclusive, autoDelete));
            return Task.CompletedTask;
        }

        public Task QueueBindAsync(
            string queue,
            string exchange,
            string routingKey,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bindings.Add((queue, exchange, routingKey));
            return Task.CompletedTask;
        }

        public Task<string> BasicConsumeAsync(
            string queue,
            ushort prefetchCount,
            Func<RabbitMqManualAckDelivery, Task> onDelivery,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConsumedQueue = queue;
            DeliveryHandler = onDelivery;
            return Task.FromResult("recording-manual-ack-consumer");
        }

        public Task BasicCancelAsync(string consumerTag, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task BasicAckAsync(ulong deliveryTag, CancellationToken cancellationToken)
        {
            Acks.Add(deliveryTag);
            return Task.CompletedTask;
        }

        public Task BasicNackAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task BasicRejectAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeliverAsync(
            ulong deliveryTag,
            ReadOnlyMemory<byte> body,
            string correlationId,
            string replyTo,
            string? contentType = StorageArticleLookupWireProtocol.JsonContentType)
        {
            if (DeliveryHandler is null)
            {
                throw new InvalidOperationException("No consumer is registered.");
            }

            return DeliveryHandler(new RabbitMqManualAckDelivery(
                deliveryTag,
                body.ToArray(),
                correlationId,
                replyTo,
                contentType,
                RequestIdHeader: null,
                Redelivered: false,
                RoutingKey: string.Empty,
                Exchange: CacheFleetTopology.RequestsExchangeName,
                ConsumerTag: "recording-manual-ack-consumer",
                Generation));
        }

        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
