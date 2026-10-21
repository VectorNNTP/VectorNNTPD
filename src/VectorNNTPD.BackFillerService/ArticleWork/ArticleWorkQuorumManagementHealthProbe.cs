using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// Verifies Article Work quorum membership through the RabbitMQ Management HTTP API.
    /// </summary>
    /// <remarks>
    /// Uses <c>GET /api/queues/{vhost}/{name}</c>. AMQP <c>queue.declare-ok</c> is never treated as membership proof.
    /// When <see cref="RabbitMqManagementOptions.BaseUrl"/> is unset, every inspection is
    /// <see cref="ArticleWorkQuorumHealthKind.Unverifiable"/>.
    /// </remarks>
    internal sealed class ArticleWorkQuorumManagementHealthProbe : IArticleWorkQuorumHealthProbe
    {
        /// <summary>Named <see cref="IHttpClientFactory"/> client for Management calls.</summary>
        internal const string HttpClientName = "VectorNNTPD.ArticleWork.QuorumManagement";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IOptions<RabbitMqOptions> _options;

        /// <summary>
        /// Initializes a new Management-backed probe.
        /// </summary>
        /// <param name="httpClientFactory">Creates the named Management client.</param>
        /// <param name="options">RabbitMQ options including Management base URL and credentials.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required dependency is null.</exception>
        public ArticleWorkQuorumManagementHealthProbe(
            IHttpClientFactory httpClientFactory,
            IOptions<RabbitMqOptions> options)
        {
            ArgumentNullException.ThrowIfNull(httpClientFactory);
            ArgumentNullException.ThrowIfNull(options);
            _httpClientFactory = httpClientFactory;
            _options = options;
        }

        /// <inheritdoc />
        public async Task<ArticleWorkQuorumHealthResult> InspectAsync(
            string queueName,
            int requiredMembers,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
            ArgumentOutOfRangeException.ThrowIfLessThan(requiredMembers, 1);

            var rabbitMq = _options.Value;
            var management = rabbitMq.Management;
            if (management?.BaseUrl is not { Length: > 0 } baseUrl)
            {
                return Unverifiable(
                    requiredMembers,
                    "RabbitMQ Management BaseUrl is not configured; quorum membership cannot be verified.");
            }

            if (string.IsNullOrWhiteSpace(rabbitMq.Username) || rabbitMq.Password is null)
            {
                return Unverifiable(
                    requiredMembers,
                    "RabbitMQ credentials are not available for Management authentication.");
            }

            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                var timeoutSeconds = management.RequestTimeoutSeconds is { } configured and > 0
                    ? configured
                    : 5;
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                var vhost = string.IsNullOrWhiteSpace(rabbitMq.VirtualHost) ? "/" : rabbitMq.VirtualHost;
                var requestUri = BuildQueueUri(baseUrl, vhost, queueName);
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                var credential = Convert.ToBase64String(
                    Encoding.ASCII.GetBytes(rabbitMq.Username + ":" + rabbitMq.Password));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credential);

                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return new ArticleWorkQuorumHealthResult(
                        ArticleWorkQuorumHealthKind.QueueMissing,
                        requiredMembers,
                        ObservedMembers: null,
                        OnlineMembers: null,
                        Leader: null,
                        Reason: "Management API reported the provider queue as missing.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Unverifiable(
                        requiredMembers,
                        $"Management API returned HTTP {(int)response.StatusCode}.");
                }

                await using var stream = await response.Content
                    .ReadAsStreamAsync(timeoutCts.Token)
                    .ConfigureAwait(false);
                var document = await JsonSerializer
                    .DeserializeAsync<ManagementQueueDocument>(stream, JsonOptions, timeoutCts.Token)
                    .ConfigureAwait(false);

                if (document is null)
                {
                    return Unverifiable(requiredMembers, "Management API returned an empty queue document.");
                }

                return Evaluate(document, requiredMembers);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Unverifiable(requiredMembers, "Management API request timed out.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Management faults must become Unverifiable, not crash reconcile.
            catch (Exception ex)
            {
                return Unverifiable(
                    requiredMembers,
                    "Management API request failed: " + ex.GetType().Name + ".");
            }
        }

        /// <summary>Maps a Management queue document to a health result.</summary>
        private static ArticleWorkQuorumHealthResult Evaluate(
            ManagementQueueDocument document,
            int requiredMembers)
        {
            var members = document.Members?.Count;
            var online = document.Online?.Count;
            var leader = string.IsNullOrWhiteSpace(document.Leader) ? null : document.Leader;

            if (members is null || online is null)
            {
                return new ArticleWorkQuorumHealthResult(
                    ArticleWorkQuorumHealthKind.Unverifiable,
                    requiredMembers,
                    members,
                    online,
                    leader,
                    "Management queue document did not expose quorum members/online arrays.");
            }

            if (leader is null)
            {
                return new ArticleWorkQuorumHealthResult(
                    ArticleWorkQuorumHealthKind.Unverifiable,
                    requiredMembers,
                    members,
                    online,
                    Leader: null,
                    Reason: "Management queue document did not report a quorum leader.");
            }

            if (members.Value < requiredMembers)
            {
                return new ArticleWorkQuorumHealthResult(
                    ArticleWorkQuorumHealthKind.InsufficientMembers,
                    requiredMembers,
                    members,
                    online,
                    leader,
                    "Configured quorum membership is below the required group size. Operator membership growth is required for pre-existing queues.");
            }

            if (online.Value < requiredMembers)
            {
                return new ArticleWorkQuorumHealthResult(
                    ArticleWorkQuorumHealthKind.InsufficientOnlineMembers,
                    requiredMembers,
                    members,
                    online,
                    leader,
                    "Online quorum membership is below the required availability floor.");
            }

            return new ArticleWorkQuorumHealthResult(
                ArticleWorkQuorumHealthKind.Healthy,
                requiredMembers,
                members,
                online,
                leader,
                "Quorum membership, online availability, and leader meet the configured policy.");
        }

        /// <summary>Builds an absolute Management queue URL without credentials.</summary>
        internal static Uri BuildQueueUri(string baseUrl, string vhost, string queueName)
        {
            var root = baseUrl.TrimEnd('/');
            var encodedVhost = Uri.EscapeDataString(vhost);
            var encodedQueue = Uri.EscapeDataString(queueName);
            return new Uri($"{root}/api/queues/{encodedVhost}/{encodedQueue}");
        }

        private static ArticleWorkQuorumHealthResult Unverifiable(int requiredMembers, string reason) =>
            new(
                ArticleWorkQuorumHealthKind.Unverifiable,
                requiredMembers,
                ObservedMembers: null,
                OnlineMembers: null,
                Leader: null,
                Reason: reason);

        /// <summary>Subset of the Management queue JSON used for quorum health.</summary>
        private sealed class ManagementQueueDocument
        {
            /// <summary>Configured quorum member node names.</summary>
            public List<string>? Members { get; set; }

            /// <summary>Online quorum member node names.</summary>
            public List<string>? Online { get; set; }

            /// <summary>Current Raft leader node name.</summary>
            public string? Leader { get; set; }
        }
    }
}
