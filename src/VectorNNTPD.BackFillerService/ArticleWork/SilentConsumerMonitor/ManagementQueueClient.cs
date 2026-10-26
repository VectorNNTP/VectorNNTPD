using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// Read-only RabbitMQ Management HTTP client for
    /// <c>GET /api/queues/{vhost}/{queue}</c>. Classifies success and failures into
    /// <see cref="ManagementPollResult"/> without throwing for transport/auth/parse outcomes.
    /// Never logs username or password.
    /// </summary>
    internal sealed class ManagementQueueClient
    {
        /// <summary>
        /// Named <see cref="IHttpClientFactory"/> client registered for this monitor
        /// (infinite HttpClient timeout; per-request cancel enforces Management timeout).
        /// </summary>
        internal const string HttpClientName = "VectorNNTPD.BackFiller.SilentConsumerMonitor";

        /// <summary>Factory used to create the named Management HTTP client per request.</summary>
        private readonly IHttpClientFactory _httpClientFactory;

        /// <summary>
        /// Live RabbitMQ options; <see cref="IOptions{TOptions}.Value"/> is read on each poll so
        /// Management BaseUrl, RequestTimeoutSeconds, Username, and Password reflect account-refresh projection.
        /// </summary>
        private readonly IOptions<RabbitMqOptions> _rabbitOptions;

        /// <summary>Clock used to stamp <see cref="ManagementQueueSnapshot.SampledAtUtc"/> on successful parses.</summary>
        private readonly TimeProvider _time;

        /// <summary>
        /// Creates a Management queue client that polls via the named HTTP client and live RabbitMQ options.
        /// </summary>
        /// <param name="httpClientFactory">
        /// Factory used to create <see cref="HttpClientName"/> for each <see cref="GetQueueAsync"/> call.
        /// </param>
        /// <param name="rabbitOptions">
        /// Options monitor whose current <see cref="RabbitMqOptions"/> supply Management BaseUrl,
        /// RequestTimeoutSeconds (clamped 1–60, default
        /// <see cref="SilentConsumerMonitorDefaults.ManagementHttpTimeoutSeconds"/>), Username, and Password.
        /// Values are read at call time, not cached in the constructor.
        /// </param>
        /// <param name="time">
        /// Time provider used only to set <see cref="ManagementQueueSnapshot.SampledAtUtc"/> when parsing
        /// a successful JSON document; not used for HTTP timeout (timeout uses a linked CTS).
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="httpClientFactory"/>, <paramref name="rabbitOptions"/>, or
        /// <paramref name="time"/> is <see langword="null"/>.
        /// </exception>
        public ManagementQueueClient(
            IHttpClientFactory httpClientFactory,
            IOptions<RabbitMqOptions> rabbitOptions,
            TimeProvider time)
        {
            ArgumentNullException.ThrowIfNull(httpClientFactory);
            ArgumentNullException.ThrowIfNull(rabbitOptions);
            ArgumentNullException.ThrowIfNull(time);
            _httpClientFactory = httpClientFactory;
            _rabbitOptions = rabbitOptions;
            _time = time;
        }

        /// <summary>
        /// Fetches one queue document using live <see cref="RabbitMqOptions"/> (BaseUrl, timeout, username/password).
        /// </summary>
        /// <param name="virtualHost">RabbitMQ virtual host (URI-escaped in the request path).</param>
        /// <param name="queueName">Queue entity name (URI-escaped in the request path).</param>
        /// <param name="cancellationToken">
        /// Caller cancellation. Linked with a per-request timeout CTS; timeout alone yields
        /// <see cref="ManagementPollKind.TransientFailure"/>; caller cancel yields
        /// <see cref="ManagementPollKind.Cancelled"/>.
        /// </param>
        /// <returns>
        /// Classified poll result. Success carries a snapshot (gauges may still be incomplete).
        /// Missing BaseUrl/credentials, HTTP errors, timeouts, and parse failures return failure kinds
        /// without throwing.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="virtualHost"/> or <paramref name="queueName"/> is null, empty, or whitespace.
        /// </exception>
        /// <remarks>
        /// Builds Basic auth from the options instance at call time so account-refresh credential projection
        /// applies on the next request. Does not log username or password. Missing BaseUrl or credentials
        /// yield <see cref="ManagementPollKind.TransientFailure"/> without throwing.
        /// </remarks>
        public async Task<ManagementPollResult> GetQueueAsync(
            string virtualHost,
            string queueName,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(virtualHost);
            ArgumentException.ThrowIfNullOrWhiteSpace(queueName);

            if (!TryResolve(out var baseUrl, out var username, out var password, out var failure))
            {
                return Fail(ManagementPollKind.TransientFailure, failure);
            }

            var configuredTimeout = _rabbitOptions.Value.Management?.RequestTimeoutSeconds
                ?? SilentConsumerMonitorDefaults.ManagementHttpTimeoutSeconds;
            var timeoutSeconds = Math.Clamp(configuredTimeout, 1, 60);
            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                var uri = BuildQueueUri(baseUrl, virtualHost, queueName);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.ASCII.GetBytes(username + ":" + password)));

                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                    .ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return Fail(ManagementPollKind.AuthenticationFailure, $"HTTP {(int)response.StatusCode}");
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return Fail(ManagementPollKind.QueueMissing, "Queue missing.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Fail(ManagementPollKind.TransientFailure, $"HTTP {(int)response.StatusCode}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token)
                    .ConfigureAwait(false);
                var snapshot = Parse(document.RootElement, virtualHost, queueName, _time.GetUtcNow());
                return new ManagementPollResult
                {
                    Kind = ManagementPollKind.Success,
                    Reason = snapshot.HasRequiredGauges ? "ok" : "incomplete gauges",
                    Snapshot = snapshot,
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Fail(ManagementPollKind.TransientFailure, "timeout");
            }
            catch (OperationCanceledException)
            {
                return Fail(ManagementPollKind.Cancelled, "cancelled");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException)
            {
                return Fail(ManagementPollKind.TransientFailure, ex.GetType().Name);
            }
        }

        /// <summary>Builds the Management queue URI without embedding credentials.</summary>
        /// <param name="baseUrl">Management HTTP base URL (trailing slash trimmed).</param>
        /// <param name="vhost">Virtual host segment (URI-escaped).</param>
        /// <param name="queueName">Queue name segment (URI-escaped).</param>
        /// <returns><c>{base}/api/queues/{vhost}/{queue}</c> as an absolute <see cref="Uri"/>.</returns>
        internal static Uri BuildQueueUri(string baseUrl, string vhost, string queueName) =>
            new($"{baseUrl.TrimEnd('/')}/api/queues/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(queueName)}");

        /// <summary>
        /// Parses Management queue JSON gauges and rates without inventing zeros for missing fields.
        /// </summary>
        /// <param name="root">Root object of the queue document.</param>
        /// <param name="virtualHost">Virtual host stamped onto the snapshot.</param>
        /// <param name="queueName">Queue name stamped onto the snapshot.</param>
        /// <param name="atUtc">Sample timestamp stamped onto the snapshot.</param>
        /// <returns>
        /// Snapshot with nullable gauges/rates; missing or invalid JSON properties remain <see langword="null"/>.
        /// </returns>
        internal static ManagementQueueSnapshot Parse(
            JsonElement root,
            string virtualHost,
            string queueName,
            DateTimeOffset atUtc)
        {
            long? ackTotal = null;
            long? deliverGetTotal = null;
            double? ackRate = null;
            double? deliverGetRate = null;
            if (root.TryGetProperty("message_stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
            {
                ackTotal = TryInt64(stats, "ack");
                deliverGetTotal = TryInt64(stats, "deliver_get");
                ackRate = TryRate(stats, "ack_details");
                deliverGetRate = TryRate(stats, "deliver_get_details") ?? TryRate(stats, "deliver_details");
            }

            return new ManagementQueueSnapshot
            {
                QueueName = queueName,
                VirtualHost = virtualHost,
                SampledAtUtc = atUtc,
                MessagesReady = TryInt64(root, "messages_ready"),
                MessagesUnacknowledged = TryInt64(root, "messages_unacknowledged"),
                Consumers = TryInt64(root, "consumers"),
                AckRate = ackRate,
                DeliverGetRate = deliverGetRate,
                AckTotal = ackTotal,
                DeliverGetTotal = deliverGetTotal,
            };
        }

        /// <summary>
        /// Reads current <see cref="RabbitMqOptions"/> for Management BaseUrl and AMQP Username/Password.
        /// </summary>
        /// <param name="baseUrl">Configured Management BaseUrl when resolution succeeds; otherwise empty.</param>
        /// <param name="username">Configured Username when resolution succeeds; otherwise empty.</param>
        /// <param name="password">Configured Password when resolution succeeds; otherwise empty.</param>
        /// <param name="failure">Human-readable reason when resolution fails; empty on success.</param>
        /// <returns>
        /// <see langword="true"/> when BaseUrl is non-whitespace and Username/Password are present;
        /// otherwise <see langword="false"/> (caller maps this to <see cref="ManagementPollKind.TransientFailure"/>).
        /// </returns>
        private bool TryResolve(out string baseUrl, out string username, out string password, out string failure)
        {
            var rabbit = _rabbitOptions.Value;
            var baseCandidate = rabbit.Management?.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseCandidate))
            {
                baseUrl = string.Empty;
                username = string.Empty;
                password = string.Empty;
                failure = "Management BaseUrl is not configured.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(rabbit.Username) || string.IsNullOrEmpty(rabbit.Password))
            {
                baseUrl = string.Empty;
                username = string.Empty;
                password = string.Empty;
                failure = "Management credentials are not configured.";
                return false;
            }

            baseUrl = baseCandidate;
            username = rabbit.Username;
            password = rabbit.Password;
            failure = string.Empty;
            return true;
        }

        /// <summary>Builds a failure <see cref="ManagementPollResult"/> with no snapshot.</summary>
        /// <param name="kind">Failure classification.</param>
        /// <param name="reason">Non-secret diagnostic reason.</param>
        /// <returns>Poll result with <see cref="ManagementPollResult.Snapshot"/> unset.</returns>
        private static ManagementPollResult Fail(ManagementPollKind kind, string reason) =>
            new() { Kind = kind, Reason = reason };

        /// <summary>Reads a JSON number property as <see cref="long"/>, or null if absent/unconvertible.</summary>
        /// <param name="parent">Object that may contain <paramref name="name"/>.</param>
        /// <param name="name">Property name.</param>
        /// <returns>Parsed integer, truncated double in long range, or <see langword="null"/>.</returns>
        private static long? TryInt64(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var i) => i,
                JsonValueKind.Number when value.TryGetDouble(out var d)
                    && d is >= long.MinValue and <= long.MaxValue => (long)d,
                _ => null,
            };
        }

        /// <summary>
        /// Reads <c>{detailsName}.rate</c> from message_stats when present and finite; otherwise null.
        /// </summary>
        /// <param name="stats"><c>message_stats</c> object.</param>
        /// <param name="detailsName">Details object name (for example <c>ack_details</c>).</param>
        /// <returns>Finite rate value, or <see langword="null"/> when missing/NaN/Infinity.</returns>
        private static double? TryRate(JsonElement stats, string detailsName)
        {
            if (!stats.TryGetProperty(detailsName, out var details)
                || details.ValueKind != JsonValueKind.Object
                || !details.TryGetProperty("rate", out var rate)
                || rate.ValueKind != JsonValueKind.Number
                || !rate.TryGetDouble(out var value)
                || double.IsNaN(value)
                || double.IsInfinity(value))
            {
                return null;
            }

            return value;
        }
    }
}
