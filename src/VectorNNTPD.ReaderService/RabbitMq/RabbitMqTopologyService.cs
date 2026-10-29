using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.ReaderService.RabbitMq
{
    /// <summary>
    /// Declares NNTPD-owned RabbitMQ topology: cache fleet exchanges and OverviewDB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declares the durable <c>cache.requests</c> fanout exchange (no shared work queue),
    /// the durable <c>cache.broadcast</c> fanout exchange, and the OverviewDB ingest queue
    /// <c>overviewdb.queue</c>. Per-backbone <c>backfiller.*</c> provider topology is declared
    /// by VectorNNTP.BackFiller. Per-NNTPD broadcast queues and StorageServer request queues
    /// are ephemeral and owned by their respective consumers. Article-work and storage-lookup
    /// reply queues are exclusive auto-delete non-durable classic queues owned by their RPC
    /// services.
    /// </para>
    /// <para>
    /// Declaration uses RabbitMQ's normal idempotent declare/bind operations. Existing
    /// entities are never deleted, purged, or mutated. An incompatible existing entity
    /// fails startup.
    /// </para>
    /// </remarks>
    public sealed class RabbitMqTopologyService : IApplicationService
    {
        private readonly IRabbitMqService _rabbitMq;
        private readonly ILogger<RabbitMqTopologyService> _logger;
        private int _started;

        /// <summary>Initializes a new instance of the <see cref="RabbitMqTopologyService"/> class.</summary>
        public RabbitMqTopologyService(
            IRabbitMqService rabbitMq,
            ILogger<RabbitMqTopologyService> logger)
        {
            ArgumentNullException.ThrowIfNull(rabbitMq);
            ArgumentNullException.ThrowIfNull(logger);
            _rabbitMq = rabbitMq;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "RabbitMQTopology";

        /// <inheritdoc />
        public Task? Execution => null;

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            try
            {
                await DeclareRequiredTopologyAsync(cancellationToken).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Exchange(ref _started, 0);
                throw;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref _started, 0);
                throw;
            }
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// Declares <c>cache.requests</c>, <c>cache.broadcast</c>, and <c>overviewdb.queue</c>.
        /// </summary>
        internal async Task DeclareRequiredTopologyAsync(CancellationToken cancellationToken)
        {
            RabbitMqTopologyLogMessages.Establishing(_logger, ExchangeCount: 2);

            if (!_rabbitMq.TryGetCurrent(out var handle))
            {
                RabbitMqTopologyLogMessages.ConnectionNotReady(_logger);
                throw new InvalidOperationException("RabbitMQ connection is not ready for topology declaration.");
            }

            IRabbitMqTopologyChannel? channel = null;
            try
            {
                channel = await handle.Connection
                    .CreateTopologyChannelAsync(cancellationToken)
                    .ConfigureAwait(false);

                await DeclareFanoutExchangeAsync(
                        channel,
                        CacheRequestsTopology.ExchangeName,
                        CacheRequestsTopology.ExchangeDurable,
                        CacheRequestsTopology.ExchangeAutoDelete,
                        cancellationToken)
                    .ConfigureAwait(false);

                await DeclareFanoutExchangeAsync(
                        channel,
                        CacheBroadcastTopology.ExchangeName,
                        CacheBroadcastTopology.ExchangeDurable,
                        CacheBroadcastTopology.ExchangeAutoDelete,
                        cancellationToken)
                    .ConfigureAwait(false);

                await DeclareOverviewDbQueueAsync(channel, cancellationToken).ConfigureAwait(false);

                RabbitMqTopologyLogMessages.Established(_logger, ExchangeCount: 2, handle.Generation);
                RabbitMqTopologyLogMessages.CacheRequestsDeclared(
                    _logger,
                    CacheRequestsTopology.ExchangeName,
                    handle.Generation);
                RabbitMqTopologyLogMessages.CacheBroadcastDeclared(
                    _logger,
                    CacheBroadcastTopology.ExchangeName,
                    handle.Generation);
                RabbitMqTopologyLogMessages.OverviewQueueDeclared(_logger, OverviewDbTopology.QueueName, handle.Generation);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RabbitMqTopologyLogMessages.DeclarationFailed(
                    _logger,
                    ex,
                    CacheFleetTopology.RequestsExchangeName,
                    CacheFleetTopology.RequestsExchangeName,
                    OverviewDbTopology.QueueName);
                throw;
            }
            finally
            {
                if (channel is not null)
                {
                    await channel.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private static Task DeclareFanoutExchangeAsync(
            IRabbitMqTopologyChannel channel,
            string exchange,
            bool durable,
            bool autoDelete,
            CancellationToken cancellationToken) =>
            channel.ExchangeDeclareAsync(
                exchange,
                CacheFleetTopology.FanoutExchangeType,
                durable,
                autoDelete,
                arguments: null,
                cancellationToken);

        private static Task DeclareOverviewDbQueueAsync(
            IRabbitMqTopologyChannel channel,
            CancellationToken cancellationToken) =>
            channel.QueueDeclareAsync(
                OverviewDbTopology.QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                OverviewDbTopology.QueueArguments,
                cancellationToken);
    }
}
