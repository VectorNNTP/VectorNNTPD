using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.RabbitMq;
using VectorNNTPD.ReaderService.RabbitMq.ArticleWork;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.ReaderService.Tests.TestDoubles;

/// <summary>In-memory RabbitMQ connection factory for offline tests.</summary>
internal sealed class FakeRabbitMqConnectionFactory : IRabbitMqConnectionFactory
{
    private int _connectCount;
    private int _attemptCount;
    private readonly SemaphoreSlim _attemptPulse = new(0, int.MaxValue);

    /// <summary>Gets the number of successful connects.</summary>
    public int ConnectCount => Volatile.Read(ref _connectCount);

    /// <summary>Gets the number of connect attempts, including failures.</summary>
    public int AttemptCount => Volatile.Read(ref _attemptCount);

    /// <summary>Gets the most recently created connection.</summary>
    public FakeRabbitMqConnection? LastConnection { get; private set; }

    /// <summary>Gets every connection created by this factory.</summary>
    public List<FakeRabbitMqConnection> Connections { get; } = [];

    /// <summary>When set, every connect attempt throws this exception.</summary>
    public Exception? ConnectException { get; set; }

    /// <summary>
    /// Remaining connect attempts that throw <see cref="ConnectException"/> or a default broker failure.
    /// After the budget is exhausted, connects succeed unless <see cref="ConnectException"/> is set.
    /// </summary>
    public int RemainingConnectFailures { get; set; }

    /// <summary>Signaled when a connect attempt begins, before <see cref="BlockConnect"/>.</summary>
    public TaskCompletionSource? ConnectStarted { get; set; }

    /// <summary>When set, connect waits on this source before completing.</summary>
    public TaskCompletionSource? BlockConnect { get; set; }

    /// <summary>Signaled after each successful connect with the new <see cref="ConnectCount"/>.</summary>
    public TaskCompletionSource<int>? Connected { get; set; }

    /// <summary>When <see langword="true"/>, created connections report <c>IsOpen == false</c>.</summary>
    public bool ReturnUnusableConnection { get; set; }

    /// <summary>Copied onto each created connection as <see cref="FakeRabbitMqConnection.QueueDeclareException"/>.</summary>
    public Exception? QueueDeclareException { get; set; }

    /// <summary>
    /// Copied onto each created connection as <see cref="FakeRabbitMqConnection.PassiveConsumerCounts"/>.
    /// </summary>
    public Dictionary<string, uint> PassiveConsumerCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>Waits until at least <paramref name="count"/> connect attempts have started.</summary>
    public async Task WaitForAttemptsAsync(int count, CancellationToken cancellationToken)
    {
        while (AttemptCount < count)
        {
            await _attemptPulse.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IRabbitMqConnection> ConnectAsync(
        RabbitMqOptions options,
        string connectionName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

        Interlocked.Increment(ref _attemptCount);
        _attemptPulse.Release();
        ConnectStarted?.TrySetResult();
        if (BlockConnect is not null)
        {
            await BlockConnect.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (RemainingConnectFailures > 0)
        {
            RemainingConnectFailures--;
            throw ConnectException ?? new InvalidOperationException("broker down");
        }

        if (ConnectException is not null)
        {
            throw ConnectException;
        }

        var count = Interlocked.Increment(ref _connectCount);
        var hosts = options.Hosts ?? [];
        var connection = new FakeRabbitMqConnection(
            hosts.Length > 0 ? hosts[0] : "127.0.0.1",
            options.Port ?? 5672,
            string.IsNullOrWhiteSpace(options.VirtualHost) ? "/" : options.VirtualHost.Trim(),
            connectionName)
        {
            IsOpen = !ReturnUnusableConnection,
            QueueDeclareException = QueueDeclareException,
        };
        foreach (var pair in PassiveConsumerCounts)
        {
            connection.PassiveConsumerCounts[pair.Key] = pair.Value;
        }

        LastConnection = connection;
        Connections.Add(connection);
        Connected?.TrySetResult(count);
        return connection;
    }
}

/// <summary>In-memory RabbitMQ connection for lifecycle tests.</summary>
internal sealed class FakeRabbitMqConnection : IRabbitMqConnection
{
    /// <summary>Initializes a new fake connection.</summary>
    public FakeRabbitMqConnection(string host, int port, string virtualHost, string clientProvidedName)
    {
        Host = host;
        Port = port;
        VirtualHost = virtualHost;
        ClientProvidedName = clientProvidedName;
    }

    /// <inheritdoc />
    public bool IsOpen { get; set; } = true;

    /// <inheritdoc />
    public event EventHandler<RabbitMqChannelFaultedEventArgs>? ChannelFaulted;

    /// <inheritdoc />
    public string Host { get; }

    /// <inheritdoc />
    public int Port { get; }

    /// <inheritdoc />
    public string VirtualHost { get; }

    /// <inheritdoc />
    public string ClientProvidedName { get; }

    /// <inheritdoc />
    public event EventHandler<RabbitMqConnectionBlockedEventArgs>? ConnectionBlocked;

    /// <inheritdoc />
    public event EventHandler? ConnectionUnblocked;

    /// <summary>Gets how many times the connection was disposed.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>Signaled when <see cref="DisposeAsync"/> begins, before <see cref="BlockDispose"/>.</summary>
    public TaskCompletionSource? DisposeStarted { get; set; }

    /// <summary>When set, <see cref="DisposeAsync"/> waits on this source before completing.</summary>
    public TaskCompletionSource? BlockDispose { get; set; }

    /// <inheritdoc />
    public event EventHandler<RabbitMqConnectionLostEventArgs>? ConnectionLost;

    /// <summary>Gets every topology channel created on this connection.</summary>
    public List<FakeRabbitMqTopologyChannel> TopologyChannels { get; } = [];

    /// <summary>Gets every RPC channel created on this connection.</summary>
    public List<FakeRabbitMqRpcChannel> RpcChannels { get; } = [];

    /// <summary>Gets every confirm-enabled publish channel created on this connection.</summary>
    public List<FakeRabbitMqPublishChannel> PublishChannels { get; } = [];

    /// <summary>Gets every async-confirm publish channel created on this connection.</summary>
    public List<FakeRabbitMqAsyncConfirmPublishChannel> AsyncConfirmPublishChannels { get; } = [];

    /// <summary>Gets every manual-ack channel created on this connection.</summary>
    public List<FakeRabbitMqManualAckChannel> ManualAckChannels { get; } = [];

    /// <summary>When set, <see cref="CreateRpcChannelAsync"/> throws this exception.</summary>
    public Exception? CreateRpcChannelException { get; set; }

    /// <summary>When set, <see cref="CreateManualAckChannelAsync"/> throws this exception.</summary>
    public Exception? CreateManualAckChannelException { get; set; }

    /// <summary>When set, <see cref="CreatePublishChannelAsync"/> throws this exception.</summary>
    public Exception? CreatePublishChannelException { get; set; }

    /// <summary>When set, <see cref="CreateAsyncConfirmPublishChannelAsync"/> throws this exception.</summary>
    public Exception? CreateAsyncConfirmPublishChannelException { get; set; }

    /// <summary>Optional configurator invoked for every newly created publish channel.</summary>
    public Action<FakeRabbitMqPublishChannel>? ConfigurePublishChannel { get; set; }

    /// <summary>Optional configurator invoked for every newly created async-confirm channel.</summary>
    public Action<FakeRabbitMqAsyncConfirmPublishChannel>? ConfigureAsyncConfirmPublishChannel { get; set; }

    /// <summary>When set, <see cref="CreateTopologyChannelAsync"/> throws this exception.</summary>
    public Exception? CreateTopologyChannelException { get; set; }

    /// <summary>When set, the next created topology channel throws this exception from queue declare.</summary>
    public Exception? QueueDeclareException { get; set; }

    /// <summary>When set, the next created topology channel throws this exception from exchange declare.</summary>
    public Exception? ExchangeDeclareException { get; set; }

    /// <summary>When set, the next created topology channel throws this exception from queue bind.</summary>
    public Exception? QueueBindException { get; set; }

    /// <summary>
    /// Passive-declare consumer counts copied onto each new topology channel (ArticleWork tests).
    /// </summary>
    public Dictionary<string, uint> PassiveConsumerCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, <see cref="IRabbitMqTopologyChannel.QueueDeclarePassiveAsync"/> throws on new topology channels.</summary>
    public Exception? QueueDeclarePassiveException { get; set; }

    /// <summary>
    /// Queue names for which passive declare throws <c>NOT_FOUND</c> on each new topology channel
    /// (simulates missing provider queues under BackFiller-owned topology).
    /// </summary>
    public HashSet<string> PassiveDeclareNotFoundQueues { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<IRabbitMqTopologyChannel> CreateTopologyChannelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CreateTopologyChannelException is not null)
        {
            throw CreateTopologyChannelException;
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ connection is not open for topology declaration.");
        }

        var channel = new FakeRabbitMqTopologyChannel
        {
            ExchangeDeclareException = ExchangeDeclareException,
            QueueDeclareException = QueueDeclareException,
            QueueBindException = QueueBindException,
            QueueDeclarePassiveException = QueueDeclarePassiveException,
        };
        foreach (var pair in PassiveConsumerCounts)
        {
            channel.PassiveConsumerCounts[pair.Key] = pair.Value;
        }

        foreach (var queue in PassiveDeclareNotFoundQueues)
        {
            channel.PassiveDeclareNotFoundQueues.Add(queue);
        }

        TopologyChannels.Add(channel);
        return Task.FromResult<IRabbitMqTopologyChannel>(channel);
    }

    /// <inheritdoc />
    public Task<IRabbitMqRpcChannel> CreateRpcChannelAsync(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CreateRpcChannelException is not null)
        {
            throw CreateRpcChannelException;
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ connection is not open for article-work RPC.");
        }

        var channel = new FakeRabbitMqRpcChannel(generation);
        RpcChannels.Add(channel);
        return Task.FromResult<IRabbitMqRpcChannel>(channel);
    }

    /// <inheritdoc />
    public Task<IRabbitMqManualAckChannel> CreateManualAckChannelAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CreateManualAckChannelException is not null)
        {
            throw CreateManualAckChannelException;
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ connection is not open for channel creation.");
        }

        var channel = new FakeRabbitMqManualAckChannel(generation);
        ManualAckChannels.Add(channel);
        return Task.FromResult<IRabbitMqManualAckChannel>(channel);
    }

    /// <inheritdoc />
    public Task<IRabbitMqPublishChannel> CreatePublishChannelAsync(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CreatePublishChannelException is not null)
        {
            throw CreatePublishChannelException;
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ connection is not open for publication.");
        }

        var channel = new FakeRabbitMqPublishChannel(generation);
        ConfigurePublishChannel?.Invoke(channel);
        PublishChannels.Add(channel);
        return Task.FromResult<IRabbitMqPublishChannel>(channel);
    }

    /// <inheritdoc />
    public Task<IRabbitMqAsyncConfirmPublishChannel> CreateAsyncConfirmPublishChannelAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CreateAsyncConfirmPublishChannelException is not null)
        {
            throw CreateAsyncConfirmPublishChannelException;
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ connection is not open for publication.");
        }

        var channel = new FakeRabbitMqAsyncConfirmPublishChannel(generation);
        ConfigureAsyncConfirmPublishChannel?.Invoke(channel);
        AsyncConfirmPublishChannels.Add(channel);
        return Task.FromResult<IRabbitMqAsyncConfirmPublishChannel>(channel);
    }

    /// <summary>Raises <see cref="ConnectionLost"/> as a peer-initiated disconnect.</summary>
    public void SimulateLost(ushort replyCode = 320, string replyText = "CONNECTION FORCED")
    {
        IsOpen = false;
        ConnectionLost?.Invoke(this, new RabbitMqConnectionLostEventArgs(replyCode, replyText, "Peer"));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        IsOpen = false;
        DisposeStarted?.TrySetResult();
        if (BlockDispose is not null)
        {
            await BlockDispose.Task.ConfigureAwait(false);
        }

        DisposeCount++;
    }

    /// <summary>Gets every exchange declared on any topology channel of this connection.</summary>
    public IReadOnlyList<FakeRabbitMqExchangeDeclaration> ExchangeDeclarations =>
        TopologyChannels.SelectMany(static channel => channel.Exchanges).ToList();

    /// <summary>Gets every queue declared on any topology channel of this connection.</summary>
    public IReadOnlyList<FakeRabbitMqQueueDeclaration> QueueDeclarations =>
        TopologyChannels.SelectMany(static channel => channel.Queues).ToList();

    /// <summary>Gets every binding declared on any topology channel of this connection.</summary>
    public IReadOnlyList<FakeRabbitMqBindingDeclaration> BindingDeclarations =>
        TopologyChannels.SelectMany(static channel => channel.Bindings).ToList();
}

/// <summary>In-memory declare-only RabbitMQ channel for topology tests.</summary>
internal sealed class FakeRabbitMqTopologyChannel : IRabbitMqTopologyChannel
{
    /// <summary>Gets recorded exchange declarations in call order.</summary>
    public List<FakeRabbitMqExchangeDeclaration> Exchanges { get; } = [];

    /// <summary>Gets recorded queue declarations in call order.</summary>
    public List<FakeRabbitMqQueueDeclaration> Queues { get; } = [];

    /// <summary>Gets recorded queue bindings in call order.</summary>
    public List<FakeRabbitMqBindingDeclaration> Bindings { get; } = [];

    /// <summary>Gets how many times the channel was disposed.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>When set, <see cref="ExchangeDeclareAsync"/> throws this exception.</summary>
    public Exception? ExchangeDeclareException { get; set; }

    /// <summary>When set, <see cref="QueueDeclareAsync"/> throws this exception.</summary>
    public Exception? QueueDeclareException { get; set; }

    /// <summary>When set, <see cref="QueueBindAsync"/> throws this exception.</summary>
    public Exception? QueueBindException { get; set; }

    /// <summary>Passive-declare consumer counts keyed by queue name (tests).</summary>
    public Dictionary<string, uint> PassiveConsumerCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, <see cref="QueueDeclarePassiveAsync"/> throws this exception.</summary>
    public Exception? QueueDeclarePassiveException { get; set; }

    /// <summary>Queues that throw a broker-style NOT_FOUND on passive declare.</summary>
    public HashSet<string> PassiveDeclareNotFoundQueues { get; } = new(StringComparer.Ordinal);

    /// <summary>Number of passive queue declares performed on this channel.</summary>
    public int PassiveDeclareCount { get; private set; }

    /// <summary>Passive-declare call targets in order.</summary>
    public List<string> PassiveDeclareQueues { get; } = [];

    /// <inheritdoc />
    public Task ExchangeDeclareAsync(
        string exchange,
        string type,
        bool durable,
        bool autoDelete,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ExchangeDeclareException is not null)
        {
            throw ExchangeDeclareException;
        }

        Exchanges.Add(new FakeRabbitMqExchangeDeclaration(
            exchange,
            type,
            durable,
            autoDelete,
            CopyArguments(arguments)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task QueueDeclareAsync(
        string queue,
        bool durable,
        bool exclusive,
        bool autoDelete,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (QueueDeclareException is not null)
        {
            throw QueueDeclareException;
        }

        Queues.Add(new FakeRabbitMqQueueDeclaration(
            queue,
            durable,
            exclusive,
            autoDelete,
            CopyArguments(arguments)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<RabbitMqQueueStats> QueueDeclarePassiveAsync(string queue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PassiveDeclareCount++;
        PassiveDeclareQueues.Add(queue);
        if (QueueDeclarePassiveException is not null)
        {
            throw QueueDeclarePassiveException;
        }

        if (PassiveDeclareNotFoundQueues.Contains(queue))
        {
            throw new InvalidOperationException($"NOT_FOUND - no queue '{queue}' in vhost '/'");
        }

        PassiveConsumerCounts.TryGetValue(queue, out var consumers);
        return Task.FromResult(new RabbitMqQueueStats(MessageCount: 0, ConsumerCount: consumers));
    }

    /// <inheritdoc />
    public Task QueueBindAsync(
        string queue,
        string exchange,
        string routingKey,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (QueueBindException is not null)
        {
            throw QueueBindException;
        }

        Bindings.Add(new FakeRabbitMqBindingDeclaration(
            queue,
            exchange,
            routingKey,
            CopyArguments(arguments)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }

    private static IReadOnlyDictionary<string, object?>? CopyArguments(
        IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        return new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
    }
}

/// <summary>In-memory RabbitMQ channel for article-work RPC tests.</summary>
internal sealed class FakeRabbitMqRpcChannel : IRabbitMqRpcChannel
{
    /// <summary>Initializes a new fake RPC channel.</summary>
    public FakeRabbitMqRpcChannel(long generation)
    {
        Generation = generation;
    }

    /// <inheritdoc />
    public long Generation { get; }

    /// <summary>Gets recorded queue declarations.</summary>
    public List<FakeRabbitMqQueueDeclaration> Queues { get; } = [];

    /// <summary>Gets recorded publications.</summary>
    public List<FakeRabbitMqRpcPublication> Publications { get; } = [];

    /// <summary>Gets how many times the channel was disposed.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>When set, <see cref="PublishAsync"/> throws this exception.</summary>
    public Exception? PublishException { get; set; }

    /// <summary>Gets the consume handler when a consumer has started.</summary>
    public Func<RabbitMqRpcDelivery, Task>? DeliveryHandler { get; private set; }

    /// <summary>Gets the most recent delivery passed to the consumer, when any.</summary>
    public RabbitMqRpcDelivery? LastDelivery { get; private set; }

    /// <summary>Gets the consumed queue name.</summary>
    public string? ConsumedQueue { get; private set; }

    /// <inheritdoc />
    public Task QueueDeclareAsync(
        string queue,
        bool durable,
        bool exclusive,
        bool autoDelete,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Queues.Add(new FakeRabbitMqQueueDeclaration(
            queue,
            durable,
            exclusive,
            autoDelete,
            arguments is null ? null : new Dictionary<string, object?>(arguments, StringComparer.Ordinal)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PublishAsync(
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
        cancellationToken.ThrowIfCancellationRequested();
        if (DisposeCount > 0)
        {
            throw new ObjectDisposedException(nameof(FakeRabbitMqRpcChannel));
        }

        if (PublishException is not null)
        {
            throw PublishException;
        }

        Publications.Add(new FakeRabbitMqRpcPublication(
            exchange,
            routingKey,
            correlationId,
            requestId,
            replyTo,
            contentType,
            expiration,
            body.ToArray()));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string> ConsumeAsync(
        string queue,
        Func<RabbitMqRpcDelivery, Task> onDelivery,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConsumedQueue = queue;
        DeliveryHandler = onDelivery;
        return Task.FromResult("fake-consumer");
    }

    /// <summary>Delivers one message to the registered consumer.</summary>
    public Task DeliverAsync(
        string? correlationId,
        ReadOnlyMemory<byte> body,
        string? contentType = "application/json",
        string? requestId = null,
        string? expiration = ArticleWorkRpcAmqp.ExpirationMilliseconds)
    {
        if (DeliveryHandler is null)
        {
            throw new InvalidOperationException("No RPC consumer is registered.");
        }

        var delivery = new RabbitMqRpcDelivery(
            correlationId,
            requestId,
            contentType,
            expiration,
            body.ToArray(),
            Generation);
        LastDelivery = delivery;
        return DeliveryHandler(delivery);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>In-memory confirm-enabled publish channel for OverviewDB handoff tests.</summary>
internal sealed class FakeRabbitMqPublishChannel : IRabbitMqPublishChannel
{
    /// <summary>Initializes a new fake publish channel.</summary>
    public FakeRabbitMqPublishChannel(long generation)
    {
        Generation = generation;
    }

    /// <inheritdoc />
    public long Generation { get; }

    /// <inheritdoc />
    public bool IsOpen { get; set; } = true;

    /// <inheritdoc />
    public event EventHandler<RabbitMqChannelFaultedEventArgs>? ChannelFaulted;

    /// <summary>Gets recorded publications.</summary>
    public List<FakeRabbitMqConfirmedPublication> Publications { get; } = [];

    /// <summary>Gets how many times the channel was disposed.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>When set, <see cref="PublishConfirmedAsync"/> throws this exception.</summary>
    public Exception? PublishException { get; set; }

    /// <summary>
    /// Remaining publications that throw <see cref="PublishException"/> or a default nack.
    /// After the budget is exhausted, publications succeed unless <see cref="PublishException"/> stays set
    /// and <see cref="RemainingPublishFailures"/> is zero with a sticky exception.
    /// </summary>
    public int RemainingPublishFailures { get; set; }

    /// <summary>
    /// When <see langword="true"/>, waits until <c>cancellationToken</c> is cancelled
    /// instead of completing a confirm.
    /// </summary>
    public bool HoldUntilCancelled { get; set; }

    /// <summary>
    /// When <see langword="true"/>, marks the channel closed at the start of the next
    /// publish so the not-open failure path is exercised after the channel was obtained.
    /// </summary>
    public bool CloseBeforePublish { get; set; }

    /// <summary>
    /// Optional await injected after open checks and before recording a successful confirm.
    /// Used by concurrency tests to observe in-flight publishes.
    /// </summary>
    public Func<Task>? BeforeConfirmAsync { get; set; }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public async Task PublishConfirmedAsync(
        RabbitMqConfirmedPublication publication,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        cancellationToken.ThrowIfCancellationRequested();
        if (DisposeCount > 0)
        {
            throw new ObjectDisposedException(nameof(FakeRabbitMqPublishChannel));
        }

        if (CloseBeforePublish)
        {
            IsOpen = false;
        }

        if (HoldUntilCancelled)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ publish channel is not open.");
        }

        if (BeforeConfirmAsync is not null)
        {
            await BeforeConfirmAsync().ConfigureAwait(false);
        }

        if (RemainingPublishFailures > 0)
        {
            RemainingPublishFailures--;
            throw PublishException ?? new InvalidOperationException("RabbitMQ negatively acknowledged the publication.");
        }

        if (PublishException is not null)
        {
            throw PublishException;
        }

        Publications.Add(new FakeRabbitMqConfirmedPublication(
            publication.Exchange,
            publication.RoutingKey,
            publication.MessageId,
            publication.AppId,
            publication.CorrelationId,
            publication.ContentType,
            publication.RequestIdHeader,
            publication.ExpirationMilliseconds,
            publication.Persistent,
            publication.Mandatory,
            publication.Body.ToArray()));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// In-memory async-confirm publish channel. <see cref="PublishAsync"/> returns after the write;
/// auto-acks by default so outstanding windows drain without a real broker.
/// </summary>
internal sealed class FakeRabbitMqAsyncConfirmPublishChannel : IRabbitMqAsyncConfirmPublishChannel
{
    private long _nextSequence = 1;
    private AsyncEventHandler<BasicAckEventArgs>? _acks;
    private AsyncEventHandler<BasicNackEventArgs>? _nacks;
    private AsyncEventHandler<BasicReturnEventArgs>? _returns;

    public FakeRabbitMqAsyncConfirmPublishChannel(long generation)
    {
        Generation = generation;
    }

    /// <inheritdoc />
    public long Generation { get; }

    /// <inheritdoc />
    public bool IsOpen { get; set; } = true;

    /// <inheritdoc />
    public event EventHandler<RabbitMqChannelFaultedEventArgs>? ChannelFaulted;

    /// <summary>Gets recorded publications.</summary>
    public List<FakeRabbitMqConfirmedPublication> Publications { get; } = [];

    /// <summary>Gets how many times the channel was disposed.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>When true (default), raises BasicAck after each successful publish.</summary>
    public bool AutoAck { get; set; } = true;

    /// <summary>Delay before auto-ack (simulates quorum confirm latency).</summary>
    public TimeSpan AutoAckDelay { get; set; }

    /// <summary>When set, <see cref="PublishAsync"/> throws this exception.</summary>
    public Exception? PublishException { get; set; }

    /// <summary>Remaining publications that throw before succeeding.</summary>
    public int RemainingPublishFailures { get; set; }

    /// <summary>Optional await before completing a publish write.</summary>
    public Func<Task>? BeforePublishAsync { get; set; }

    /// <inheritdoc />
    public event AsyncEventHandler<BasicAckEventArgs> BasicAcksAsync
    {
        add => _acks += value;
        remove => _acks -= value;
    }

    /// <inheritdoc />
    public event AsyncEventHandler<BasicNackEventArgs> BasicNacksAsync
    {
        add => _nacks += value;
        remove => _nacks -= value;
    }

    /// <inheritdoc />
    public event AsyncEventHandler<BasicReturnEventArgs> BasicReturnAsync
    {
        add => _returns += value;
        remove => _returns -= value;
    }

    /// <inheritdoc />
    public ValueTask<ulong> GetNextPublishSequenceNumberAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var seq = (ulong)Interlocked.Increment(ref _nextSequence) - 1;
        return ValueTask.FromResult(seq);
    }

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
        cancellationToken.ThrowIfCancellationRequested();
        if (DisposeCount > 0)
        {
            throw new ObjectDisposedException(nameof(FakeRabbitMqAsyncConfirmPublishChannel));
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ publish channel is not open.");
        }

        if (BeforePublishAsync is not null)
        {
            await BeforePublishAsync().ConfigureAwait(false);
        }

        if (RemainingPublishFailures > 0)
        {
            RemainingPublishFailures--;
            throw PublishException
                ?? new InvalidOperationException("RabbitMQ publish write failed.");
        }

        if (PublishException is not null)
        {
            throw PublishException;
        }

        Publications.Add(new FakeRabbitMqConfirmedPublication(
            exchange,
            routingKey,
            messageId,
            appId,
            CorrelationId: null,
            ContentType: null,
            RequestIdHeader: null,
            expiration,
            Persistent: true,
            Mandatory: OverviewDbTopology.Mandatory,
            body.ToArray()));

        if (AutoAck)
        {
            var delay = AutoAckDelay;
            var seq = publishSequenceNumber;
            _ = Task.Run(async () =>
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }

                await RaiseAckAsync(seq, multiple: false).ConfigureAwait(false);
            });
        }
    }

    /// <summary>Raises a Basic.Ack for tests.</summary>
    public Task RaiseAckAsync(ulong deliveryTag, bool multiple) =>
        _acks?.Invoke(this, new BasicAckEventArgs(deliveryTag, multiple, CancellationToken.None))
        ?? Task.CompletedTask;

    /// <summary>Raises a Basic.Nack for tests.</summary>
    public Task RaiseNackAsync(ulong deliveryTag, bool multiple) =>
        _nacks?.Invoke(this, new BasicNackEventArgs(deliveryTag, multiple, requeue: false, CancellationToken.None))
        ?? Task.CompletedTask;

    /// <summary>Raises a Basic.Return for tests.</summary>
    public Task RaiseReturnAsync(ulong publishSequenceNumber, ReadOnlyMemory<byte> body)
    {
        var properties = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                // Mirror production AMQP table representation (invariant decimal string).
                [Constants.PublishSequenceNumberHeader] =
                    RabbitMqClientAsyncConfirmPublishChannel.FormatPublishSequenceNumberHeader(
                        publishSequenceNumber),
            },
        };
        return _returns?.Invoke(
                   this,
                   new BasicReturnEventArgs(
                       312,
                       "NO_ROUTE",
                       OverviewDbTopology.DefaultExchange,
                       OverviewDbTopology.RoutingKey,
                       properties,
                       body,
                       CancellationToken.None))
               ?? Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Recorded article-work RPC publication.</summary>
internal sealed record FakeRabbitMqRpcPublication(
    string Exchange,
    string RoutingKey,
    string CorrelationId,
    string RequestId,
    string ReplyTo,
    string ContentType,
    string Expiration,
    byte[] Body);

/// <summary>In-memory manual-ack RabbitMQ channel for consume/settle tests.</summary>
internal sealed class FakeRabbitMqManualAckChannel : IRabbitMqManualAckChannel
{
    /// <summary>Initializes a new fake manual-ack channel.</summary>
    public FakeRabbitMqManualAckChannel(long generation)
    {
        Generation = generation;
    }

    /// <inheritdoc />
    public long Generation { get; }

    /// <inheritdoc />
    public bool IsOpen { get; set; } = true;

    /// <inheritdoc />
    public event EventHandler<RabbitMqChannelFaultedEventArgs>? ChannelFaulted;

    /// <summary>Gets recorded exchange declarations.</summary>
    public List<FakeRabbitMqExchangeDeclaration> Exchanges { get; } = [];

    /// <summary>Gets recorded queue declarations.</summary>
    public List<FakeRabbitMqQueueDeclaration> Queues { get; } = [];

    /// <summary>Gets recorded bindings.</summary>
    public List<FakeRabbitMqBindingDeclaration> Bindings { get; } = [];

    /// <summary>Gets recorded acknowledgements.</summary>
    public List<ulong> Acks { get; } = [];

    /// <summary>Gets recorded negative acknowledgements.</summary>
    public List<(ulong DeliveryTag, bool Requeue)> Nacks { get; } = [];

    /// <summary>Gets how many times the channel was disposed.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>Gets the consume handler when a consumer has started.</summary>
    public Func<RabbitMqManualAckDelivery, Task>? DeliveryHandler { get; private set; }

    /// <summary>Gets the consumed queue name.</summary>
    public string? ConsumedQueue { get; private set; }

    /// <summary>Gets the configured prefetch.</summary>
    public ushort PrefetchCount { get; private set; }

    /// <inheritdoc />
    public Task ExchangeDeclareAsync(
        string exchange,
        string type,
        bool durable,
        bool autoDelete,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Exchanges.Add(new FakeRabbitMqExchangeDeclaration(
            exchange,
            type,
            durable,
            autoDelete,
            arguments is null ? null : new Dictionary<string, object?>(arguments, StringComparer.Ordinal)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task QueueDeclareAsync(
        string queue,
        bool durable,
        bool exclusive,
        bool autoDelete,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Queues.Add(new FakeRabbitMqQueueDeclaration(
            queue,
            durable,
            exclusive,
            autoDelete,
            arguments is null ? null : new Dictionary<string, object?>(arguments, StringComparer.Ordinal)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task QueueBindAsync(
        string queue,
        string exchange,
        string routingKey,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Bindings.Add(new FakeRabbitMqBindingDeclaration(
            queue,
            exchange,
            routingKey,
            arguments is null ? null : new Dictionary<string, object?>(arguments, StringComparer.Ordinal)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string> BasicConsumeAsync(
        string queue,
        ushort prefetchCount,
        Func<RabbitMqManualAckDelivery, Task> onDelivery,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConsumedQueue = queue;
        PrefetchCount = prefetchCount;
        DeliveryHandler = onDelivery;
        return Task.FromResult("fake-manual-ack-consumer");
    }

    /// <inheritdoc />
    public Task BasicCancelAsync(string consumerTag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task BasicAckAsync(ulong deliveryTag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Acks.Add(deliveryTag);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task BasicNackAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Nacks.Add((deliveryTag, requeue));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task BasicRejectAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Nacks.Add((deliveryTag, requeue));
        return Task.CompletedTask;
    }

    /// <summary>Delivers one message to the registered manual-ack consumer.</summary>
    public Task DeliverAsync(
        ulong deliveryTag,
        ReadOnlyMemory<byte> body,
        string? contentType = StorageServerAdvertisementWireProtocol.JsonContentType,
        string? exchange = CacheFleetTopology.BroadcastExchangeName,
        string? routingKey = "",
        string? correlationId = null,
        string? replyTo = null)
    {
        if (DeliveryHandler is null)
        {
            throw new InvalidOperationException("No manual-ack consumer is registered.");
        }

        var delivery = new RabbitMqManualAckDelivery(
            deliveryTag,
            body.ToArray(),
            correlationId,
            replyTo,
            contentType,
            RequestIdHeader: null,
            Redelivered: false,
            routingKey,
            exchange,
            ConsumerTag: "fake-manual-ack-consumer",
            Generation);
        return DeliveryHandler(delivery);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Recorded confirm-enabled publication.</summary>
internal sealed record FakeRabbitMqConfirmedPublication(
    string Exchange,
    string RoutingKey,
    string MessageId,
    string? AppId,
    string? CorrelationId,
    string? ContentType,
    string? RequestIdHeader,
    string Expiration,
    bool Persistent,
    bool Mandatory,
    byte[] Body);

/// <summary>Recorded exchange declaration.</summary>
internal sealed record FakeRabbitMqExchangeDeclaration(
    string Name,
    string Type,
    bool Durable,
    bool AutoDelete,
    IReadOnlyDictionary<string, object?>? Arguments);

/// <summary>Recorded queue declaration.</summary>
internal sealed record FakeRabbitMqQueueDeclaration(
    string Name,
    bool Durable,
    bool Exclusive,
    bool AutoDelete,
    IReadOnlyDictionary<string, object?>? Arguments);

/// <summary>Recorded queue binding.</summary>
internal sealed record FakeRabbitMqBindingDeclaration(
    string Queue,
    string Exchange,
    string RoutingKey,
    IReadOnlyDictionary<string, object?>? Arguments);
