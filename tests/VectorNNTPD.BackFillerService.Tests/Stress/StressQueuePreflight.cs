using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Read-only RabbitMQ Management preflight for stress runs. Never purges or mutates queues.
    /// </summary>
    public static class StressQueuePreflight
    {
        /// <summary>
        /// Default cleanliness: no ready messages and unacked not above a small residual.
        /// </summary>
        public const long DefaultMaxReady = 0;

        /// <summary>
        /// Allows a tiny unacked residual (e.g. concurrent unrelated work). Poison storms are far larger.
        /// </summary>
        public const long DefaultMaxUnacked = 2;

        /// <summary>
        /// Evaluates whether a queue snapshot is clean enough for a controlled concurrency sweep.
        /// </summary>
        public static bool IsClean(
            long ready,
            long unacked,
            long maxReady = DefaultMaxReady,
            long maxUnacked = DefaultMaxUnacked) =>
            ready <= maxReady && unacked <= maxUnacked;

        /// <summary>
        /// Fetches one Management API snapshot for <paramref name="queueName"/>. Does not modify the broker.
        /// </summary>
        public static async Task<QueueSnapshot> FetchAsync(
            StressHarnessOptions options,
            string queueName,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(queueName);

            var user = options.RabbitMqUsername;
            var pass = options.RabbitMqPassword;
            var creds = Convert.ToBase64String(Encoding.ASCII.GetBytes(user + ":" + pass));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
            var vhost = Uri.EscapeDataString(options.RabbitMqVirtualHost);
            var q = Uri.EscapeDataString(queueName);
            var url = $"http://{options.RabbitMqHost}:15672/api/queues/{vhost}/{q}";
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = doc.RootElement;
            return new QueueSnapshot(
                DateTimeOffset.UtcNow,
                queueName,
                root.TryGetProperty("type", out var type) ? type.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("messages", out var m) ? m.GetInt64() : 0,
                root.TryGetProperty("messages_ready", out var r) ? r.GetInt64() : 0,
                root.TryGetProperty("messages_unacknowledged", out var u) ? u.GetInt64() : 0,
                root.TryGetProperty("consumers", out var c) ? c.GetInt64() : 0,
                TryRate(root, "ack_details"),
                TryRate(root, "redeliver_details"),
                TryRate(root, "deliver_get_details"));
        }

        /// <summary>
        /// Writes a JSON snapshot under the report directory and throws if the queue is not clean.
        /// </summary>
        public static async Task RequireCleanAsync(
            StressHarnessOptions options,
            string backbone,
            string label,
            CancellationToken cancellationToken,
            long maxReady = DefaultMaxReady,
            long maxUnacked = DefaultMaxUnacked)
        {
            ArgumentNullException.ThrowIfNull(options);
            var queueName = ArticleWorkStressClient.ComposeProviderEntity(backbone);
            var snap = await FetchAsync(options, queueName, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(options.ReportDirectory);
            var path = Path.Join(
                options.ReportDirectory,
                $"preflight-{label}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmss}.json");
            await File.WriteAllTextAsync(
                    path,
                    JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine(
                $"preflight {label} queue={snap.QueueName} type={snap.Type} messages={snap.Messages} ready={snap.Ready} unacked={snap.Unacked} consumers={snap.Consumers} ackRate={snap.AckRate} redeliverRate={snap.RedeliverRate}");

            if (!IsClean(snap.Ready, snap.Unacked, maxReady, maxUnacked))
            {
                throw new InvalidOperationException(
                    $"Stress preflight failed for {snap.QueueName}: ready={snap.Ready} unacked={snap.Unacked} "
                    + $"(limits ready<={maxReady} unacked<={maxUnacked}). "
                    + "Do not run a concurrency sweep against a poisoned backlog. "
                    + "Complete operator-approved recovery first (see BACKFILLER_QUEUE_RECOVERY_ASSESSMENT.md). "
                    + $"Snapshot: {path}");
            }
        }

        /// <summary>
        /// Polls until the queue is clean or <paramref name="timeout"/> elapses. Does not purge or ack.
        /// Returns the final snapshot. Throws only when still dirty after the timeout.
        /// </summary>
        public static async Task<QueueSnapshot> WaitUntilCleanAsync(
            StressHarnessOptions options,
            string backbone,
            string label,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            long maxReady = DefaultMaxReady,
            long maxUnacked = DefaultMaxUnacked,
            TimeSpan? pollInterval = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            var interval = pollInterval ?? TimeSpan.FromSeconds(2);
            var queueName = ArticleWorkStressClient.ComposeProviderEntity(backbone);
            var deadline = DateTimeOffset.UtcNow + timeout;
            QueueSnapshot? last = null;
            while (DateTimeOffset.UtcNow <= deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                last = await FetchAsync(options, queueName, cancellationToken).ConfigureAwait(false);
                Console.WriteLine(
                    $"wait-clean {label} queue={last.QueueName} ready={last.Ready} unacked={last.Unacked} consumers={last.Consumers}");
                if (IsClean(last.Ready, last.Unacked, maxReady, maxUnacked))
                {
                    Directory.CreateDirectory(options.ReportDirectory);
                    var path = Path.Join(
                        options.ReportDirectory,
                        $"preflight-{label}-clean-{DateTimeOffset.UtcNow:yyyyMMddTHHmmss}.json");
                    await File.WriteAllTextAsync(
                            path,
                            JsonSerializer.Serialize(last, new JsonSerializerOptions { WriteIndented = true }),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return last;
                }

                try
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }

            await RequireCleanAsync(options, backbone, label + "-timeout", cancellationToken, maxReady, maxUnacked)
                .ConfigureAwait(false);
            return last!;
        }

        private static double TryRate(JsonElement root, string detailsName)
        {
            if (!root.TryGetProperty("message_stats", out var stats))
            {
                return 0;
            }

            if (!stats.TryGetProperty(detailsName, out var details))
            {
                return 0;
            }

            return details.TryGetProperty("rate", out var rate) ? rate.GetDouble() : 0;
        }

        /// <summary>One read-only Management API queue sample.</summary>
        public sealed record QueueSnapshot(
            DateTimeOffset TimestampUtc,
            string QueueName,
            string Type,
            long Messages,
            long Ready,
            long Unacked,
            long Consumers,
            double AckRate,
            double RedeliverRate,
            double DeliverRate);
    }
}
