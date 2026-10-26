using System.Text;
using RabbitMQ.Client;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Removes only work-queue messages published by a specific stress run (<c>StressRunId</c> header).
    /// Foreign deliveries stay unacknowledged and are requeued when the cleanup channel is disposed.
    /// </summary>
    /// <remarks>
    /// Foreign messages must not be <c>basic.reject(requeue=true)</c>'d during the scan: on classic queues
    /// that often resurfaces the same head message. They also must not be acked and republished: that
    /// creates a loss window on crash. Holding foreign deliveries unacked lets <c>basic.get</c> advance
    /// through ready messages; channel/connection dispose returns unacked foreign work to the queue.
    /// Concurrent publishers may still change relative order when foreign messages are requeued.
    /// Brief unacked occupancy is far below typical broker consumer-timeout defaults (minutes).
    /// </remarks>
    public static class StressOwnedWorkMessageCleanup
    {
        /// <summary>AMQP header name written by <see cref="ArticleWorkStressClient.CreateRequestProperties"/>.</summary>
        public const string StressRunIdHeader = "StressRunId";

        /// <summary>
        /// Upper bound on basic.get visits per cleanup (owned discards + foreign holds).
        /// Prevents unbounded scans when the queue holds unrelated backlog.
        /// </summary>
        internal const int DefaultMaxVisits = 512;

        /// <summary>
        /// Reads a <c>StressRunId</c> header value from AMQP headers (string or UTF-8 bytes).
        /// </summary>
        /// <param name="headers">Delivery headers; may be null.</param>
        /// <returns>The run id when present and non-blank; otherwise null.</returns>
        public static string? TryGetStressRunId(IDictionary<string, object?>? headers)
        {
            if (headers is null
                || !headers.TryGetValue(StressRunIdHeader, out var raw)
                || raw is null)
            {
                return null;
            }

            switch (raw)
            {
                case string s when !string.IsNullOrWhiteSpace(s):
                    return s.Trim();
                case byte[] bytes when bytes.Length > 0:
                    return Encoding.UTF8.GetString(bytes).Trim();
                case ReadOnlyMemory<byte> memory when memory.Length > 0:
                    return Encoding.UTF8.GetString(memory.Span).Trim();
                case ArraySegment<byte> segment when segment.Count > 0:
                    return Encoding.UTF8.GetString(segment.AsSpan()).Trim();
                case Memory<byte> memory when memory.Length > 0:
                    return Encoding.UTF8.GetString(memory.Span).Trim();
                default:
                    return null;
            }
        }

        /// <summary>
        /// Returns whether a delivery's <c>StressRunId</c> equals <paramref name="ownedRunId"/>.
        /// </summary>
        /// <param name="headers">Delivery headers.</param>
        /// <param name="ownedRunId">Run id that owns messages eligible for discard.</param>
        /// <returns><see langword="true"/> when the header matches the owned run id.</returns>
        public static bool IsOwnedByRun(IDictionary<string, object?>? headers, string ownedRunId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ownedRunId);
            var observed = TryGetStressRunId(headers);
            return observed is not null
                   && string.Equals(observed, ownedRunId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Connects briefly and discards ready messages on <c>backfiller.{backbone}</c> whose
        /// <c>StressRunId</c> equals <paramref name="stressRunId"/>. Foreign deliveries remain
        /// unacknowledged and are requeued when the cleanup channel is disposed.
        /// Best-effort: failures are written to stderr and do not throw (harness teardown must proceed).
        /// </summary>
        /// <param name="options">Broker connection settings (credentials never logged).</param>
        /// <param name="backbone">Provider backbone used to compose the work queue name.</param>
        /// <param name="stressRunId">Run id that owns messages to delete.</param>
        /// <param name="cancellationToken">Cancels connect/get/settle.</param>
        /// <param name="maxVisits">Maximum basic.get operations this call may perform.</param>
        /// <returns>Count of owned messages discarded.</returns>
        public static async Task<int> RemoveOwnedAsync(
            StressHarnessOptions options,
            string backbone,
            string stressRunId,
            CancellationToken cancellationToken,
            int maxVisits = DefaultMaxVisits)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
            ArgumentException.ThrowIfNullOrWhiteSpace(stressRunId);
            if (maxVisits <= 0)
            {
                return 0;
            }

            var queueName = ArticleWorkStressClient.ComposeProviderEntity(backbone);
            try
            {
                var factory = new ConnectionFactory
                {
                    HostName = options.RabbitMqHost,
                    Port = options.RabbitMqPort,
                    VirtualHost = options.RabbitMqVirtualHost,
                    UserName = options.RabbitMqUsername,
                    Password = options.RabbitMqPassword,
                    RequestedHeartbeat = TimeSpan.FromSeconds(30),
                    AutomaticRecoveryEnabled = false,
                    ClientProvidedName = "VectorNNTPD.BackFillerService.Tests:stress-owned-cleanup",
                };

                await using var connection = await factory.CreateConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var removed = await DiscardOwnedHoldingForeignAsync(
                        new LiveCleanupChannel(channel, queueName),
                        stressRunId,
                        maxVisits,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (removed > 0)
                {
                    Console.WriteLine(
                        $"stress-owned-cleanup queue={queueName} runId={stressRunId} removed={removed}");
                }

                return removed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"stress-owned-cleanup failed for runId={stressRunId}: {ex.GetType().Name}");
                return 0;
            }
        }

        /// <summary>
        /// Scans ready deliveries: nack-discards owned messages; leaves foreign unacked.
        /// Caller must dispose the channel so foreign deliveries are requeued by the broker.
        /// </summary>
        /// <param name="channel">Cleanup channel (get + discard only; no foreign ack).</param>
        /// <param name="stressRunId">Owned run id.</param>
        /// <param name="maxVisits">Visit bound.</param>
        /// <param name="cancellationToken">Cancels get/discard.</param>
        /// <returns>Owned messages discarded.</returns>
        internal static async Task<int> DiscardOwnedHoldingForeignAsync(
            IStressCleanupChannel channel,
            string stressRunId,
            int maxVisits,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(channel);
            ArgumentException.ThrowIfNullOrWhiteSpace(stressRunId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxVisits);

            var removed = 0;
            var visits = 0;
            while (visits < maxVisits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var got = await channel.BasicGetAsync(cancellationToken).ConfigureAwait(false);
                if (got is not { } delivery)
                {
                    break;
                }

                visits++;
                if (IsOwnedByRun(delivery.Headers, stressRunId))
                {
                    await channel.BasicNackDiscardAsync(delivery.DeliveryTag, cancellationToken)
                        .ConfigureAwait(false);
                    removed++;
                }
                // Foreign / missing / malformed StressRunId: leave unacked for channel dispose requeue.
            }

            return removed;
        }

        /// <summary>One gotten delivery used by the cleanup scan (headers + delivery tag only).</summary>
        /// <param name="DeliveryTag">Broker delivery tag for nack/discard.</param>
        /// <param name="Headers">AMQP headers; may be null.</param>
        internal readonly record struct StressCleanupDelivery(
            ulong DeliveryTag,
            IDictionary<string, object?>? Headers);

        /// <summary>
        /// Minimal channel surface for owned discard while holding foreign unacked.
        /// Implementations must not ack foreign deliveries.
        /// </summary>
        internal interface IStressCleanupChannel : IAsyncDisposable
        {
            /// <summary>Gets the next ready message, or null when the ready set is empty.</summary>
            /// <param name="cancellationToken">Cancels the get.</param>
            Task<StressCleanupDelivery?> BasicGetAsync(CancellationToken cancellationToken);

            /// <summary>Discards one owned delivery without requeue.</summary>
            /// <param name="deliveryTag">Tag from a prior get.</param>
            /// <param name="cancellationToken">Cancels the nack.</param>
            Task BasicNackDiscardAsync(ulong deliveryTag, CancellationToken cancellationToken);
        }

        /// <summary>Live RabbitMQ.Client adapter for <see cref="IStressCleanupChannel"/>.</summary>
        private sealed class LiveCleanupChannel(IChannel channel, string queueName) : IStressCleanupChannel
        {
            /// <inheritdoc />
            public async Task<StressCleanupDelivery?> BasicGetAsync(CancellationToken cancellationToken)
            {
                var got = await channel.BasicGetAsync(queueName, autoAck: false, cancellationToken)
                    .ConfigureAwait(false);
                if (got is null)
                {
                    return null;
                }

                return new StressCleanupDelivery(got.DeliveryTag, got.BasicProperties.Headers);
            }

            /// <inheritdoc />
            public Task BasicNackDiscardAsync(ulong deliveryTag, CancellationToken cancellationToken) =>
                channel.BasicNackAsync(deliveryTag, multiple: false, requeue: false, cancellationToken)
                    .AsTask();

            /// <inheritdoc />
            public ValueTask DisposeAsync() => channel.DisposeAsync();
        }
    }
}
