using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.TransitService.RabbitMq;

namespace VectorNNTPD.TransitService.Storage
{
    /// <summary>
    /// Consumes StorageServer advertisements from <c>cache.broadcast</c> into
    /// <see cref="IStorageServerRegistry"/>.
    /// </summary>
    /// <remarks>
    /// Declares an ephemeral per-instance queue <c>cache.&lt;nntpd-fqdn&gt;</c> (non-durable,
    /// exclusive, auto-delete) bound to the fanout exchange so every NNTPD receives every
    /// advertisement without competing consumers.
    /// </remarks>
    internal sealed class StorageServerFleetConsumerService : IApplicationService
    {
        private readonly IRabbitMqService _rabbitMq;
        private readonly IOptions<NntpdOptions> _nntpdOptions;
        private readonly IStorageServerRegistry _registry;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<StorageServerFleetConsumerService> _logger;
        private readonly object _sessionGate = new();
        private CancellationTokenSource? _shutdownCts;
        private ConsumeSession? _session;
        private int _started;

        /// <summary>Initializes a new fleet advertisement consumer.</summary>
        public StorageServerFleetConsumerService(
            IRabbitMqService rabbitMq,
            IOptions<NntpdOptions> nntpdOptions,
            IStorageServerRegistry registry,
            ILogger<StorageServerFleetConsumerService> logger,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(rabbitMq);
            ArgumentNullException.ThrowIfNull(nntpdOptions);
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(logger);
            _rabbitMq = rabbitMq;
            _nntpdOptions = nntpdOptions;
            _registry = registry;
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <inheritdoc />
        public string Name => "StorageServerFleetConsumer";

        /// <inheritdoc />
        public Task? Execution => null;

        /// <summary>Gets the current instance queue name when attached (tests).</summary>
        internal string? CurrentQueueName
        {
            get
            {
                lock (_sessionGate)
                {
                    return _session?.QueueName;
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

        private async Task AttachCurrentSessionAsync(CancellationToken cancellationToken)
        {
            if (!_rabbitMq.TryGetCurrent(out var handle) || !handle.IsCurrent)
            {
                StorageServerFleetLogMessages.ConnectionNotReady(_logger);
                throw new InvalidOperationException("RabbitMQ connection is not ready for StorageServer fleet consume.");
            }

            var fqdn = _nntpdOptions.Value.Fqdn;
            if (string.IsNullOrWhiteSpace(fqdn))
            {
                throw new InvalidOperationException("StorageServer fleet consume requires the generated NNTPD FQDN.");
            }

            var queueName = CacheBroadcastTopology.BuildInstanceQueueName(fqdn);
            var generation = handle.Generation;
            IRabbitMqManualAckChannel? channel = null;
            try
            {
                channel = await handle.Connection
                    .CreateManualAckChannelAsync(generation, cancellationToken)
                    .ConfigureAwait(false);

                await channel.ExchangeDeclareAsync(
                        CacheBroadcastTopology.ExchangeName,
                        CacheBroadcastTopology.ExchangeTypeName,
                        CacheBroadcastTopology.ExchangeDurable,
                        CacheBroadcastTopology.ExchangeAutoDelete,
                        arguments: null,
                        cancellationToken)
                    .ConfigureAwait(false);

                await channel.QueueDeclareAsync(
                        queueName,
                        durable: false,
                        exclusive: true,
                        autoDelete: true,
                        arguments: null,
                        cancellationToken)
                    .ConfigureAwait(false);

                await channel.QueueBindAsync(
                        queueName,
                        CacheBroadcastTopology.ExchangeName,
                        routingKey: string.Empty,
                        arguments: null,
                        cancellationToken)
                    .ConfigureAwait(false);

                var consumerTag = await channel
                    .BasicConsumeAsync(queueName, prefetchCount: 64, OnDeliveryAsync, cancellationToken)
                    .ConfigureAwait(false);

                var session = new ConsumeSession(generation, queueName, consumerTag, channel);
                ConsumeSession? previous;
                lock (_sessionGate)
                {
                    previous = _session;
                    _session = session;
                }

                if (previous is not null)
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                    StorageServerFleetLogMessages.SessionReplaced(_logger, queueName, generation);
                }
                else
                {
                    StorageServerFleetLogMessages.Ready(_logger, queueName, generation);
                }
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StorageServerFleetLogMessages.SessionAttachFailed(_logger, ex, generation);
                if (channel is not null)
                {
                    await channel.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
        }

        private async Task OnDeliveryAsync(RabbitMqManualAckDelivery delivery)
        {
            ConsumeSession? session;
            lock (_sessionGate)
            {
                session = _session;
            }

            if (session is null || delivery.Generation != session.Generation)
            {
                return;
            }

            try
            {
                if (!string.Equals(
                        delivery.ContentType,
                        StorageServerAdvertisementWireProtocol.JsonContentType,
                        StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(delivery.ContentType))
                {
                    StorageServerFleetLogMessages.PayloadRejected(_logger, "Unexpected content type.");
                    await session.Channel.BasicAckAsync(delivery.DeliveryTag, CancellationToken.None)
                        .ConfigureAwait(false);
                    return;
                }

                var receivedAt = _timeProvider.GetUtcNow();
                if (StorageServerLifecycleWireProtocol.TryParseV1(
                        delivery.Body.Span,
                        out var announcement,
                        out var lifecycleReason,
                        out var lifecycleRecognized))
                {
                    if (announcement is null)
                    {
                        StorageServerFleetLogMessages.PayloadRejected(_logger, lifecycleReason);
                    }
                    else
                    {
                        _registry.ApplyLifecycle(announcement, receivedAt);
                    }

                    await session.Channel.BasicAckAsync(delivery.DeliveryTag, CancellationToken.None)
                        .ConfigureAwait(false);
                    return;
                }

                if (lifecycleRecognized)
                {
                    StorageServerFleetLogMessages.PayloadRejected(_logger, lifecycleReason);
                    await session.Channel.BasicAckAsync(delivery.DeliveryTag, CancellationToken.None)
                        .ConfigureAwait(false);
                    return;
                }

                if (!StorageServerAdvertisementWireProtocol.TryParseV1(
                        delivery.Body.Span,
                        out var advertisement,
                        out var reason)
                    || advertisement is null)
                {
                    StorageServerFleetLogMessages.PayloadRejected(_logger, reason);
                    await session.Channel.BasicAckAsync(delivery.DeliveryTag, CancellationToken.None)
                        .ConfigureAwait(false);
                    return;
                }

                _registry.ApplyAdvertisement(advertisement, receivedAt);
                await session.Channel.BasicAckAsync(delivery.DeliveryTag, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                StorageServerFleetLogMessages.DeliveryFailed(_logger, ex);
                try
                {
                    await session.Channel
                        .BasicNackAsync(delivery.DeliveryTag, requeue: false, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception nackEx)
                {
                    StorageServerFleetLogMessages.DeliveryFailed(_logger, nackEx);
                }
            }
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
                StorageServerFleetLogMessages.SessionAttachFailed(_logger, ex, _rabbitMq.ConnectionGeneration);
            }
        }

        private async Task StopCoreAsync()
        {
            _rabbitMq.ConnectionReplaced -= OnConnectionReplaced;
            if (_shutdownCts is not null)
            {
                await _shutdownCts.CancelAsync().ConfigureAwait(false);
            }

            ConsumeSession? session;
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
            StorageServerFleetLogMessages.Stopped(_logger);
        }

        private sealed class ConsumeSession : IAsyncDisposable
        {
            internal ConsumeSession(
                long generation,
                string queueName,
                string consumerTag,
                IRabbitMqManualAckChannel channel)
            {
                Generation = generation;
                QueueName = queueName;
                ConsumerTag = consumerTag;
                Channel = channel;
            }

            internal long Generation { get; }

            internal string QueueName { get; }

            internal string ConsumerTag { get; }

            internal IRabbitMqManualAckChannel Channel { get; }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    if (Channel.IsOpen)
                    {
                        await Channel.BasicCancelAsync(ConsumerTag, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                {
                    _ = ignored; // observed; intentionally ignored at this best-effort boundary
                    // Channel may already be closed by connection loss.
                }

                await Channel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
