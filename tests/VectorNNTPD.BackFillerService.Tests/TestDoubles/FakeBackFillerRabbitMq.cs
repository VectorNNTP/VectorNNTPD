using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.TestDoubles
{
    /// <summary>In-memory RabbitMQ connection factory for BackFiller offline tests.</summary>
    internal sealed class FakeBackFillerRabbitMqConnectionFactory : IRabbitMqConnectionFactory
    {
        /// <summary>Successful connections returned from <see cref="ConnectAsync"/>.</summary>
        private int _connectCount;
        /// <summary>Total connect attempts including failures.</summary>
        private int _attemptCount;

        /// <summary>Successful connects after configured failures are exhausted.</summary>
        internal int ConnectCount => Volatile.Read(ref _connectCount);

        /// <summary>Every connect attempt, including those that throw before creating a connection.</summary>
        internal int AttemptCount => Volatile.Read(ref _attemptCount);

        /// <summary>Most recently created fake connection from a successful connect.</summary>
        public FakeBackFillerRabbitMqConnection? LastConnection { get; private set; }

        /// <summary>All fake connections created in order; does not remove disposed instances.</summary>
        public List<FakeBackFillerRabbitMqConnection> Connections { get; } = [];

        /// <summary>When set, thrown on connect after optional failure countdown; also used as the default broker-down error.</summary>
        public Exception? ConnectException { get; set; }

        /// <summary>Decremented on each failing connect before success; when positive, connect throws without incrementing <see cref="ConnectCount"/>.</summary>
        public int RemainingConnectFailures { get; set; }

        /// <summary>Completed when connect begins after incrementing the attempt counter and before optional blocking.</summary>
        public TaskCompletionSource? ConnectStarted { get; set; }

        /// <summary>When set, connect waits on this task until the test releases it.</summary>
        public TaskCompletionSource? BlockConnect { get; set; }

        /// <summary>When set, receives the successful connect count after a connection is created and recorded.</summary>
        public TaskCompletionSource<int>? Connected { get; set; }

        /// <summary>When true, new connections are created with <see cref="FakeBackFillerRabbitMqConnection.IsOpen"/> false to simulate an unusable broker handle.</summary>
        public bool ReturnUnusableConnection { get; set; }

        /// <summary>Default publisher-confirm simulation copied onto each new connection and its publish channels.</summary>
        public FakePublishConfirmBehavior DefaultPublishConfirmBehavior { get; set; } =
            FakePublishConfirmBehavior.Wait;

        /// <summary>Forwarded to each new connection to gate publish-channel creation.</summary>
        public TaskCompletionSource? BlockCreatePublishChannel { get; set; }

        /// <summary>Forwarded to each new connection and signaled when publish-channel creation starts.</summary>
        public TaskCompletionSource? CreatePublishChannelStarted { get; set; }

        /// <summary>
        /// Copied onto each new connection as <see cref="FakeBackFillerRabbitMqConnection.CreateChannelException"/>.
        /// A replacement connection keeps failing channel creation until this is cleared before that connect.
        /// </summary>
        public Exception? CreateChannelException { get; set; }

        /// <summary>
        /// Simulates broker connect: honors failure count, exceptions, and gates, then builds an in-memory connection from options.
        /// Does not open AMQP/TCP.
        /// </summary>
        /// <param name="options">Host, port, and virtual host copied onto the fake connection metadata.</param>
        /// <param name="connectionName">Stored as the client-provided connection name.</param>
        /// <param name="cancellationToken">Honored while blocked and before throwing or returning.</param>
        /// <returns>A fake connection registered in <see cref="Connections"/>.</returns>
        public async Task<IRabbitMqConnection> ConnectAsync(
            RabbitMqOptions options,
            string connectionName,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

            Interlocked.Increment(ref _attemptCount);
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
            var connection = new FakeBackFillerRabbitMqConnection(
                hosts.Length > 0 ? hosts[0]! : "127.0.0.1",
                options.Port ?? 5672,
                string.IsNullOrWhiteSpace(options.VirtualHost) ? "/" : options.VirtualHost.Trim(),
                connectionName)
            {
                IsOpen = !ReturnUnusableConnection,
                DefaultPublishConfirmBehavior = DefaultPublishConfirmBehavior,
                BlockCreatePublishChannel = BlockCreatePublishChannel,
                CreatePublishChannelStarted = CreatePublishChannelStarted,
                CreateChannelException = CreateChannelException,
            };

            LastConnection = connection;
            Connections.Add(connection);
            Connected?.TrySetResult(count);
            return connection;
        }
    }

    /// <summary>
    /// Simulates IRabbitMqConnection manual-ack and publish channels; topology/RPC/async-confirm are unsupported.
    /// </summary>

    internal sealed class FakeBackFillerRabbitMqConnection : IRabbitMqConnection
    {
        /// <summary>Captures connection identity metadata; does not open a TCP socket.</summary>
        /// <param name="host">Broker host label recorded for assertions.</param>
        /// <param name="port">Broker port recorded for assertions.</param>
        /// <param name="virtualHost">Virtual host recorded for assertions.</param>
        /// <param name="clientProvidedName">Client-provided connection name.</param>
        internal FakeBackFillerRabbitMqConnection(string host, int port, string virtualHost, string clientProvidedName)
        {
            Host = host;
            Port = port;
            VirtualHost = virtualHost;
            ClientProvidedName = clientProvidedName;
        }

        /// <summary>Writable open flag; when false, channel creation throws as on a closed broker connection.</summary>
        public bool IsOpen { get; set; } = true;

        /// <summary>Broker host label recorded at construction for assertions.</summary>
        public string Host { get; }

        /// <summary>Broker port recorded at construction for assertions.</summary>
        public int Port { get; }

        /// <summary>Virtual host recorded at construction for assertions.</summary>
        public string VirtualHost { get; }

        /// <summary>Client-provided connection name supplied by the factory connect call.</summary>
        public string ClientProvidedName { get; }

        /// <summary>Number of completed asynchronous disposes on this connection.</summary>
        public int DisposeCount { get; private set; }

        /// <summary>Completed when dispose begins, before optional blocking.</summary>
        public TaskCompletionSource? DisposeStarted { get; set; }

        /// <summary>When set, dispose waits on this task until the test releases it.</summary>
        public TaskCompletionSource? BlockDispose { get; set; }

        /// <summary>Raised by <see cref="SimulateLost"/>; not wired to a real broker.</summary>
        public event EventHandler<RabbitMqConnectionLostEventArgs>? ConnectionLost;

        /// <summary>Raised by <see cref="SimulateBlocked"/>; not wired to a real broker.</summary>
        public event EventHandler<RabbitMqConnectionBlockedEventArgs>? ConnectionBlocked;

        /// <summary>Raised by <see cref="SimulateUnblocked"/>; not wired to a real broker.</summary>
        public event EventHandler? ConnectionUnblocked;

        /// <summary>Manual-ack channels created through this connection in order.</summary>
        public List<FakeBackFillerRabbitMqChannel> Channels { get; } = [];

        /// <summary>Publish channels created through this connection in order.</summary>
        public List<FakeBackFillerRabbitMqPublishChannel> PublishChannels { get; } = [];

        /// <summary>When set, thrown from manual-ack channel creation instead of adding to <see cref="Channels"/>.</summary>
        public Exception? CreateChannelException { get; set; }

        /// <summary>Manual-ack channel create attempts, including those that throw <see cref="CreateChannelException"/>.</summary>
        public int ManualAckChannelCreateAttempts { get; private set; }

        /// <summary>
        /// Invoked with each new manual-ack channel before it is returned.
        /// Tests use it to arm a declaration failure on every channel.
        /// </summary>
        public Action<FakeBackFillerRabbitMqChannel>? OnManualAckChannelCreated { get; set; }

        /// <summary>When set, thrown from publish-channel creation after optional start/block gates.</summary>
        public Exception? CreatePublishChannelException { get; set; }

        /// <summary>
        /// When set, the next manual-ack channel receives this as
        /// <see cref="FakeBackFillerRabbitMqChannel.QueueDeclareException"/> (once).
        /// </summary>
        public Exception? NextChannelQueueDeclareException { get; set; }

        /// <summary>Signaled when publish-channel creation starts, before optional blocking.</summary>
        public TaskCompletionSource? CreatePublishChannelStarted { get; set; }

        /// <summary>When set, publish-channel creation waits on this task until the test releases it.</summary>
        public TaskCompletionSource? BlockCreatePublishChannel { get; set; }

        /// <summary>Optional hook invoked with each new publish channel before it is added to <see cref="PublishChannels"/>.</summary>
        public Action<FakeBackFillerRabbitMqPublishChannel>? OnPublishChannelCreated { get; set; }

        /// <summary>Default confirm simulation applied to publish channels created on this connection.</summary>
        public FakePublishConfirmBehavior DefaultPublishConfirmBehavior { get; set; } =
            FakePublishConfirmBehavior.Wait;

        /// <summary>Not supported in Article Work offline tests; topology uses manual-ack channels instead.</summary>
        /// <param name="cancellationToken">Unused because the method always throws.</param>
        public Task<IRabbitMqTopologyChannel> CreateTopologyChannelAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("BackFiller Article Work tests use manual-ack channels for topology.");

        /// <summary>Not supported; Article Work tests do not model RPC channels.</summary>
        /// <param name="generation">Unused because the method always throws.</param>
        /// <param name="cancellationToken">Unused because the method always throws.</param>
        public Task<IRabbitMqRpcChannel> CreateRpcChannelAsync(long generation, CancellationToken cancellationToken) =>
            throw new NotSupportedException("BackFiller Article Work tests do not open RPC channels.");

        /// <summary>Not supported; Article Work tests use synchronous confirm publish channels only.</summary>
        /// <param name="generation">Unused because the method always throws.</param>
        /// <param name="cancellationToken">Unused because the method always throws.</param>
        public Task<IRabbitMqAsyncConfirmPublishChannel> CreateAsyncConfirmPublishChannelAsync(
            long generation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("BackFiller Article Work tests do not open async-confirm channels.");

        /// <summary>
        /// Creates a recording manual-ack channel when open and no create exception is configured.
        /// Applies <see cref="NextChannelQueueDeclareException"/> once to the new channel.
        /// </summary>
        /// <param name="generation">Stored on the fake channel for consumer tag generation.</param>
        /// <param name="cancellationToken">Honored before validation and creation.</param>
        /// <returns>A fake manual-ack channel added to <see cref="Channels"/>.</returns>
        public Task<IRabbitMqManualAckChannel> CreateManualAckChannelAsync(long generation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManualAckChannelCreateAttempts++;
            if (CreateChannelException is not null)
            {
                throw CreateChannelException;
            }

            if (!IsOpen)
            {
                throw new InvalidOperationException("RabbitMQ connection is not open for channel creation.");
            }

            var channel = new FakeBackFillerRabbitMqChannel(generation);
            if (NextChannelQueueDeclareException is not null)
            {
                channel.QueueDeclareException = NextChannelQueueDeclareException;
                NextChannelQueueDeclareException = null;
            }

            OnManualAckChannelCreated?.Invoke(channel);
            Channels.Add(channel);
            return Task.FromResult<IRabbitMqManualAckChannel>(channel);
        }

        /// <summary>
        /// Signals start gates, optionally blocks, then creates a publish channel with default confirm behavior.
        /// Throws when closed or when <see cref="CreatePublishChannelException"/> is set.
        /// </summary>
        /// <param name="generation">Stored on the fake publish channel.</param>
        /// <param name="cancellationToken">Honored while blocked and before validation.</param>
        /// <returns>A fake publish channel registered in <see cref="PublishChannels"/>.</returns>
        public async Task<IRabbitMqPublishChannel> CreatePublishChannelAsync(
            long generation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreatePublishChannelStarted?.TrySetResult();
            if (BlockCreatePublishChannel is not null)
            {
                await BlockCreatePublishChannel.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (CreatePublishChannelException is not null)
            {
                throw CreatePublishChannelException;
            }

            if (!IsOpen)
            {
                throw new InvalidOperationException("RabbitMQ connection is not open for publish channel creation.");
            }

            var channel = new FakeBackFillerRabbitMqPublishChannel(generation)
            {
                ConfirmBehavior = DefaultPublishConfirmBehavior,
            };
            OnPublishChannelCreated?.Invoke(channel);
            PublishChannels.Add(channel);
            return channel;
        }

        /// <summary>Marks the connection closed and raises <see cref="ConnectionLost"/> with broker-like reply metadata; does not tear down channels automatically.</summary>
        /// <param name="replyCode">Reported reply code on the lost event.</param>
        /// <param name="replyText">Reported reply text on the lost event.</param>
        /// <param name="initiator">Reported initiator label on the lost event.</param>
        internal void SimulateLost(
            ushort replyCode = 320,
            string replyText = "connection lost",
            string initiator = "Peer")
        {
            IsOpen = false;
            ConnectionLost?.Invoke(this, new RabbitMqConnectionLostEventArgs(replyCode, replyText, initiator));
        }

        /// <summary>Raises <see cref="ConnectionBlocked"/> while leaving <see cref="IsOpen"/> unchanged.</summary>
        /// <param name="reason">Block reason delivered to subscribers.</param>
        internal void SimulateBlocked(string reason = "low on disk")
        {
            ConnectionBlocked?.Invoke(this, new RabbitMqConnectionBlockedEventArgs(reason));
        }

        /// <summary>Raises <see cref="ConnectionUnblocked"/> while leaving <see cref="IsOpen"/> unchanged.</summary>
        internal void SimulateUnblocked()
        {
            ConnectionUnblocked?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>When set, thrown after dispose gates complete and the dispose count is incremented.</summary>
        public Exception? DisposeException { get; set; }

        /// <summary>Runs dispose gates, increments <see cref="DisposeCount"/>, optionally throws, then clears <see cref="IsOpen"/>.</summary>
        public async ValueTask DisposeAsync()
        {
            DisposeStarted?.TrySetResult();
            if (BlockDispose is not null)
            {
                await BlockDispose.Task.ConfigureAwait(false);
            }

            DisposeCount++;
            if (DisposeException is { } disposeException)
            {
                throw disposeException;
            }
            IsOpen = false;
        }
    }

    /// <summary>Recorded ack/nack settlement for a delivery tag on a fake channel.</summary>
    /// <param name="DeliveryTag">Broker delivery tag settled.</param>
    /// <param name="Acknowledge">True for ack; false for nack.</param>
    /// <param name="Requeue">Requeue flag on nack.</param>
    internal readonly record struct FakeRabbitMqSettlement(ulong DeliveryTag, bool Acknowledge, bool Requeue);

    /// <summary>
    /// Records declare/consume/ack/nack and supports gated settlement for concurrency tests.
    /// </summary>

    internal sealed class FakeBackFillerRabbitMqChannel(long generation) : IRabbitMqManualAckChannel
    {
        /// <summary>Consumer callback registered by the last successful consume.</summary>
        private Func<RabbitMqManualAckDelivery, Task>? _onDelivery;

        /// <summary>Broker channel generation supplied at creation; embedded in generated consumer tags.</summary>
        public long Generation { get; } = generation;

        /// <summary>Writable open flag; when false, consume and settlement operations throw.</summary>
        public bool IsOpen { get; set; } = true;

        /// <summary>Raised by <see cref="SimulateChannelShutdown"/> / <see cref="SimulateConsumerCancelled"/>; not wired to a real broker.</summary>
        public event EventHandler<RabbitMqChannelFaultedEventArgs>? ChannelFaulted;

        /// <summary>Number of exclusive dispose passes completed on this channel.</summary>
        public int DisposeCount { get; private set; }

        /// <summary>Successful basic.consume registrations on this channel.</summary>
        public int ConsumeCount { get; private set; }

        /// <summary>basic.cancel calls observed on this channel.</summary>
        public int CancelCount { get; private set; }

        /// <summary>Prefetch from the most recent consume call.</summary>
        public ushort? LastPrefetch { get; private set; }

        /// <summary>Queue name from the most recent consume call.</summary>
        public string? LastQueue { get; private set; }

        /// <summary>Consumer tag returned from the most recent consume call.</summary>
        public string? ConsumerTag { get; private set; }

        /// <summary>Queue names passed to every consume call in order.</summary>
        public List<string> ConsumedQueues { get; } = [];

        /// <summary>Ack and nack settlements recorded in invocation order.</summary>
        public List<FakeRabbitMqSettlement> Settlements { get; } = [];

        /// <summary>Exchange declare calls in invocation order.</summary>
        public List<(string Name, string Type, bool Durable, bool AutoDelete)> ExchangeDeclarations { get; } = [];

        /// <summary>Queue declare calls in invocation order.</summary>
        public List<(string Name, bool Durable, bool Exclusive, bool AutoDelete, IReadOnlyDictionary<string, object?>? Arguments)> QueueDeclarations { get; } = [];

        /// <summary>Queue bind calls in invocation order.</summary>
        public List<(string Queue, string Exchange, string RoutingKey)> BindingDeclarations { get; } = [];

        /// <summary>When set, thrown from exchange declare instead of recording the declaration.</summary>
        public Exception? ExchangeDeclareException { get; set; }

        /// <summary>
        /// When it returns an exception, that exchange name fails and other exchanges on this channel are still declared.
        /// Checked before <see cref="ExchangeDeclareException"/>.
        /// </summary>
        public Func<string, Exception?>? ExchangeDeclareFailure { get; set; }

        /// <summary>When set, thrown from queue declare instead of recording the declaration.</summary>
        public Exception? QueueDeclareException { get; set; }

        /// <summary>
        /// When it returns an exception, that queue name fails and clears <see cref="IsOpen"/> to model a broker channel close.
        /// Checked before <see cref="QueueDeclareException"/>.
        /// </summary>
        public Func<string, Exception?>? QueueDeclareFailure { get; set; }

        /// <summary>When set, thrown from queue bind instead of recording the binding.</summary>
        public Exception? QueueBindException { get; set; }

        /// <summary>When set, thrown from consume instead of registering a consumer callback.</summary>
        public Exception? ConsumeException { get; set; }

        /// <summary>When set, thrown from ack after optional ack gates complete.</summary>
        public Exception? AckException { get; set; }

        /// <summary>When set on the gated nack path, thrown once and cleared; immediate nacks throw without clearing when no gate is configured.</summary>
        public Exception? NackException { get; set; }

        /// <summary>Signaled when a gated nack enters the broker-op section, before waiting on <see cref="NackGate"/>.</summary>
        public TaskCompletionSource? NackEntered { get; set; }

        /// <summary>When set, nacks wait here so tests can observe concurrent overlap via <see cref="MaxNackInFlight"/>.</summary>
        public TaskCompletionSource? NackGate { get; set; }

        /// <summary>Concurrent nack operations inside the gated nack path.</summary>
        private int _nackInFlight;

        /// <summary>High-water concurrent nacks observed for overlap assertions.</summary>
        private int _maxNackInFlight;

        /// <summary>Peak concurrent gated nack operations observed while <see cref="NackGate"/> is configured.</summary>
        internal int MaxNackInFlight => Volatile.Read(ref _maxNackInFlight);

        /// <summary>Signaled when an ack enters the broker-op section, before waiting on <see cref="AckGate"/>.</summary>
        public TaskCompletionSource? AckStarted { get; set; }

        /// <summary>When set, acks wait here before recording a settlement or throwing <see cref="AckException"/>.</summary>
        public TaskCompletionSource? AckGate { get; set; }

        /// <summary>Records exchange declare parameters; does not create broker topology.</summary>
        /// <param name="exchange">Exchange name to record.</param>
        /// <param name="type">Exchange type to record.</param>
        /// <param name="durable">Durable flag to record.</param>
        /// <param name="autoDelete">Auto-delete flag to record.</param>
        /// <param name="arguments">Accepted for interface compatibility; not stored.</param>
        /// <param name="cancellationToken">Honored before recording or throwing.</param>
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
            cancellationToken.ThrowIfCancellationRequested();
            if (ExchangeDeclareFailure?.Invoke(exchange) is { } selectiveFailure)
            {
                throw selectiveFailure;
            }

            if (ExchangeDeclareException is not null)
            {
                throw ExchangeDeclareException;
            }

            ExchangeDeclarations.Add((exchange, type, durable, autoDelete));
            return Task.CompletedTask;
        }

        /// <summary>Records queue declare parameters; does not create a broker queue.</summary>
        /// <param name="queue">Queue name to record.</param>
        /// <param name="durable">Durable flag to record.</param>
        /// <param name="exclusive">Exclusive flag to record.</param>
        /// <param name="autoDelete">Auto-delete flag to record.</param>
        /// <param name="arguments">Optional arguments stored with the declaration entry.</param>
        /// <param name="cancellationToken">Honored before recording or throwing.</param>
        public Task QueueDeclareAsync(
            string queue,
            bool durable,
            bool exclusive,
            bool autoDelete,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queue);
            cancellationToken.ThrowIfCancellationRequested();
            if (QueueDeclareFailure?.Invoke(queue) is { } selectiveFailure)
            {
                // Broker PRECONDITION_FAILED and similar declare failures close the AMQP channel.
                IsOpen = false;
                throw selectiveFailure;
            }

            if (QueueDeclareException is not null)
            {
                IsOpen = false;
                throw QueueDeclareException;
            }

            QueueDeclarations.Add((queue, durable, exclusive, autoDelete, arguments));
            return Task.CompletedTask;
        }

        /// <summary>Records queue bind parameters; does not alter broker routing.</summary>
        /// <param name="queue">Queue name to record.</param>
        /// <param name="exchange">Exchange name to record.</param>
        /// <param name="routingKey">Routing key to record.</param>
        /// <param name="arguments">Accepted for interface compatibility; not stored.</param>
        /// <param name="cancellationToken">Honored before recording or throwing.</param>
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
            cancellationToken.ThrowIfCancellationRequested();
            if (QueueBindException is not null)
            {
                throw QueueBindException;
            }

            BindingDeclarations.Add((queue, exchange, routingKey));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Registers the delivery callback and records consume metadata; does not pull messages from a broker.
        /// </summary>
        /// <param name="queue">Queue name recorded and appended to <see cref="ConsumedQueues"/>.</param>
        /// <param name="prefetchCount">Stored as <see cref="LastPrefetch"/>.</param>
        /// <param name="onDelivery">Callback invoked when the test calls <see cref="DeliverAsync"/>.</param>
        /// <param name="cancellationToken">Honored before validation and registration.</param>
        /// <returns>A deterministic consumer tag derived from generation and consume count.</returns>
        public Task<string> BasicConsumeAsync(
            string queue,
            ushort prefetchCount,
            Func<RabbitMqManualAckDelivery, Task> onDelivery,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queue);
            ArgumentNullException.ThrowIfNull(onDelivery);
            cancellationToken.ThrowIfCancellationRequested();
            if (ConsumeException is not null)
            {
                throw ConsumeException;
            }

            if (!IsOpen)
            {
                throw new InvalidOperationException("RabbitMQ channel is not open for consume.");
            }

            _onDelivery = onDelivery;
            LastPrefetch = prefetchCount;
            LastQueue = queue;
            ConsumedQueues.Add(queue);
            ConsumeCount++;
            ConsumerTag = $"ctag-{Generation}-{ConsumeCount}";
            return Task.FromResult(ConsumerTag);
        }

        /// <summary>Increments <see cref="CancelCount"/> without unregistering the consumer callback or raising <see cref="ChannelFaulted"/>.</summary>
        /// <param name="consumerTag">Validated but not matched against <see cref="ConsumerTag"/>.</param>
        /// <param name="cancellationToken">Honored before incrementing the counter.</param>
        public Task BasicCancelAsync(string consumerTag, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(consumerTag);
            cancellationToken.ThrowIfCancellationRequested();
            CancelCount++;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Marks the channel closed and raises <see cref="ChannelFaulted"/> as an unexpected channel shutdown.
        /// Leaves the process connection open.
        /// </summary>
        /// <param name="reason">Fault reason text.</param>
        internal void SimulateChannelShutdown(string reason = "channel closed by broker")
        {
            IsOpen = false;
            ChannelFaulted?.Invoke(
                this,
                new RabbitMqChannelFaultedEventArgs(RabbitMqChannelFaultKind.ChannelShutdown, reason));
        }

        /// <summary>
        /// Marks the channel closed and raises <see cref="ChannelFaulted"/> as a broker-originated consumer cancellation.
        /// Leaves the process connection open.
        /// </summary>
        /// <param name="reason">Fault reason text.</param>
        internal void SimulateConsumerCancelled(string reason = "consumer cancelled by broker")
        {
            IsOpen = false;
            ChannelFaulted?.Invoke(
                this,
                new RabbitMqChannelFaultedEventArgs(RabbitMqChannelFaultKind.ConsumerCancelled, reason));
        }

        /// <summary>
        /// Runs ack gates, optionally throws <see cref="AckException"/>, then appends an ack settlement.
        /// Tracks broker-op overlap via <see cref="MaxBrokerOpsInFlight"/> while inside the exclusive section.
        /// </summary>
        /// <param name="deliveryTag">Delivery tag recorded in <see cref="Settlements"/>.</param>
        /// <param name="cancellationToken">Honored before gates; not passed to infinite gate waits beyond cancellation.</param>
        public async Task BasicAckAsync(ulong deliveryTag, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(DisposeCount > 0, this);
            if (!IsOpen)
            {
                throw new InvalidOperationException("RabbitMQ channel is not open for ACK.");
            }

            EnterBrokerOp();
            try
            {
                AckStarted?.TrySetResult();
                if (AckGate is not null)
                {
                    await AckGate.Task.ConfigureAwait(false);
                }

                if (AckException is not null)
                {
                    throw AckException;
                }

                Settlements.Add(new FakeRabbitMqSettlement(deliveryTag, Acknowledge: true, Requeue: false));
            }
            finally
            {
                ExitBrokerOp();
            }
        }

        /// <summary>
        /// Records a nack immediately when no gate is configured; otherwise delegates to the gated path for overlap testing.
        /// </summary>
        /// <param name="deliveryTag">Delivery tag recorded when the nack completes.</param>
        /// <param name="requeue">Requeue flag stored on the settlement.</param>
        /// <param name="cancellationToken">Honored before entering settlement logic.</param>
        public Task BasicNackAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken) =>
            RecordNegativeSettlementAsync(deliveryTag, requeue, cancellationToken, "NACK");

        /// <inheritdoc />
        public Task BasicRejectAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken) =>
            RecordNegativeSettlementAsync(deliveryTag, requeue, cancellationToken, "REJECT");

        /// <summary>
        /// Records a negative settlement (NACK or REJECT) with the same gate/exception hooks used by settlement overlap tests.
        /// </summary>
        private Task RecordNegativeSettlementAsync(
            ulong deliveryTag,
            bool requeue,
            CancellationToken cancellationToken,
            string operation)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(DisposeCount > 0, this);
            if (!IsOpen)
            {
                throw new InvalidOperationException($"RabbitMQ channel is not open for {operation}.");
            }

            if (NackException is not null)
            {
                var error = NackException;
                NackException = null;
                throw error;
            }

            if (NackGate is null)
            {
                Settlements.Add(new FakeRabbitMqSettlement(deliveryTag, Acknowledge: false, requeue));
                return Task.CompletedTask;
            }

            return NackAfterGateAsync(deliveryTag, requeue);
        }

        /// <summary>Waits on <see cref="NackGate"/> while tracking concurrent nack depth, then records the settlement or throws.</summary>
        /// <param name="deliveryTag">Delivery tag to settle after the gate releases.</param>
        /// <param name="requeue">Requeue flag stored on the settlement.</param>
        private async Task NackAfterGateAsync(ulong deliveryTag, bool requeue)
        {
            var inFlight = Interlocked.Increment(ref _nackInFlight);
            int observed;
            while ((observed = Volatile.Read(ref _maxNackInFlight)) < inFlight
                && Interlocked.CompareExchange(ref _maxNackInFlight, inFlight, observed) != observed)
            {
                // Compare-exchange lost the race. Retry until this count is installed or a higher count is visible.
            }

            EnterBrokerOp();
            try
            {
                NackEntered?.TrySetResult();
                await NackGate!.Task.ConfigureAwait(false);
                if (NackException is not null)
                {
                    var error = NackException;
                    NackException = null;
                    throw error;
                }

                Settlements.Add(new FakeRabbitMqSettlement(deliveryTag, Acknowledge: false, requeue));
            }
            finally
            {
                ExitBrokerOp();
                Interlocked.Decrement(ref _nackInFlight);
            }
        }

        /// <summary>Broker settlement operations currently inside the exclusive gate.</summary>
        private int _brokerOps;

        /// <summary>High-water concurrent broker ops observed on this channel.</summary>
        private int _maxBrokerOps;

        /// <summary>Peak concurrent ack/nack operations inside the broker-op gate on this channel.</summary>
        internal int MaxBrokerOpsInFlight => Volatile.Read(ref _maxBrokerOps);

        /// <summary>Increments in-flight broker settlement depth and updates <see cref="MaxBrokerOpsInFlight"/>.</summary>
        private void EnterBrokerOp()
        {
            var now = Interlocked.Increment(ref _brokerOps);
            int observed;
            while ((observed = Volatile.Read(ref _maxBrokerOps)) < now
                && Interlocked.CompareExchange(ref _maxBrokerOps, now, observed) != observed)
            {
                // Compare-exchange lost the race. Retry until this count is installed or a higher count is visible.
            }
        }

        /// <summary>Decrements in-flight broker settlement depth after ack or gated nack completes.</summary>
        private void ExitBrokerOp() => Interlocked.Decrement(ref _brokerOps);

        /// <summary>Invokes the consumer callback registered by the last successful consume; does not read from a broker queue.</summary>
        /// <param name="delivery">Delivery passed through to the registered handler.</param>
        /// <returns>The handler task, or throws when no consumer was registered.</returns>
        internal Task DeliverAsync(RabbitMqManualAckDelivery delivery)
        {
            if (_onDelivery is null)
            {
                throw new InvalidOperationException("RabbitMQ channel has no consumer.");
            }

            return _onDelivery(delivery);
        }

        /// <summary>Runs dispose under the Article Work settlement lease, then marks the channel closed.</summary>
        public async ValueTask DisposeAsync()
        {
            await ArticleWorkSettlementLease.RunExclusiveAsync(this, () =>
            {
                DisposeCount++;
                IsOpen = false;
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }

    /// <summary>Simulated publisher-confirm outcomes for <see cref="FakeBackFillerRabbitMqPublishChannel"/>.</summary>
    internal enum FakePublishConfirmBehavior
    {
        /// <summary>Publish waits on ConfirmGate or blocks until cancellation.</summary>
        Wait = 0,

        /// <summary>Publish completes immediately after recording the publication.</summary>
        Confirm = 1,

        /// <summary>Throws after enqueue to simulate broker nack.</summary>
        Nack = 2,

        /// <summary>Throws before recording the publication.</summary>
        ThrowOnPublish = 3,

        /// <summary>Throws OperationCanceledException as confirm timeout.</summary>
        Timeout = 4,

        /// <summary>Closes the channel and throws during confirm.</summary>
        CloseChannel = 5,

        /// <summary>Throws when the broker returns the message as unroutable.</summary>
        Unroutable = 6,
    }

    /// <summary>
    /// Records confirmed publications and simulates publisher confirm outcomes.
    /// </summary>

    internal sealed class FakeBackFillerRabbitMqPublishChannel(long generation) : IRabbitMqPublishChannel
    {
        /// <summary>Guards <see cref="Publications"/> for concurrent confirmed publishes.</summary>
        private readonly Lock _publicationsGate = new();

        /// <summary>Current confirm-wait depth for overlap observation.</summary>
        private int _confirmInFlight;

        /// <summary>Peak confirm-wait depth observed on this channel.</summary>
        private int _maxConfirmInFlight;

        /// <summary>Broker publish-channel generation supplied at creation.</summary>
        public long Generation { get; } = generation;

        /// <summary>Writable open flag; when false, publish throws; some confirm failures also clear this flag.</summary>
        public bool IsOpen { get; set; } = true;

        /// <summary>Raised by <see cref="SimulateChannelShutdown"/>; not wired to a real broker.</summary>
        public event EventHandler<RabbitMqChannelFaultedEventArgs>? ChannelFaulted;

        /// <summary>Number of dispose calls completed on this publish channel.</summary>
        public int DisposeCount { get; private set; }

        /// <summary>Selects simulated publisher-confirm outcome after the publication is enqueued.</summary>
        public FakePublishConfirmBehavior ConfirmBehavior { get; set; } = FakePublishConfirmBehavior.Wait;

        /// <summary>Signaled after the publication is appended to <see cref="Publications"/> and before confirm simulation runs.</summary>
        public TaskCompletionSource? Enqueued { get; set; }

        /// <summary>When confirm behavior is wait, blocks until this task completes or cancellation fires.</summary>
        public TaskCompletionSource? ConfirmGate { get; set; }

        /// <summary>Optional hook invoked immediately after enqueue and before confirm simulation.</summary>
        public Action? AfterEnqueue { get; set; }

        /// <summary>Peak number of overlapping confirm waits observed on this channel.</summary>
        internal int MaxConfirmInFlight => Volatile.Read(ref _maxConfirmInFlight);

        /// <summary>Publications accepted by this channel in invocation order.</summary>
        public List<RabbitMqConfirmedPublication> Publications { get; } = [];

        /// <summary>
        /// Builds a <see cref="RabbitMqConfirmedPublication"/> with Article Work defaults and delegates to the overload that simulates confirm.
        /// </summary>
        /// <param name="exchange">Exchange on the recorded publication.</param>
        /// <param name="routingKey">Routing key on the recorded publication.</param>
        /// <param name="messageId">Message identifier on the recorded publication.</param>
        /// <param name="appId">Application id on the recorded publication.</param>
        /// <param name="expiration">Expiration on the recorded publication.</param>
        /// <param name="body">Message body bytes on the recorded publication.</param>
        /// <param name="cancellationToken">Forwarded to confirm simulation.</param>
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

        /// <summary>
        /// Enqueues the publication, runs optional hooks, then simulates confirm, nack, timeout, or channel-close outcomes per <see cref="ConfirmBehavior"/>.
        /// Does not transmit AMQP frames.
        /// </summary>
        /// <param name="publication">Publication recorded before confirm simulation.</param>
        /// <param name="cancellationToken">Honored during wait behavior and timeout simulation.</param>
        public async Task PublishConfirmedAsync(RabbitMqConfirmedPublication publication, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(publication);
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(DisposeCount > 0, this);
            if (!IsOpen)
            {
                throw new InvalidOperationException("RabbitMQ publish channel is not open.");
            }

            if (ConfirmBehavior == FakePublishConfirmBehavior.ThrowOnPublish)
            {
                throw new InvalidOperationException("publish failed");
            }

            lock (_publicationsGate)
            {
                Publications.Add(publication);
            }

            var inFlight = Interlocked.Increment(ref _confirmInFlight);
            try
            {
                int observed;
                while ((observed = Volatile.Read(ref _maxConfirmInFlight)) < inFlight
                    && Interlocked.CompareExchange(ref _maxConfirmInFlight, inFlight, observed) != observed)
                {
                    // CAS retry: work occurs in the loop condition.
                }

                Enqueued?.TrySetResult();
                AfterEnqueue?.Invoke();

                if (ConfirmBehavior == FakePublishConfirmBehavior.Wait)
                {
                    if (ConfirmGate is not null)
                    {
                        await ConfirmGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                        throw new OperationCanceledException(cancellationToken);
                    }
                }

                // Re-read after an optional Wait gate so tests can switch outcome for overlapping callers.
                switch (ConfirmBehavior)
                {
                    case FakePublishConfirmBehavior.Confirm:
                    case FakePublishConfirmBehavior.Wait:
                        return;
                    case FakePublishConfirmBehavior.Nack:
                        throw new InvalidOperationException("publisher nack");
                    case FakePublishConfirmBehavior.Unroutable:
                        throw new RabbitMqPublicationUnroutableException();
                    case FakePublishConfirmBehavior.Timeout:
                        throw new OperationCanceledException("publisher confirm timeout", cancellationToken);
                    case FakePublishConfirmBehavior.CloseChannel:
                        IsOpen = false;
                        ChannelFaulted?.Invoke(
                            this,
                            new RabbitMqChannelFaultedEventArgs(
                                RabbitMqChannelFaultKind.ChannelShutdown,
                                "channel closed during confirmation"));
                        throw new InvalidOperationException("RabbitMQ publish channel closed during confirmation.");
                    default:
                        return;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _confirmInFlight);
            }
        }

        /// <summary>When set, thrown after incrementing dispose count and clearing <see cref="IsOpen"/>.</summary>
        public Exception? DisposeException { get; set; }

        /// <summary>
        /// Marks the channel closed and raises <see cref="ChannelFaulted"/> as an unexpected channel shutdown.
        /// Leaves the process connection open.
        /// </summary>
        /// <param name="reason">Fault reason text.</param>
        internal void SimulateChannelShutdown(string reason = "publish channel closed by broker")
        {
            IsOpen = false;
            ChannelFaulted?.Invoke(
                this,
                new RabbitMqChannelFaultedEventArgs(RabbitMqChannelFaultKind.ChannelShutdown, reason));
        }

        /// <summary>Increments dispose count, marks the channel closed, and optionally throws <see cref="DisposeException"/>.</summary>
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            IsOpen = false;
            if (DisposeException is { } disposeException)
            {
                throw disposeException;
            }

            return ValueTask.CompletedTask;
        }
    }
}
