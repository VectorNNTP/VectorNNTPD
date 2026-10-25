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
    /// Deserialization uses <see cref="ArticleWorkQuorumManagementJsonContext"/> for Native AOT.
    /// </remarks>
    internal sealed class ArticleWorkQuorumManagementHealthProbe : IArticleWorkQuorumHealthProbe
    {
        /// <summary>Named <see cref="IHttpClientFactory"/> client for Management calls.</summary>
        internal const string HttpClientName = "VectorNNTPD.ArticleWork.QuorumManagement";

        /// <summary>Factory that creates the named Management HTTP client for each inspection.</summary>
        private readonly IHttpClientFactory _httpClientFactory;

        /// <summary>Bound RabbitMQ options, including Management base URL, vhost, and credentials.</summary>
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
                    .DeserializeAsync(
                        stream,
                        ArticleWorkQuorumManagementJsonContext.Default.ArticleWorkQuorumManagementQueueDocument,
                        timeoutCts.Token)
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
            catch (HttpRequestException ex)
            {
                return Unverifiable(
                    requiredMembers,
                    "Management API request failed: " + ex.GetType().Name + ".");
            }
            catch (JsonException ex)
            {
                return Unverifiable(
                    requiredMembers,
                    "Management API request failed: " + ex.GetType().Name + ".");
            }
            catch (InvalidOperationException ex)
            {
                return Unverifiable(
                    requiredMembers,
                    "Management API request failed: " + ex.GetType().Name + ".");
            }
            catch (IOException ex)
            {
                return Unverifiable(
                    requiredMembers,
                    "Management API request failed: " + ex.GetType().Name + ".");
            }
        }

        /// <summary>Maps a Management queue document to a health result.</summary>
        /// <param name="document">Deserialized Management queue payload for the provider queue.</param>
        /// <param name="requiredMembers">Configured membership floor compared to observed members and online counts.</param>
        /// <returns>Healthy, insufficient, missing, or unverifiable classification with a non-secret reason.</returns>
        private static ArticleWorkQuorumHealthResult Evaluate(
            ArticleWorkQuorumManagementQueueDocument document,
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
        /// <param name="baseUrl">Management HTTP base URL (scheme and authority; trailing slash optional).</param>
        /// <param name="vhost">AMQP virtual host; URI-encoded into the path.</param>
        /// <param name="queueName">Queue name; URI-encoded into the path.</param>
        /// <returns>Absolute <c>/api/queues/{vhost}/{queue}</c> URI under <paramref name="baseUrl"/>.</returns>
        internal static Uri BuildQueueUri(string baseUrl, string vhost, string queueName)
        {
            var root = baseUrl.TrimEnd('/');
            var encodedVhost = Uri.EscapeDataString(vhost);
            var encodedQueue = Uri.EscapeDataString(queueName);
            return new Uri($"{root}/api/queues/{encodedVhost}/{encodedQueue}");
        }

        /// <summary>
        /// Builds an <see cref="ArticleWorkQuorumHealthKind.Unverifiable"/> result with null membership observations.
        /// </summary>
        /// <param name="requiredMembers">Configured membership floor copied onto the result.</param>
        /// <param name="reason">Operator-facing explanation of why membership could not be verified.</param>
        /// <returns>An unverifiable health result with no observed member, online, or leader counts.</returns>
        private static ArticleWorkQuorumHealthResult Unverifiable(int requiredMembers, string reason) =>
            new(
                ArticleWorkQuorumHealthKind.Unverifiable,
                requiredMembers,
                ObservedMembers: null,
                OnlineMembers: null,
                Leader: null,
                Reason: reason);
    }
}
