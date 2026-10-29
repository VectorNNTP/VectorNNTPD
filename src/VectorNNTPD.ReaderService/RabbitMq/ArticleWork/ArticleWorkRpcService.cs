using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.ReaderService.RabbitMq.Management;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.ReaderService.RabbitMq.ArticleWork
{
    /// <summary>
    /// NNTPD-owned article-work RPC session: shared reply consumer, generation-scoped channels,
    /// and sequential Backfill Scheduler orchestration.
    /// </summary>
    /// <remarks>
    /// <see cref="RabbitMqService"/> remains the sole TCP connection owner. This service
    /// opens caller-owned channels, declares an exclusive auto-delete reply queue, and
    /// rebuilds that session after <see cref="IRabbitMqService.ConnectionReplaced"/>.
    /// Outstanding application operations survive generation replacement in process memory;
    /// a stale channel cannot publish into or mutate the replacement session.
    /// BackFiller availability is discovered via the RabbitMQ Management HTTP API
    /// (eventually consistent ~5s snapshot), not via AMQP passive queue declares.
    /// </remarks>
    internal sealed class ArticleWorkRpcService : IApplicationService, IArticleWorkRpcClient, IArticleWorkRpcPublisher
    {
        private readonly IRabbitMqService _rabbitMq;
        private readonly IOptions<NntpdOptions> _nntpdOptions;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<ArticleWorkRpcService> _logger;
        private readonly ArticleWorkRpcResponseRouter _router;
        private readonly BackfillConsumerAvailabilityService _availability;
        private readonly ArticleWorkRpcClient _client;
        private readonly SemaphoreSlim _publishGate = new(1, 1);
        private readonly object _sessionGate = new();
        private readonly Guid _instanceId = Guid.NewGuid();
        private CancellationTokenSource? _shutdownCts;
        private RpcSession? _session;
        private int _started;

        /// <summary>Initializes a new article-work RPC service.</summary>
        public ArticleWorkRpcService(
            IRabbitMqService rabbitMq,
            IOptions<NntpdOptions> nntpdOptions,
            IRabbitMqManagementQueueInventory managementInventory,
            ILogger<ArticleWorkRpcService> logger,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(rabbitMq);
            ArgumentNullException.ThrowIfNull(nntpdOptions);
            ArgumentNullException.ThrowIfNull(managementInventory);
            ArgumentNullException.ThrowIfNull(logger);
            _rabbitMq = rabbitMq;
            _nntpdOptions = nntpdOptions;
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _router = new ArticleWorkRpcResponseRouter(logger);
            _availability = new BackfillConsumerAvailabilityService(managementInventory, logger, _timeProvider);
            _client = new ArticleWorkRpcClient(
                this,
                _router,
                _availability,
                new WeightedBackboneSelector(),
                _timeProvider,
                logger,
                () => _rabbitMq.ConnectionGeneration);
        }

        /// <inheritdoc />
        public string Name => "RabbitMQArticleWorkRpc";

        /// <inheritdoc />
        public Task? Execution => null;

        /// <inheritdoc />
        public string ReplyTo
        {
            get
            {
                lock (_sessionGate)
                {
                    return _session?.ReplyTo
                        ?? throw new InvalidOperationException("Article-work RPC reply queue is not ready.");
                }
            }
        }

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            try
            {
                _shutdownCts = new CancellationTokenSource();
                _rabbitMq.ConnectionReplaced += OnConnectionReplaced;
                await AttachCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
                await _availability.RefreshNowAsync(cancellationToken).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch
            {
                await StopCoreAsync().ConfigureAwait(false);
                Interlocked.Exchange(ref _started, 0);
                throw;
            }
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => StopCoreAsync();

        /// <inheritdoc />
        public async Task<ArticleWorkRpcResult> LookupByMessageIdAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _started) == 0 || _shutdownCts is null)
            {
                throw new InvalidOperationException("Article-work RPC is not started.");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
            return await _client.LookupByMessageIdAsync(messageId, linked.Token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task PublishAsync(
            string exchange,
            string routingKey,
            Guid requestId,
            string correlationId,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
            ArgumentNullException.ThrowIfNull(routingKey);
            if (requestId == Guid.Empty)
            {
                throw new ArgumentException("Article-work RPC requires a non-empty RequestId.", nameof(requestId));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

            RpcSession session;
            lock (_sessionGate)
            {
                session = _session ?? throw new InvalidOperationException("Article-work RPC session is not ready.");
            }

            if (!_rabbitMq.TryGetCurrent(out var handle)
                || !handle.IsCurrent
                || handle.Generation != session.Generation)
            {
                throw new InvalidOperationException(
                    $"Article-work RPC publication failed: connection generation {session.Generation} is not current.");
            }

            await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_sessionGate)
                {
                    if (!ReferenceEquals(_session, session))
                    {
                        throw new InvalidOperationException(
                            "Article-work RPC publication failed: session was replaced.");
                    }
                }

                await session.PublishChannel
                    .PublishAsync(
                        exchange,
                        routingKey,
                        correlationId,
                        requestId.ToString("D"),
                        session.ReplyTo,
                        ArticleWorkWireProtocol.JsonContentType,
                        expiration: string.Empty,
                        body,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _publishGate.Release();
            }
        }

        internal int OutstandingCorrelations => _router.OutstandingCount;

        internal string? CurrentReplyTo
        {
            get
            {
                lock (_sessionGate)
                {
                    return _session?.ReplyTo;
                }
            }
        }

        internal long? CurrentSessionGeneration
        {
            get
            {
                lock (_sessionGate)
                {
                    return _session?.Generation;
                }
            }
        }

        private async Task AttachCurrentSessionAsync(CancellationToken cancellationToken)
        {
            if (!_rabbitMq.TryGetCurrent(out var handle) || !handle.IsCurrent)
            {
                ArticleWorkRpcLogMessages.NotReady(_logger);
                throw new InvalidOperationException("RabbitMQ connection is not ready for article-work RPC.");
            }

            var generation = handle.Generation;
            IRabbitMqRpcChannel? publishChannel = null;
            IRabbitMqRpcChannel? consumeChannel = null;
            try
            {
                publishChannel = await handle.Connection
                    .CreateRpcChannelAsync(generation, cancellationToken)
                    .ConfigureAwait(false);
                consumeChannel = await handle.Connection
                    .CreateRpcChannelAsync(generation, cancellationToken)
                    .ConfigureAwait(false);

                var replyTo = CreateReplyQueueName();
                await consumeChannel.QueueDeclareAsync(
                        replyTo,
                        durable: false,
                        exclusive: true,
                        autoDelete: true,
                        arguments: null,
                        cancellationToken)
                    .ConfigureAwait(false);

                _ = await consumeChannel
                    .ConsumeAsync(replyTo, OnDeliveryAsync, cancellationToken)
                    .ConfigureAwait(false);

                var session = new RpcSession(generation, replyTo, publishChannel, consumeChannel);
                RpcSession? previous;
                lock (_sessionGate)
                {
                    previous = _session;
                    _session = session;
                }

                if (previous is not null)
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                    ArticleWorkRpcLogMessages.SessionReplaced(_logger, replyTo, generation);
                }
                else
                {
                    ArticleWorkRpcLogMessages.Ready(_logger, replyTo, generation);
                }
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ArticleWorkRpcLogMessages.SessionAttachFailed(_logger, ex, generation);
                if (publishChannel is not null)
                {
                    await publishChannel.DisposeAsync().ConfigureAwait(false);
                }

                if (consumeChannel is not null)
                {
                    await consumeChannel.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
        }

        private async Task OnDeliveryAsync(RabbitMqRpcDelivery delivery)
        {
            try
            {
                _router.Dispatch(
                    delivery.CorrelationId,
                    delivery.Body,
                    delivery.RequestId,
                    delivery.Generation);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                ArticleWorkRpcLogMessages.ConsumerCallbackFailed(_logger, ex);
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private void OnConnectionReplaced(object? sender, RabbitMqConnectionReplacedEventArgs eventArgs)
        {
            if (_shutdownCts is null || _shutdownCts.IsCancellationRequested || !eventArgs.IsReplacement)
            {
                return;
            }

            _ = AttachReplacementAsync();
        }

        private async Task AttachReplacementAsync()
        {
            try
            {
                await AttachCurrentSessionAsync(_shutdownCts?.Token ?? CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                ArticleWorkRpcLogMessages.SessionAttachFailed(_logger, ex, _rabbitMq.ConnectionGeneration);
            }
        }

        private async Task StopCoreAsync()
        {
            _rabbitMq.ConnectionReplaced -= OnConnectionReplaced;
            if (_shutdownCts is not null)
            {
                await _shutdownCts.CancelAsync().ConfigureAwait(false);
            }

            _router.CancelAll();

            RpcSession? session;
            lock (_sessionGate)
            {
                session = _session;
                _session = null;
            }

            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            await _availability.DisposeAsync().ConfigureAwait(false);

            _shutdownCts?.Dispose();
            _shutdownCts = null;
            Interlocked.Exchange(ref _started, 0);
        }

        private string CreateReplyQueueName()
        {
            var serverId = _nntpdOptions.Value.ServerId ?? 0;
            return RabbitMqTopologyNames.Normalize($"nntpd.{serverId:00}.{_instanceId:N}.rpc");
        }

        private sealed class RpcSession : IAsyncDisposable
        {
            internal RpcSession(
                long generation,
                string replyTo,
                IRabbitMqRpcChannel publishChannel,
                IRabbitMqRpcChannel consumeChannel)
            {
                Generation = generation;
                ReplyTo = replyTo;
                PublishChannel = publishChannel;
                ConsumeChannel = consumeChannel;
            }

            internal long Generation { get; }

            internal string ReplyTo { get; }

            internal IRabbitMqRpcChannel PublishChannel { get; }

            internal IRabbitMqRpcChannel ConsumeChannel { get; }

            public async ValueTask DisposeAsync()
            {
                await PublishChannel.DisposeAsync().ConfigureAwait(false);
                await ConsumeChannel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
