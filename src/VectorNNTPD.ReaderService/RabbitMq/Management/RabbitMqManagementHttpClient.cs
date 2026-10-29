using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.ReaderService.RabbitMq.Management
{
    /// <summary>
    /// HTTP client for RabbitMQ Management API queue inventory
    /// (<c>GET /api/queues/{vhost}</c>).
    /// </summary>
    /// <remarks>
    /// Authenticates with HTTP Basic using <see cref="RabbitMqOptions.Username"/> /
    /// <see cref="RabbitMqOptions.Password"/>. Never logs credentials or the
    /// <c>Authorization</c> header. Uses source-generated JSON deserialization.
    /// </remarks>
    internal sealed class RabbitMqManagementHttpClient : IRabbitMqManagementQueueInventory
    {
        /// <summary>Named <see cref="IHttpClientFactory"/> client for Management API calls.</summary>
        internal const string HttpClientName = "RabbitMqManagement";

        private readonly HttpClient _httpClient;
        private readonly IOptions<RabbitMqOptions> _options;

        /// <summary>Initializes a new Management API client.</summary>
        public RabbitMqManagementHttpClient(
            HttpClient httpClient,
            IOptions<RabbitMqOptions> options)
        {
            ArgumentNullException.ThrowIfNull(httpClient);
            ArgumentNullException.ThrowIfNull(options);
            _httpClient = httpClient;
            _options = options;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<RabbitMqManagementQueueInfo>> ListQueuesAsync(
            CancellationToken cancellationToken)
        {
            var rabbitMq = _options.Value;
            var management = rabbitMq.Management
                ?? throw new InvalidOperationException("RabbitMQ:Management is required.");
            var baseUrl = management.BaseUrl?.Trim();
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new InvalidOperationException("RabbitMQ:Management:BaseUrl is required.");
            }

            var username = rabbitMq.Username?.Trim();
            var password = rabbitMq.Password;
            if (string.IsNullOrWhiteSpace(username) || password is null)
            {
                throw new InvalidOperationException(
                    "RabbitMQ:Username and RabbitMQ:Password are required for Management API access.");
            }

            var virtualHost = string.IsNullOrWhiteSpace(rabbitMq.VirtualHost)
                ? "/"
                : rabbitMq.VirtualHost.Trim();
            var path = $"api/queues/{Uri.EscapeDataString(virtualHost)}";

            var timeoutSeconds = management.RequestTimeoutSeconds is > 0
                ? management.RequestTimeoutSeconds.Value
                : 5;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            try
            {
                response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"RabbitMQ Management API request timed out after {timeoutSeconds} seconds.");
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    // Do not include response bodies that may echo credentials or tokens.
                    throw new HttpRequestException(
                        $"RabbitMQ Management API returned {(int)response.StatusCode} ({response.StatusCode}) for queue inventory.");
                }

                RabbitMqManagementQueueDto[]? payload;
                try
                {
                    payload = await response.Content
                        .ReadFromJsonAsync(
                            RabbitMqManagementJsonContext.Default.RabbitMqManagementQueueDtoArray,
                            timeoutCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"RabbitMQ Management API response read timed out after {timeoutSeconds} seconds.");
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "RabbitMQ Management API returned a malformed queue inventory payload.",
                        ex);
                }

                if (payload is null)
                {
                    throw new InvalidOperationException(
                        "RabbitMQ Management API returned an empty queue inventory payload.");
                }

                var results = new List<RabbitMqManagementQueueInfo>(payload.Length);
                for (var i = 0; i < payload.Length; i++)
                {
                    var row = payload[i];
                    if (row is null || string.IsNullOrWhiteSpace(row.Name))
                    {
                        continue;
                    }

                    var consumers = row.Consumers;
                    if (consumers < 0)
                    {
                        consumers = 0;
                    }

                    results.Add(new RabbitMqManagementQueueInfo(row.Name.Trim(), consumers));
                }

                return results;
            }
        }
    }
}
