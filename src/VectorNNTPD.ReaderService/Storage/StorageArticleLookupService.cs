using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.ReaderService.RabbitMq;

namespace VectorNNTPD.ReaderService.Storage
{
    /// <summary>
    /// NNTPD-owned StorageServer fleet article-presence lookup: publish to <c>cache.requests</c>,
    /// consume replies on an ephemeral reply queue, and return the first valid positive immediately.
    /// </summary>
    /// <remarks>
    /// The registration stays until the existing lookup timeout so one later distinct positive
    /// can be retained. Independent of ArticleWork RPC. Negative responses are not expected.
    /// When <see cref="IStorageServerRegistry"/> has no entries, no <c>cache.requests</c>
    /// publication is made and the existing miss result is returned so ARTICLE/HEAD/BODY/STAT
    /// continue to ArticleWork. An empty registry is not an article-not-found result.
    /// A registry that still contains any entry, including a stale or draining one, uses the
    /// existing publication path.
    /// </remarks>
    internal sealed class StorageArticleLookupService : IApplicationService, IStorageArticleLookupClient
    {
        private readonly IRabbitMqService _rabbitMq;
        private readonly IOptions<NntpdOptions> _nntpdOptions;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<StorageArticleLookupService> _logger;
        private readonly IStorageServerRegistry? _storageServers;
        private readonly StorageArticleLookupResponseRouter _router = new();
        private readonly SemaphoreSlim _publishGate = new(1, 1);
        private readonly object _sessionGate = new();
        private readonly Guid _instanceId = Guid.NewGuid();
        private CancellationTokenSource? _shutdownCts;
        private LookupSession? _session;
        private int _started;

        /// <summary>Initializes a new storage article lookup service.</summary>
        public StorageArticleLookupService(
            IRabbitMqService rabbitMq,
            IOptions<NntpdOptions> nntpdOptions,
            ILogger<StorageArticleLookupService> logger,
            TimeProvider? timeProvider = null,
            IStorageServerRegistry? storageServers = null)
        {
            ArgumentNullException.ThrowIfNull(rabbitMq);
            ArgumentNullException.ThrowIfNull(nntpdOptions);
            ArgumentNullException.ThrowIfNull(logger);
            _rabbitMq = rabbitMq;
            _nntpdOptions = nntpdOptions;
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _storageServers = storageServers;
        }

        /// <inheritdoc />
        public string Name => "StorageArticleLookup";

        /// <inheritdoc />
        public Task? Execution => null;

        /// <summary>Gets the current reply queue name when attached (tests).</summary>
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

        /// <summary>Gets outstanding correlations (tests).</summary>
        internal int OutstandingCorrelations => _router.OutstandingCount;

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
        public async Task<StorageArticleLookupResult> LookupAsync(
            ArticleId articleId,
            CancellationToken cancellationToken)
        {
            if (_storageServers is not null && _storageServers.Snapshot().Count == 0)
            {
                StorageArticleLookupLogMessages.SkippedEmptyRegistry(_logger, articleId.ToLowerHexString());
                return StorageArticleLookupResult.NotFound(
                    Guid.NewGuid(),
                    articleId,
                    "Storage article lookup timed out with no positive response.");
            }

            if (Volatile.Read(ref _started) == 0 || _shutdownCts is null)
            {
                throw new InvalidOperationException("Storage article lookup is not started.");
            }

            var requestId = Guid.NewGuid();
            var correlationId = Guid.NewGuid().ToString("D");
            var operation = new StorageArticleLookupOperation(requestId, articleId);
            // codeql[cs/missed-using-statement] Linked CTS ownership transfers to ObserveLookupWindowAsync when the lookup window starts; disposed there or in the !windowStarted finally below. A using around LookupAsync would cancel the background window early.
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
            bool registered;
            try
            {
                // codeql[cs/dispose-not-called-on-throw] linked is disposed in catch below, on !registered, or by ObserveLookupWindowAsync after ownership transfer; method-scoped using would cancel the window early.
                registered = _router.TryRegister(correlationId, operation);
            }
            catch
            {
                linked.Dispose();
                throw;
            }

            if (!registered)
            {
                // codeql[cs/dispose-not-called-on-throw] Explicit dispose on registration failure; throw path disposed in catch. Ownership otherwise transfers to ObserveLookupWindowAsync.
                linked.Dispose();
                return StorageArticleLookupResult.NotFound(requestId, articleId, "Lookup could not be registered.");
            }

            var windowStarted = false;
            try
            {
                await PublishRequestAsync(requestId, correlationId, articleId, linked.Token).ConfigureAwait(false);
                windowStarted = true;
                _ = ObserveLookupWindowAsync(operation, correlationId, linked);
                await operation.FirstReady.WaitAsync(linked.Token).ConfigureAwait(false);
                if (operation.TryGetFirst(out var first))
                {
                    return new StorageArticleLookupResult(
                        StorageArticleLookupOutcome.Found,
                        requestId,
                        articleId,
                        first.ServerId,
                        first.Fqdn,
                        first.VatpPort,
                        Error: null)
                    {
                        Alternates = operation,
                    };
                }

                return StorageArticleLookupResult.NotFound(
                    requestId,
                    articleId,
                    "Storage article lookup timed out with no positive response.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                operation.CloseWindow();
                throw;
            }
            catch (OperationCanceledException)
            {
                operation.CloseWindow();
                return StorageArticleLookupResult.NotFound(
                    requestId,
                    articleId,
                    "Storage article lookup was canceled.");
            }
            finally
            {
                if (!windowStarted)
                {
                    _router.Unregister(correlationId);
                    linked.Dispose();
                }
            }
        }

        private async Task ObserveLookupWindowAsync(
            StorageArticleLookupOperation operation,
            string correlationId,
            CancellationTokenSource linked)
        {
            using (linked)
            {
                try
                {
                    await Task.Delay(CacheFleetTopology.LookupTimeout, _timeProvider, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }
                finally
                {
                    operation.CloseWindow();
                    _router.Unregister(correlationId);
                }
            }
        }

        /// <summary>Completes a lookup from a parsed response body (tests).</summary>
        internal bool TryDispatchResponse(string correlationId, StorageArticleLookupResponse response) =>
            _router.TryComplete(correlationId, response);

        private async Task PublishRequestAsync(
            Guid requestId,
            string correlationId,
            ArticleId articleId,
            CancellationToken cancellationToken)
        {
            LookupSession session;
            lock (_sessionGate)
            {
                session = _session ?? throw new InvalidOperationException("Storage article lookup session is not ready.");
            }

            if (!_rabbitMq.TryGetCurrent(out var handle)
                || !handle.IsCurrent
                || handle.Generation != session.Generation)
            {
                throw new InvalidOperationException(
                    $"Storage article lookup publication failed: connection generation {session.Generation} is not current.");
            }

            var body = StorageArticleLookupWireProtocol.SerializeRequestV1(
                new StorageArticleLookupRequest(
                    StorageArticleLookupWireProtocol.CurrentVersion,
                    requestId,
                    articleId));

            await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_sessionGate)
                {
                    if (!ReferenceEquals(_session, session))
                    {
                        throw new InvalidOperationException("Storage article lookup session was replaced.");
                    }
                }

                await session.PublishChannel
                    .PublishAsync(
                        CacheRequestsTopology.ExchangeName,
                        routingKey: string.Empty,
                        correlationId,
                        requestId.ToString("D"),
                        session.ReplyTo,
                        StorageArticleLookupWireProtocol.JsonContentType,
                        CacheRequestsTopology.ExpirationMilliseconds,
                        body,
                        cancellationToken)
                    .ConfigureAwait(false);

                StorageArticleLookupLogMessages.Published(
                    _logger,
                    requestId,
                    correlationId,
                    articleId.ToLowerHexString(),
                    session.Generation);
            }
            finally
            {
                _publishGate.Release();
            }
        }

        private async Task AttachCurrentSessionAsync(CancellationToken cancellationToken)
        {
            if (!_rabbitMq.TryGetCurrent(out var handle) || !handle.IsCurrent)
            {
                StorageArticleLookupLogMessages.ConnectionNotReady(_logger);
                throw new InvalidOperationException("RabbitMQ connection is not ready for storage article lookup.");
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

                var session = new LookupSession(generation, replyTo, publishChannel, consumeChannel);
                LookupSession? previous;
                lock (_sessionGate)
                {
                    previous = _session;
                    _session = session;
                }

                if (previous is not null)
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                    StorageArticleLookupLogMessages.SessionReplaced(_logger, replyTo, generation);
                }
                else
                {
                    StorageArticleLookupLogMessages.Ready(_logger, replyTo, generation);
                }
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StorageArticleLookupLogMessages.SessionAttachFailed(_logger, ex, generation);
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
                if (!StorageArticleLookupWireProtocol.TryParseResponseV1(
                        delivery.Body.Span,
                        out var response,
                        out var reason)
                    || response is null)
                {
                    StorageArticleLookupLogMessages.ResponseIgnored(_logger, delivery.CorrelationId ?? string.Empty, reason);
                    return;
                }

                if (!_router.TryComplete(delivery.CorrelationId, response))
                {
                    StorageArticleLookupLogMessages.ResponseIgnored(
                        _logger,
                        delivery.CorrelationId ?? string.Empty,
                        "Unknown, duplicate, or mismatched correlation.");
                }
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                StorageArticleLookupLogMessages.DeliveryFailed(_logger, ex);
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
                StorageArticleLookupLogMessages.SessionAttachFailed(_logger, ex, _rabbitMq.ConnectionGeneration);
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

            LookupSession? session;
            lock (_sessionGate)
            {
                session = _session;
                _session = null;
            }

            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            _shutdownCts?.Dispose();
            _shutdownCts = null;
            Interlocked.Exchange(ref _started, 0);
            StorageArticleLookupLogMessages.Stopped(_logger);
        }

        private string CreateReplyQueueName()
        {
            var serverId = _nntpdOptions.Value.ServerId ?? 0;
            return RabbitMqTopologyNames.Normalize($"nntpd.{serverId:00}.{_instanceId:N}.cache.lookup");
        }

        private sealed class LookupSession : IAsyncDisposable
        {
            internal LookupSession(
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
