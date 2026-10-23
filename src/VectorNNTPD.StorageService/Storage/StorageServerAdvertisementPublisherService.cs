using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Configuration;

namespace VectorNNTPD.StorageService.Storage
{

    /// <summary>
    /// Publishes StorageServer capacity advertisements to <c>cache.broadcast</c> once per second.
    /// </summary>
    /// <remarks>
    /// Declares the durable fanout exchange so StorageServer can start before NNTPD.
    /// Uses the shared <see cref="RabbitMqService"/> connection; does not own TCP lifecycle.
    /// Heartbeat publication must not block clean shutdown.
    /// The periodic advertisement is the readiness signal. It starts only after this service starts,
    /// which is after the VATP listener is accepting. Stop publishes one transient Draining
    /// announcement after the loop has stopped and before the channel is disposed. A failed Draining
    /// publication is logged and does not block shutdown past <see cref="LifecycleAnnouncementTimeout"/>.
    /// </remarks>
    public sealed class StorageServerAdvertisementPublisherService : IApplicationService
    {
        /// <summary>
        /// Bound for one lifecycle publication. Shutdown continues when this elapses.
        /// </summary>
        private static readonly TimeSpan LifecycleAnnouncementTimeout = TimeSpan.FromSeconds(2);

        private readonly IRabbitMqService _rabbitMq;
        private readonly StorageServerRuntimeOptionsSource? _runtimeSource;
        private StorageServerRuntimeOptions? _runtime;
        private readonly IStorageCapacityReader _capacityReader;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<StorageServerAdvertisementPublisherService> _logger;
        private readonly SemaphoreSlim _publishGate = new(1, 1);
        private CancellationTokenSource? _shutdownCts;
        private Task? _loop;
        private IRabbitMqPublishChannel? _channel;
        private int _started;
        private int _stopAdvertisements;
        private int _lifecycleArmed;
        private int _drainingPublished;
        private long _lastTimestampTicks;

        /// <summary>Initializes a new advertisement publisher for tests that already have a runtime snapshot.</summary>
        internal StorageServerAdvertisementPublisherService(
            IRabbitMqService rabbitMq,
            StorageServerRuntimeOptions runtime,
            IStorageCapacityReader capacityReader,
            ILogger<StorageServerAdvertisementPublisherService> logger,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(rabbitMq);
            ArgumentNullException.ThrowIfNull(runtime);
            ArgumentNullException.ThrowIfNull(capacityReader);
            ArgumentNullException.ThrowIfNull(logger);
            _rabbitMq = rabbitMq;
            _runtime = runtime;
            _capacityReader = capacityReader;
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <summary>
        /// Initializes the host publisher. Runtime options are read when <see cref="StartAsync"/> runs,
        /// after <c>nntpsharedconfig</c> has been published.
        /// </summary>
        public StorageServerAdvertisementPublisherService(
            IRabbitMqService rabbitMq,
            StorageServerRuntimeOptionsSource runtimeSource,
            IStorageCapacityReader capacityReader,
            ILogger<StorageServerAdvertisementPublisherService> logger,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(rabbitMq);
            ArgumentNullException.ThrowIfNull(runtimeSource);
            ArgumentNullException.ThrowIfNull(capacityReader);
            ArgumentNullException.ThrowIfNull(logger);
            _rabbitMq = rabbitMq;
            _runtimeSource = runtimeSource;
            _capacityReader = capacityReader;
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        private StorageServerRuntimeOptions Runtime =>
            _runtime ?? throw new InvalidOperationException("StorageServer runtime options have not been published.");

        /// <inheritdoc />
        public string Name => "StorageServerAdvertisementPublisher";

        /// <inheritdoc />
        public Task? Execution => _loop;

        /// <summary>Gets how many advertisements were published successfully (tests).</summary>
        internal long PublishedCount => Volatile.Read(ref _publishedCount);

        private long _publishedCount;

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            Volatile.Write(ref _stopAdvertisements, 0);
            Interlocked.Exchange(ref _drainingPublished, 0);

            try
            {
                _runtime ??= _runtimeSource?.Current
                    ?? throw new InvalidOperationException("StorageServer runtime options have not been published.");

                _shutdownCts = new CancellationTokenSource();
                await EnsureExchangeAsync(cancellationToken).ConfigureAwait(false);
                await EnsurePublishChannelAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _lifecycleArmed, 1);
                _loop = RunPublishLoopAsync(_shutdownCts.Token);
                StorageServerAdvertisementLogMessages.Started(_logger, CacheFleetTopology.BroadcastExchangeName);
            }
            catch
            {
                await StopCoreAsync().ConfigureAwait(false);
                Interlocked.Exchange(ref _started, 0);
                throw;
            }
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => StopCoreAsync();

        /// <summary>Publishes one advertisement immediately (tests).</summary>
        internal Task PublishOnceAsync(CancellationToken cancellationToken) =>
            PublishAdvertisementAsync(cancellationToken);

        private async Task RunPublishLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await PublishAdvertisementAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                    {
                        StorageServerAdvertisementLogMessages.PublishFailed(_logger, ex);
                    }

                    try
                    {
                        await Task.Delay(CacheFleetTopology.AdvertisementInterval, _timeProvider, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                ExpectedException.Ignore(ex);
            }
        }

        private async Task PublishAdvertisementAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _stopAdvertisements) == 1)
            {
                return;
            }

            var capacity = _capacityReader.Read();
            var advertisement = new StorageServerAdvertisement(
                StorageServerAdvertisementWireProtocol.CurrentVersion,
                Runtime.ServerId,
                Runtime.Fqdn,
                capacity.TotalBytes,
                capacity.UsedBytes,
                capacity.AvailableBytes,
                NextTimestamp(),
                Runtime.BindPortTls);

            var body = StorageServerAdvertisementWireProtocol.SerializeV1(advertisement);
            await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var channel = await EnsurePublishChannelAsync(cancellationToken).ConfigureAwait(false);
                await channel.PublishConfirmedAsync(
                        new RabbitMqConfirmedPublication(
                            CacheFleetTopology.BroadcastExchangeName,
                            // Fanout ignores the key. The common publisher rejects empty keys;
                            // the exchange name matches the other durable fanout publications.
                            RoutingKey: CacheFleetTopology.BroadcastExchangeName,
                            MessageId: Guid.NewGuid().ToString("D"),
                            AppId: Runtime.Fqdn,
                            CorrelationId: null,
                            ContentType: StorageServerAdvertisementWireProtocol.JsonContentType,
                            RequestIdHeader: null,
                            ExpirationMilliseconds: CacheFleetTopology.AdvertisementExpirationMilliseconds,
                            Persistent: false,
                            Mandatory: false,
                            Body: body),
                        cancellationToken)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref _publishedCount);
            }
            finally
            {
                _publishGate.Release();
            }
        }

        private async Task EnsureExchangeAsync(CancellationToken cancellationToken)
        {
            if (!_rabbitMq.TryGetCurrent(out var handle) || !handle.IsCurrent)
            {
                throw new InvalidOperationException("RabbitMQ connection is not ready for cache broadcast declaration.");
            }

            await using var channel = await handle.Connection
                .CreateTopologyChannelAsync(cancellationToken)
                .ConfigureAwait(false);
            await channel.ExchangeDeclareAsync(
                    CacheFleetTopology.BroadcastExchangeName,
                    CacheFleetTopology.BroadcastExchangeType,
                    durable: true,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<IRabbitMqPublishChannel> EnsurePublishChannelAsync(CancellationToken cancellationToken)
        {
            var existing = _channel;
            if (existing is { IsOpen: true })
            {
                return existing;
            }

            if (!_rabbitMq.TryGetCurrent(out var handle) || !handle.IsCurrent)
            {
                throw new InvalidOperationException("RabbitMQ connection is not ready for StorageServer advertisement publish.");
            }

            var channel = await handle.Connection
                .CreatePublishChannelAsync(handle.Generation, cancellationToken)
                .ConfigureAwait(false);
            var previous = Interlocked.Exchange(ref _channel, channel);
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            return channel;
        }

        private async Task PublishDrainingAsync()
        {
            if (Volatile.Read(ref _lifecycleArmed) == 0
                || Interlocked.Exchange(ref _drainingPublished, 1) == 1)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(LifecycleAnnouncementTimeout);
            try
            {
                await PublishDrainingAnnouncementAsync(timeout.Token).ConfigureAwait(false);
                StorageServerAdvertisementLogMessages.LifecycleDrainingPublished(
                    _logger,
                    Runtime.Fqdn,
                    Runtime.ServerId);
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                StorageServerAdvertisementLogMessages.LifecycleAnnouncementFailed(
                    _logger,
                    ex,
                    nameof(StorageServerLifecycleState.Draining));
            }
        }

        private async Task PublishDrainingAnnouncementAsync(CancellationToken cancellationToken)
        {
            var announcement = new StorageServerLifecycleAnnouncement(
                StorageServerLifecycleWireProtocol.CurrentVersion,
                Runtime.ServerId,
                Runtime.Fqdn,
                StorageServerLifecycleState.Draining,
                NextTimestamp(),
                Runtime.BindPortTls);
            var body = StorageServerLifecycleWireProtocol.SerializeV1(announcement);
            await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var channel = await EnsurePublishChannelAsync(cancellationToken).ConfigureAwait(false);
                await channel.PublishConfirmedAsync(
                        new RabbitMqConfirmedPublication(
                            CacheFleetTopology.BroadcastExchangeName,
                            RoutingKey: CacheFleetTopology.BroadcastExchangeName,
                            MessageId: Guid.NewGuid().ToString("D"),
                            AppId: Runtime.Fqdn,
                            CorrelationId: null,
                            ContentType: StorageServerAdvertisementWireProtocol.JsonContentType,
                            RequestIdHeader: null,
                            ExpirationMilliseconds: CacheFleetTopology.AdvertisementExpirationMilliseconds,
                            Persistent: false,
                            Mandatory: false,
                            Body: body),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _publishGate.Release();
            }
        }

        /// <summary>
        /// Returns a UTC timestamp strictly after the previous publication from this process.
        /// Peers use it to order this process's advertisements and its Draining announcement.
        /// It is not a process identity.
        /// </summary>
        private DateTimeOffset NextTimestamp()
        {
            var nowTicks = _timeProvider.GetUtcNow().ToUniversalTime().UtcTicks;
            var last = Volatile.Read(ref _lastTimestampTicks);
            var ticks = nowTicks > last ? nowTicks : last + 1;
            Volatile.Write(ref _lastTimestampTicks, ticks);
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }

        private async Task StopCoreAsync()
        {
            Volatile.Write(ref _stopAdvertisements, 1);
            if (_shutdownCts is not null)
            {
                await _shutdownCts.CancelAsync().ConfigureAwait(false);
            }

            var loop = Interlocked.Exchange(ref _loop, null);
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    ExpectedException.Ignore(ex);
                }
            }

            await PublishDrainingAsync().ConfigureAwait(false);

            var channel = Interlocked.Exchange(ref _channel, null);
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }

            _shutdownCts?.Dispose();
            _shutdownCts = null;
            Interlocked.Exchange(ref _started, 0);
            StorageServerAdvertisementLogMessages.Stopped(_logger);
        }
    }
}
