using System.Collections.Immutable;
using VectorNNTPD.TransitService.RabbitMq.Management;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.TransitService.RabbitMq.ArticleWork
{
    /// <summary>
    /// Cached BackFiller ArticleWork consumer counts from the RabbitMQ Management HTTP API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Discovery plane: Management API queue inventory refreshed on a fixed ~5s interval.
    /// AMQP remains the ArticleWork data plane. Availability is intentionally eventually
    /// consistent and may be approximately 5 seconds stale.
    /// </para>
    /// <para>
    /// A provider queue is eligible only when the Management API reports
    /// <c>consumers &gt; 0</c> for that queue name. Missing queues and zero-consumer queues
    /// are unavailable and are omitted from the snapshot. Absent/zero-consumer states are
    /// normal and do not produce exceptions.
    /// </para>
    /// <para>
    /// A failed Management API refresh retains the last successfully retrieved snapshot
    /// (last-known-good). There is no additional long-term stale expiry beyond the normal
    /// ~5s refresh cadence.
    /// </para>
    /// </remarks>
    internal sealed class BackfillConsumerAvailabilityService : IBackfillConsumerAvailability, IAsyncDisposable
    {
        /// <summary>Minimum interval between Management API refresh attempts.</summary>
        internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

        private readonly IRabbitMqManagementQueueInventory _inventory;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger _logger;
        private readonly object _gate = new();
        private readonly SemaphoreSlim _refreshGate = new(1, 1);
        private ImmutableArray<BackfillEligibleBackbone> _snapshot = [];
        private DateTimeOffset _nextRefresh = DateTimeOffset.MinValue;
        private int _disposed;

        /// <summary>Initializes a new availability cache.</summary>
        internal BackfillConsumerAvailabilityService(
            IRabbitMqManagementQueueInventory inventory,
            ILogger logger,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(inventory);
            ArgumentNullException.ThrowIfNull(logger);
            _inventory = inventory;
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <inheritdoc />
        public IReadOnlyList<BackfillEligibleBackbone> GetEligibleBackbones()
        {
            RefreshIfDueAsync(force: false, CancellationToken.None).GetAwaiter().GetResult();
            return _snapshot;
        }

        /// <summary>Forces a refresh regardless of interval (startup / tests).</summary>
        internal Task RefreshNowAsync(CancellationToken cancellationToken = default) =>
            RefreshIfDueAsync(force: true, cancellationToken);

        private async Task RefreshIfDueAsync(bool force, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            if (!force)
            {
                lock (_gate)
                {
                    if (now < _nextRefresh)
                    {
                        return;
                    }
                }
            }

            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                now = _timeProvider.GetUtcNow();
                if (!force)
                {
                    lock (_gate)
                    {
                        if (now < _nextRefresh)
                        {
                            return;
                        }
                    }
                }

                await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _nextRefresh = _timeProvider.GetUtcNow() + RefreshInterval;
                }
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        private async Task RefreshCoreAsync(CancellationToken cancellationToken)
        {
            try
            {
                var queues = await _inventory.ListQueuesAsync(cancellationToken).ConfigureAwait(false);
                var consumersByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < queues.Count; i++)
                {
                    var queue = queues[i];
                    if (string.IsNullOrWhiteSpace(queue.Name))
                    {
                        continue;
                    }

                    // Last duplicate name wins; Management API should not duplicate names in one vhost.
                    consumersByName[queue.Name] = queue.Consumers;
                }

                var builder = ImmutableArray.CreateBuilder<BackfillEligibleBackbone>();
                foreach (var definition in BackfillArticleRetrievalTopology.Definitions)
                {
                    if (!consumersByName.TryGetValue(definition.QueueName, out var consumers)
                        || consumers <= 0)
                    {
                        continue;
                    }

                    builder.Add(new BackfillEligibleBackbone(
                        definition.Provider,
                        definition.ExchangeName,
                        definition.RoutingKey,
                        definition.QueueName,
                        consumers));
                }

                var next = builder.ToImmutable();
                lock (_gate)
                {
                    _snapshot = next;
                }

                ArticleWorkRpcLogMessages.AvailabilitySnapshotRefreshed(_logger, next.Length);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                // Retain last-known-good snapshot. Do not treat Management outage as zero consumers.
                ArticleWorkRpcLogMessages.ManagementAvailabilityRefreshFailed(_logger, ex);
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _refreshGate.Dispose();
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    /// <summary>Test double / static snapshot availability.</summary>
    internal sealed class StaticBackfillConsumerAvailability : IBackfillConsumerAvailability
    {
        private volatile IReadOnlyList<BackfillEligibleBackbone> _eligible;

        /// <summary>Creates availability from an initial snapshot.</summary>
        internal StaticBackfillConsumerAvailability(IReadOnlyList<BackfillEligibleBackbone> eligible)
        {
            ArgumentNullException.ThrowIfNull(eligible);
            _eligible = eligible;
        }

        /// <summary>Replaces the snapshot (tests).</summary>
        internal void SetEligible(IReadOnlyList<BackfillEligibleBackbone> eligible)
        {
            ArgumentNullException.ThrowIfNull(eligible);
            _eligible = eligible;
        }

        /// <inheritdoc />
        public IReadOnlyList<BackfillEligibleBackbone> GetEligibleBackbones() => _eligible;
    }
}
