using System.Text;
using RabbitMQ.Client;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Removes only work-queue messages published by a specific stress run (<c>StressRunId</c> header).
    /// Non-matching messages are parked and republished; shared/production traffic is never purged wholesale.
    /// </summary>
    /// <remarks>
    /// Foreign messages cannot be <c>basic.reject(requeue=true)</c>'d while scanning: on classic queues that
    /// often resurfaces the same head message immediately, so owned payloads behind foreign backlog would
    /// never be reached. Park+ack then republish avoids that spin. Unavoidable limitations: brief absence
    /// of parked messages until republish; relative order vs messages published during cleanup may change;
    /// a process crash between ack and republish can lose a parked foreign message.
    /// </remarks>
    public static class StressOwnedWorkMessageCleanup
    {
        /// <summary>AMQP header name written by <see cref="ArticleWorkStressClient.CreateRequestProperties"/>.</summary>
        public const string StressRunIdHeader = "StressRunId";

        /// <summary>
        /// Upper bound on basic.get visits per cleanup (owned discards + foreign parks).
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
        /// Connects briefly and removes ready messages on <c>backfiller.{backbone}</c> whose
        /// <c>StressRunId</c> equals <paramref name="stressRunId"/>. Foreign messages are parked
        /// (acked) and republished after the scan so owned messages behind them can be reached.
        /// Best-effort: failures are written to stderr and do not throw (harness teardown must proceed).
        /// </summary>
        /// <param name="options">Broker connection settings (credentials never logged).</param>
        /// <param name="backbone">Provider backbone used to compose the work queue name.</param>
        /// <param name="stressRunId">Run id that owns messages to delete.</param>
        /// <param name="cancellationToken">Cancels connect/get/settle/republish.</param>
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

                var removed = 0;
                var visits = 0;
                var parkedForeign = new List<ParkedForeignMessage>();
                while (visits < maxVisits)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var got = await channel.BasicGetAsync(queueName, autoAck: false, cancellationToken)
                        .ConfigureAwait(false);
                    if (got is null)
                    {
                        break;
                    }

                    visits++;
                    if (IsOwnedByRun(got.BasicProperties.Headers, stressRunId))
                    {
                        await channel.BasicNackAsync(
                                got.DeliveryTag,
                                multiple: false,
                                requeue: false,
                                cancellationToken)
                            .ConfigureAwait(false);
                        removed++;
                        continue;
                    }

                    // Park foreign so the scan can continue past them (reject+requeue spins on classic head).
                    parkedForeign.Add(
                        new ParkedForeignMessage(
                            CopyProperties(got.BasicProperties),
                            got.Body.ToArray()));
                    await channel.BasicAckAsync(got.DeliveryTag, multiple: false, cancellationToken)
                        .ConfigureAwait(false);
                }

                foreach (var foreign in parkedForeign)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await channel.BasicPublishAsync(
                            exchange: queueName,
                            routingKey: queueName,
                            mandatory: false,
                            basicProperties: foreign.Properties,
                            body: foreign.Body,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }

                if (removed > 0 || parkedForeign.Count > 0)
                {
                    Console.WriteLine(
                        $"stress-owned-cleanup queue={queueName} runId={stressRunId} removed={removed} parkedForeign={parkedForeign.Count} visits={visits}");
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

        /// <summary>Copies settlement-relevant AMQP properties for a temporary foreign park/republish.</summary>
        /// <param name="source">Properties from the gotten delivery.</param>
        /// <returns>A new <see cref="BasicProperties"/> suitable for republish.</returns>
        internal static BasicProperties CopyProperties(IReadOnlyBasicProperties source)
        {
            ArgumentNullException.ThrowIfNull(source);
            IDictionary<string, object?>? headers = null;
            if (source.Headers is { Count: > 0 })
            {
                headers = new Dictionary<string, object?>(source.Headers, StringComparer.Ordinal);
            }

            return new BasicProperties
            {
                ContentType = source.ContentType,
                ContentEncoding = source.ContentEncoding,
                Headers = headers,
                DeliveryMode = source.DeliveryMode,
                Priority = source.Priority,
                CorrelationId = source.CorrelationId,
                ReplyTo = source.ReplyTo,
                Expiration = source.Expiration,
                MessageId = source.MessageId,
                Timestamp = source.Timestamp,
                Type = source.Type,
                UserId = source.UserId,
                AppId = source.AppId,
            };
        }

        /// <summary>One foreign work message held in memory until the owned scan finishes.</summary>
        /// <param name="Properties">AMQP properties to republish.</param>
        /// <param name="Body">Payload octets to republish.</param>
        private readonly record struct ParkedForeignMessage(BasicProperties Properties, byte[] Body);
    }
}
