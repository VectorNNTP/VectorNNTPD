using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using VectorNNTPD.BackFillerService.ArticleWork;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Publishes Article Work requests to per-backbone <c>backfiller.{backbone}</c> fanout exchanges
    /// and correlates compact JSON replies. Reply bodies do not contain article octets.
    /// </summary>
    /// <remarks>
    /// One exclusive auto-delete reply queue is owned by this client's connection. Caller wait timeouts
    /// remove the waiter but keep the correlation marked in-flight until a late reply arrives or
    /// <see cref="DisposeAsync"/> finishes its bounded drain. Closing the connection deletes ReplyTo;
    /// drain must therefore wait on broker-side outstanding work, not only on waiting callers.
    /// Work <em>requests</em> deliberately omit AMQP <c>Expiration</c> so RabbitMQ may buffer ready
    /// messages while consumers are at capacity. Response TTL remains a BackFiller reply-path concern
    /// (<see cref="ArticleWorkResponseWireProtocol.ExpirationMilliseconds"/>).
    /// </remarks>
    public sealed class ArticleWorkStressClient : IAsyncDisposable
    {
        /// <summary>Owns the exclusive ReplyTo queue; disposed last after drain.</summary>
        private readonly IConnection _connection;

        /// <summary>Channel used for BasicPublish to provider fanout exchanges.</summary>
        private readonly IChannel _publishChannel;

        /// <summary>Channel that declares ReplyTo and runs the auto-ack reply consumer.</summary>
        private readonly IChannel _consumeChannel;

        /// <summary>Exclusive auto-delete queue name passed as AMQP ReplyTo.</summary>
        private readonly string _replyQueue;

        /// <summary>Outstanding correlations keyed by AMQP CorrelationId.</summary>
        private readonly ConcurrentDictionary<string, PendingRpc> _pending =
            new(StringComparer.Ordinal);

        /// <summary>Per-request waiter timeout configured from harness options.</summary>
        private readonly TimeSpan _replyTimeout;

        /// <summary>Maximum time to wait for broker-outstanding replies before closing ReplyTo.</summary>
        private readonly TimeSpan _disposeDrainTimeout;

        /// <summary>Non-zero after <see cref="DisposeAsync"/> begins.</summary>
        private int _disposed;

        /// <summary>Active caller waiters (mirrors publish-side lifecycle accounting).</summary>
        private int _waitingCount;

        /// <summary>Published correlations not yet settled by reply, timeout path, or publish failure.</summary>
        private int _brokerOutstandingCount;

        /// <summary>Broker outstanding remaining when dispose drain timed out.</summary>
        private int _unresolvedAtDispose;

        /// <summary>Late replies received after caller timeout.</summary>
        private int _lateRepliesAfterTimeout;

        /// <summary>Set to 1 after ReplyTo consumer is registered in <see cref="ConnectAsync"/>.</summary>
        private int _replyConsumerReady;

        /// <summary>Replies with unknown or stale CorrelationId.</summary>
        private int _unknownCorrelationReplies;

        /// <summary>Optional run id propagated on request headers when set.</summary>
        private string? _stressRunId;

        /// <summary>Private ctor; use <see cref="ConnectAsync"/> to construct a connected client.</summary>
        private ArticleWorkStressClient(
            IConnection connection,
            IChannel publishChannel,
            IChannel consumeChannel,
            string replyQueue,
            TimeSpan replyTimeout,
            TimeSpan disposeDrainTimeout)
        {
            _connection = connection;
            _publishChannel = publishChannel;
            _consumeChannel = consumeChannel;
            _replyQueue = replyQueue;
            _replyTimeout = replyTimeout;
            _disposeDrainTimeout = disposeDrainTimeout;
        }

        /// <summary>
        /// Opens a RabbitMQ connection using environment credentials (never logged).
        /// </summary>
        public static async Task<ArticleWorkStressClient> ConnectAsync(
            StressHarnessOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            var factory = new ConnectionFactory
            {
                HostName = options.RabbitMqHost,
                Port = options.RabbitMqPort,
                VirtualHost = options.RabbitMqVirtualHost,
                UserName = options.RabbitMqUsername,
                Password = options.RabbitMqPassword,
                RequestedHeartbeat = TimeSpan.FromSeconds(30),
                AutomaticRecoveryEnabled = false,
                ClientProvidedName = "VectorNNTPD.BackFillerService.Tests:e2e-stress",
            };

            var connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            var publishChannel = await connection.CreateChannelAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var consumeChannel = await connection.CreateChannelAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var reply = await consumeChannel.QueueDeclareAsync(
                    queue: string.Empty,
                    durable: false,
                    exclusive: true,
                    autoDelete: true,
                    arguments: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var client = new ArticleWorkStressClient(
                connection,
                publishChannel,
                consumeChannel,
                reply.QueueName,
                options.WorkReplyTimeout,
                options.ReplyQueueDisposeDrainTimeout);

            // Reply queue must exist and the response consumer must be registered before any request publish.
            var consumer = new AsyncEventingBasicConsumer(consumeChannel);
            consumer.ReceivedAsync += client.OnReplyAsync;
            await consumeChannel.BasicConsumeAsync(
                    reply.QueueName,
                    autoAck: true,
                    consumer: consumer,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            Volatile.Write(ref client._replyConsumerReady, 1);
            return client;
        }

        /// <summary>
        /// <see langword="true"/> after the exclusive ReplyTo queue is declared and the response consumer is consuming.
        /// </summary>
        public bool IsReplyConsumerReady => Volatile.Read(ref _replyConsumerReady) != 0;

        /// <summary>Replies whose CorrelationId did not match any outstanding request (accounted, not applied to a waiter).</summary>
        public int UnknownCorrelationReplies => Volatile.Read(ref _unknownCorrelationReplies);

        /// <summary>
        /// Optional stress-run correlation written as AMQP header <c>StressRunId</c> on each request.
        /// Distinguishes this harness run from unrelated broker work in logs and captures.
        /// </summary>
        public string? StressRunId
        {
            get => _stressRunId;
            set => _stressRunId = value;
        }

        /// <summary>Number of callers still awaiting a correlated reply (excludes timed-out waiters).</summary>
        public int WaitingCount => Volatile.Read(ref _waitingCount);

        /// <summary>
        /// Number of published requests whose ReplyTo may still receive a response
        /// (includes timed-out waiters until a late reply or dispose drain completes).
        /// </summary>
        public int BrokerOutstandingCount => Volatile.Read(ref _brokerOutstandingCount);

        /// <summary>Alias for <see cref="WaitingCount"/> kept for existing harness drain loops.</summary>
        public int PendingCount => WaitingCount;

        /// <summary>Exclusive reply-queue name used as AMQP ReplyTo for this client.</summary>
        public string ReplyQueueName => _replyQueue;

        /// <summary>Requests still outstanding when dispose drain timed out (0 when drain completed).</summary>
        public int UnresolvedAtDispose => Volatile.Read(ref _unresolvedAtDispose);

        /// <summary>Replies received after the caller wait timed out (safe discard path).</summary>
        public int LateRepliesAfterTimeout => Volatile.Read(ref _lateRepliesAfterTimeout);

        /// <summary>
        /// Publishes one Article Work request and awaits the correlated reply (or timeout).
        /// </summary>
        public async Task<ArticleWorkReply> RequestAsync(
            string backbone,
            string messageId,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!IsReplyConsumerReady)
            {
                throw new InvalidOperationException(
                    "Article Work RPC response consumer is not ready; ConnectAsync must complete before RequestAsync.");
            }

            var requestId = Guid.NewGuid();
            var correlationId = Guid.NewGuid().ToString("D");
            var exchange = ComposeProviderEntity(backbone);
            var payload = BuildRequestJson(requestId, messageId, backbone);

            var tcs = new TaskCompletionSource<ArticleWorkReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingRpc(tcs);
            // Register the waiter before publishing so an immediate reply cannot race registration.
            if (!_pending.TryAdd(correlationId, pending))
            {
                return new ArticleWorkReply(
                    ArticleWorkStressOutcome.BrokerError,
                    backbone,
                    messageId,
                    requestId,
                    null,
                    null,
                    null,
                    "duplicate correlation id")
                {
                    CorrelationId = correlationId,
                };
            }

            Interlocked.Increment(ref _waitingCount);
            Interlocked.Increment(ref _brokerOutstandingCount);

            var props = CreateRequestProperties(correlationId, _replyQueue, requestId, _stressRunId);

            var publishStarted = Stopwatch.GetTimestamp();
            long publishCompleted;
            try
            {
                await _publishChannel.BasicPublishAsync(
                        exchange: exchange,
                        routingKey: exchange,
                        mandatory: false,
                        basicProperties: props,
                        body: payload,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                publishCompleted = Stopwatch.GetTimestamp();
            }
            catch (AlreadyClosedException ex)
            {
                return PublishBrokerError(ex, correlationId, backbone, messageId, requestId, publishStarted);
            }
            catch (OperationInterruptedException ex)
            {
                return PublishBrokerError(ex, correlationId, backbone, messageId, requestId, publishStarted);
            }
            catch (IOException ex)
            {
                // Includes BrokerUnreachableException.
                return PublishBrokerError(ex, correlationId, backbone, messageId, requestId, publishStarted);
            }
            catch (TimeoutException ex)
            {
                return PublishBrokerError(ex, correlationId, backbone, messageId, requestId, publishStarted);
            }
            catch (ObjectDisposedException ex)
            {
                return PublishBrokerError(ex, correlationId, backbone, messageId, requestId, publishStarted);
            }
            catch (InvalidOperationException ex)
            {
                return PublishBrokerError(ex, correlationId, backbone, messageId, requestId, publishStarted);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_replyTimeout);
            try
            {
                var reply = await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                var replyReceived = Stopwatch.GetTimestamp();
                var publishMs = (long)Stopwatch.GetElapsedTime(publishStarted, publishCompleted).TotalMilliseconds;
                var publishToReplyMs = (long)Stopwatch.GetElapsedTime(publishCompleted, replyReceived).TotalMilliseconds;
                return reply with
                {
                    CorrelationId = correlationId,
                    PublishStartedTimestamp = publishStarted,
                    PublishCompletedTimestamp = publishCompleted,
                    ReplyReceivedTimestamp = replyReceived,
                    PublishMilliseconds = publishMs,
                    LatencyMilliseconds = publishToReplyMs,
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Caller no longer waits; broker may still publish to ReplyTo.
                if (_pending.TryGetValue(correlationId, out var timedOut)
                    && Interlocked.Exchange(ref timedOut.Waiting, 0) == 1)
                {
                    Interlocked.Decrement(ref _waitingCount);
                }

                var replyReceived = Stopwatch.GetTimestamp();
                return new ArticleWorkReply(
                    ArticleWorkStressOutcome.NoResponse,
                    backbone,
                    messageId,
                    requestId,
                    null,
                    null,
                    null,
                    "work reply timeout")
                {
                    CorrelationId = correlationId,
                    PublishStartedTimestamp = publishStarted,
                    PublishCompletedTimestamp = publishCompleted,
                    ReplyReceivedTimestamp = replyReceived,
                    PublishMilliseconds = (long)Stopwatch.GetElapsedTime(publishStarted, publishCompleted).TotalMilliseconds,
                    LatencyMilliseconds = (long)Stopwatch.GetElapsedTime(publishCompleted, replyReceived).TotalMilliseconds,
                };
            }
            catch (OperationCanceledException)
            {
                CompleteBrokerOutstanding(correlationId, clearWaiting: true);
                throw;
            }
        }

        /// <summary>Maps a backbone label to the fanout exchange/queue entity name <c>backfiller.{backbone}</c>.</summary>
        public static string ComposeProviderEntity(string backbone)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
            return "backfiller." + backbone.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Builds AMQP properties for one Article Work RPC request.
        /// </summary>
        /// <remarks>
        /// Exposed for deterministic regression tests that assert the exact properties passed to publish
        /// without requiring a live broker. Work requests must not set <see cref="BasicProperties.Expiration"/>:
        /// a short request TTL silently discards buffered work and invalidates overload measurements.
        /// </remarks>
        public static BasicProperties CreateRequestProperties(
            string correlationId,
            string replyTo,
            Guid requestId,
            string? stressRunId = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
            ArgumentException.ThrowIfNullOrWhiteSpace(replyTo);

            var headers = new Dictionary<string, object?>
            {
                ["RequestId"] = requestId.ToString("D"),
            };
            if (!string.IsNullOrWhiteSpace(stressRunId))
            {
                headers["StressRunId"] = stressRunId;
            }

            return new BasicProperties
            {
                ContentType = "application/json",
                CorrelationId = correlationId,
                ReplyTo = replyTo,
                DeliveryMode = DeliveryModes.Persistent,
                Headers = headers,
            };
        }

        /// <summary>Auto-ack reply handler: completes waiters or counts late/unknown correlations.</summary>
        private async Task OnReplyAsync(object sender, BasicDeliverEventArgs args)
        {
            await Task.Yield();
            var correlationId = args.BasicProperties.CorrelationId;
            if (string.IsNullOrEmpty(correlationId) || !_pending.TryRemove(correlationId, out var pending))
            {
                Interlocked.Increment(ref _unknownCorrelationReplies);
                return;
            }

            var wasWaiting = Interlocked.Exchange(ref pending.Waiting, 0) == 1;
            if (wasWaiting)
            {
                Interlocked.Decrement(ref _waitingCount);
            }
            else
            {
                Interlocked.Increment(ref _lateRepliesAfterTimeout);
            }

            Interlocked.Decrement(ref _brokerOutstandingCount);

            try
            {
                var reply = ParseReply(args.Body.Span);
                if (wasWaiting)
                {
                    pending.Waiter.TrySetResult(reply with { CorrelationId = correlationId });
                }
            }
            catch (JsonException ex)
            {
                CompleteReplyParseFault(wasWaiting, pending, correlationId, ex);
            }
            catch (KeyNotFoundException ex)
            {
                CompleteReplyParseFault(wasWaiting, pending, correlationId, ex);
            }
            catch (InvalidOperationException ex)
            {
                CompleteReplyParseFault(wasWaiting, pending, correlationId, ex);
            }
            catch (FormatException ex)
            {
                CompleteReplyParseFault(wasWaiting, pending, correlationId, ex);
            }
            catch (ArgumentException ex)
            {
                CompleteReplyParseFault(wasWaiting, pending, correlationId, ex);
            }
        }

        /// <summary>Rolls back outstanding state and returns a synthetic <see cref="ArticleWorkStressOutcome.BrokerError"/> reply.</summary>
        private ArticleWorkReply PublishBrokerError(
            Exception ex,
            string correlationId,
            string backbone,
            string messageId,
            Guid requestId,
            long publishStarted)
        {
            CompleteBrokerOutstanding(correlationId, clearWaiting: true);
            return new ArticleWorkReply(
                ArticleWorkStressOutcome.BrokerError,
                backbone,
                messageId,
                requestId,
                null,
                null,
                null,
                ex.GetType().Name)
            {
                CorrelationId = correlationId,
                PublishStartedTimestamp = publishStarted,
                PublishCompletedTimestamp = Stopwatch.GetTimestamp(),
            };
        }

        /// <summary>Completes a still-waiting caller with broker-error when JSON parsing fails.</summary>
        private static void CompleteReplyParseFault(
            bool wasWaiting,
            PendingRpc pending,
            string correlationId,
            Exception ex)
        {
            if (!wasWaiting)
            {
                return;
            }

            pending.Waiter.TrySetResult(new ArticleWorkReply(
                ArticleWorkStressOutcome.BrokerError,
                Backbone: string.Empty,
                MessageId: string.Empty,
                RequestId: null,
                Fqdn: null,
                ListenerPort: null,
                ArticleIdHex: null,
                Error: ex.GetType().Name)
            {
                CorrelationId = correlationId,
            });
        }

        /// <summary>Removes a pending correlation and decrements lifecycle counters (timeout/cancel/publish fault).</summary>
        private void CompleteBrokerOutstanding(string correlationId, bool clearWaiting)
        {
            if (!_pending.TryRemove(correlationId, out var pending))
            {
                return;
            }

            if (clearWaiting && Interlocked.Exchange(ref pending.Waiting, 0) == 1)
            {
                Interlocked.Decrement(ref _waitingCount);
            }

            Interlocked.Decrement(ref _brokerOutstandingCount);
        }

        /// <summary>UTF-8 JSON body for version-1 Article Work request (no article bytes).</summary>
        private static byte[] BuildRequestJson(Guid requestId, string messageId, string backbone)
        {
            var json =
                "{\"version\":1,\"requestId\":\""
                + requestId.ToString("D")
                + "\",\"messageId\":"
                + JsonSerializer.Serialize(messageId)
                + ",\"backbone\":"
                + JsonSerializer.Serialize(backbone)
                + "}";
            return Encoding.UTF8.GetBytes(json);
        }

        /// <summary>Deserializes compact JSON reply metadata into <see cref="ArticleWorkReply"/>.</summary>
        private static ArticleWorkReply ParseReply(ReadOnlySpan<byte> body)
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            var root = doc.RootElement;
            var outcomeText = root.GetProperty("outcome").GetString() ?? string.Empty;
            var outcome = outcomeText switch
            {
                "Success" => ArticleWorkStressOutcome.Success,
                "ArticleNotFound" => ArticleWorkStressOutcome.ArticleNotFound,
                "InvalidArticle" => ArticleWorkStressOutcome.InvalidArticle,
                "InvalidRequest" => ArticleWorkStressOutcome.InvalidRequest,
                "ProviderFailure" => ArticleWorkStressOutcome.ProviderFailure,
                "UnexpectedFailure" => ArticleWorkStressOutcome.UnexpectedFailure,
                "RetentionRejected" => ArticleWorkStressOutcome.RetentionRejected,
                _ => ArticleWorkStressOutcome.BrokerError,
            };

            Guid? requestId = null;
            if (root.TryGetProperty("requestId", out var requestIdNode)
                && requestIdNode.ValueKind == JsonValueKind.String
                && Guid.TryParse(requestIdNode.GetString(), out var parsedId))
            {
                requestId = parsedId;
            }

            var messageId = root.TryGetProperty("messageId", out var mid) && mid.ValueKind == JsonValueKind.String
                ? mid.GetString() ?? string.Empty
                : string.Empty;
            var backbone = root.TryGetProperty("backbone", out var bb) && bb.ValueKind == JsonValueKind.String
                ? bb.GetString() ?? string.Empty
                : string.Empty;
            string? fqdn = null;
            int? port = null;
            string? articleId = null;
            string? error = null;
            if (outcome == ArticleWorkStressOutcome.Success)
            {
                fqdn = root.GetProperty("fqdn").GetString();
                port = root.GetProperty("listenerPort").GetInt32();
                articleId = root.GetProperty("articleId").GetString();
            }
            else if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
            {
                error = err.GetString();
            }

            return new ArticleWorkReply(outcome, backbone, messageId, requestId, fqdn, port, articleId, error);
        }

        /// <summary>
        /// Bounded drain on broker-outstanding work, then closes channels and cancels remaining waiters.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var drainDeadline = Stopwatch.GetTimestamp()
                + (long)(_disposeDrainTimeout.TotalSeconds * Stopwatch.Frequency);
            while (Volatile.Read(ref _brokerOutstandingCount) > 0
                   && Stopwatch.GetTimestamp() < drainDeadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }

            var unresolved = Volatile.Read(ref _brokerOutstandingCount);
            if (unresolved > 0)
            {
                Interlocked.Exchange(ref _unresolvedAtDispose, unresolved);
                Console.Error.WriteLine(
                    $"ArticleWorkStressClient dispose drain timed out with {unresolved} broker-outstanding request(s); closing exclusive ReplyTo may leave work unroutable until production terminal settlement.");
            }

            foreach (var pending in _pending)
            {
                pending.Value.Waiter.TrySetCanceled();
            }

            _pending.Clear();
            Interlocked.Exchange(ref _waitingCount, 0);
            Interlocked.Exchange(ref _brokerOutstandingCount, 0);

            await CloseIgnoreFaultsAsync(_consumeChannel).ConfigureAwait(false);
            await CloseIgnoreFaultsAsync(_publishChannel).ConfigureAwait(false);
            await CloseIgnoreFaultsAsync(_connection).ConfigureAwait(false);

            await _consumeChannel.DisposeAsync().ConfigureAwait(false);
            await _publishChannel.DisposeAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>Best-effort channel close during dispose; transport faults are ignored.</summary>
        private static async Task CloseIgnoreFaultsAsync(IChannel channel)
        {
            try
            {
                await channel.CloseAsync().ConfigureAwait(false);
            }
            catch (AlreadyClosedException)
            {
                // ignored on dispose
            }
            catch (OperationInterruptedException)
            {
                // ignored on dispose
            }
            catch (IOException)
            {
                // ignored on dispose
            }
            catch (ObjectDisposedException)
            {
                // ignored on dispose
            }
            catch (InvalidOperationException)
            {
                // ignored on dispose
            }
            catch (TimeoutException)
            {
                // ignored on dispose
            }
            catch (OperationCanceledException)
            {
                // ignored on dispose
            }
        }

        /// <summary>Best-effort connection close during dispose; transport faults are ignored.</summary>
        private static async Task CloseIgnoreFaultsAsync(IConnection connection)
        {
            try
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
            catch (AlreadyClosedException)
            {
                // ignored on dispose
            }
            catch (OperationInterruptedException)
            {
                // ignored on dispose
            }
            catch (IOException)
            {
                // ignored on dispose
            }
            catch (ObjectDisposedException)
            {
                // ignored on dispose
            }
            catch (InvalidOperationException)
            {
                // ignored on dispose
            }
            catch (TimeoutException)
            {
                // ignored on dispose
            }
            catch (OperationCanceledException)
            {
                // ignored on dispose
            }
        }

        /// <summary>Per-correlation waiter state for one in-flight RPC.</summary>
        private sealed class PendingRpc(TaskCompletionSource<ArticleWorkReply> waiter)
        {
            /// <summary>Task completed when a reply arrives or parse fault is synthesized.</summary>
            public TaskCompletionSource<ArticleWorkReply> Waiter { get; } = waiter;

            /// <summary>1 while a caller is awaiting <see cref="Waiter"/>; 0 after timeout or completion.</summary>
            public int Waiting = 1;
        }
    }

    /// <summary>One correlated Article Work reply. Does not carry article bytes.</summary>
    public sealed record ArticleWorkReply(
        ArticleWorkStressOutcome Outcome,
        string Backbone,
        string MessageId,
        Guid? RequestId,
        string? Fqdn,
        int? ListenerPort,
        string? ArticleIdHex,
        string? Error)
    {
        /// <summary>AMQP correlation id used to match the reply.</summary>
        public string? CorrelationId { get; init; }

        /// <summary>
        /// Milliseconds from publish-completed to reply-received (aggregate BackFiller path).
        /// Not an isolated upstream fetch duration.
        /// </summary>
        public long LatencyMilliseconds { get; init; }

        /// <summary>Milliseconds for the local BasicPublishAsync call.</summary>
        public long PublishMilliseconds { get; init; }

        /// <summary><see cref="Stopwatch"/> timestamp when publish began.</summary>
        public long PublishStartedTimestamp { get; init; }

        /// <summary><see cref="Stopwatch"/> timestamp when BasicPublish completed.</summary>
        public long PublishCompletedTimestamp { get; init; }

        /// <summary><see cref="Stopwatch"/> timestamp when the reply was received (or timeout path taken).</summary>
        public long ReplyReceivedTimestamp { get; init; }
    }
}
