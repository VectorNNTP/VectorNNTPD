using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace VectorNNTPD.Common.Acme.Protocol
{
    /// <summary>
    /// ACME HTTP transport: directory cache, nonce pool, JWS POST, badNonce / transient retries.
    /// Creates an <see cref="HttpClient"/> per request via <see cref="IHttpClientFactory"/>.
    /// State-changing POSTs are not replayed after an ambiguous transmission outcome.
    /// </summary>
    internal sealed class AcmeHttpTransport
    {
        /// <summary>JWS request <c>Content-Type</c> required by RFC 8555 §6.2, with no charset parameter.</summary>
        private const string JoseContentType = "application/jose+json";

        /// <summary>Maximum JWS submissions after a <see cref="AcmeErrorTypes.BadNonce"/> rejection, including the first attempt.</summary>
        private const int MaxBadNonceRetries = 5;

        /// <summary>Maximum extra attempts after a transport or retryable HTTP failure. The first attempt is not counted.</summary>
        private const int MaxTransientRetries = 4;

        /// <summary>Longest Retry-After delay that is honored. A longer or non-positive delay fails the request instead of waiting.</summary>
        private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

        /// <summary>Factory used to create one <see cref="HttpClient"/> per HTTP call.</summary>
        private readonly IHttpClientFactory _httpClientFactory;

        /// <summary>Named client passed to <see cref="IHttpClientFactory.CreateClient(string)"/>.</summary>
        private readonly string _httpClientName;

        /// <summary>ACME directory URL fetched by GET and cached until invalidated.</summary>
        private readonly Uri _directoryUri;

        /// <summary>Logger for nonce and transport retries.</summary>
        private readonly ILogger _logger;

        /// <summary>Clock used for Retry-After deltas and transport backoff delays.</summary>
        private readonly TimeProvider _time;

        /// <summary>Replay nonces captured from <c>Replay-Nonce</c> response headers. Popped before each JWS.</summary>
        private readonly ConcurrentStack<string> _nonces = new();

        /// <summary>
        /// Cached directory fetch. <see langword="null"/> before the first fetch and after invalidation.
        /// A faulted or canceled task is replaced on the next read.
        /// </summary>
        private Task<AcmeDirectoryResource>? _directory;

        /// <summary>
        /// Stores the HTTP client factory, directory URL, logger, and clock used for every later request.
        /// </summary>
        /// <param name="httpClientFactory">Factory that creates the named ACME client.</param>
        /// <param name="httpClientName">Name passed to <see cref="IHttpClientFactory.CreateClient(string)"/>.</param>
        /// <param name="directoryUri">Absolute ACME directory URL.</param>
        /// <param name="logger">Logger for retry events.</param>
        /// <param name="time">Clock for retry delays.</param>
        internal AcmeHttpTransport(
            IHttpClientFactory httpClientFactory,
            string httpClientName,
            Uri directoryUri,
            ILogger logger,
            TimeProvider time)
        {
            _httpClientFactory = httpClientFactory;
            _httpClientName = httpClientName;
            _directoryUri = directoryUri;
            _logger = logger;
            _time = time;
        }

        /// <summary>Creates a new client for one request. The caller disposes it.</summary>
        /// <returns>The named <see cref="HttpClient"/>.</returns>
        private HttpClient CreateClient() => _httpClientFactory.CreateClient(_httpClientName);

        /// <summary>Returns the cached ACME directory, fetching it on first use.</summary>
        /// <param name="cancellationToken">Cancels the directory GET when a fetch is started. A cached task is returned as-is.</param>
        /// <returns>The directory document. A faulted or canceled cached task is replaced on the next call.</returns>
        internal Task<AcmeDirectoryResource> GetDirectoryAsync(CancellationToken cancellationToken)
        {
            Task<AcmeDirectoryResource>? cached = Volatile.Read(ref _directory);
            if (cached is not null && !cached.IsFaulted && !cached.IsCanceled)
            {
                return cached;
            }

            Task<AcmeDirectoryResource> fetch = FetchDirectoryAsync(cancellationToken);
            Volatile.Write(ref _directory, fetch);
            return fetch;
        }

        /// <summary>Drops the cached directory so the next request fetches it again.</summary>
        private void InvalidateDirectory() => Volatile.Write(ref _directory, null);

        /// <summary>GETs the directory and deserializes it. An empty body throws <see cref="AcmeCaException"/>.</summary>
        /// <param name="cancellationToken">Cancels the GET.</param>
        /// <returns>The directory document.</returns>
        private async Task<AcmeDirectoryResource> FetchDirectoryAsync(CancellationToken cancellationToken)
        {
            AcmeResponse<AcmeDirectoryResource> response = await SendUnsignedWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, _directoryUri),
                AcmeJsonContext.Default.AcmeDirectoryResource,
                cancellationToken).ConfigureAwait(false);

            return response.Content ?? throw new AcmeCaException("The ACME directory response was empty.");
        }

        /// <summary>
        /// POSTs a JWS-signed payload. Fresh nonces are signed for each attempt.
        /// <see cref="AcmeRequestSemantics.StateChanging"/> does not replay after an ambiguous outcome;
        /// <see cref="AcmeErrorTypes.BadNonce"/> may still re-sign because that response proves rejection.
        /// </summary>
        /// <typeparam name="T">Deserialized success body. Unused when the body is empty.</typeparam>
        /// <param name="key">Account key that signs the JWS.</param>
        /// <param name="keyId">Account URL (<c>kid</c>). <see langword="null"/> sends the JWK instead, used for <c>newAccount</c>.</param>
        /// <param name="url">Request URL, also the JWS <c>url</c> field.</param>
        /// <param name="payload">JSON payload. An empty string is POST-as-GET.</param>
        /// <param name="typeInfo">Source-generated metadata for <typeparamref name="T"/>.</param>
        /// <param name="semantics">Retry policy for this operation.</param>
        /// <param name="cancellationToken">Cancels nonce fetch and the POST.</param>
        /// <returns>The successful response.</returns>
        /// <exception cref="AcmeAmbiguousOutcomeException">
        /// Thrown for state-changing POSTs when transmission may have succeeded without a trustworthy response.
        /// </exception>
        /// <exception cref="AcmeCaException">Thrown when badNonce persists for <see cref="MaxBadNonceRetries"/> attempts, or the CA rejects the request.</exception>
        internal async Task<AcmeResponse<T>> PostAsync<T>(
            AcmeAccountKey key,
            string? keyId,
            Uri url,
            string payload,
            JsonTypeInfo<T> typeInfo,
            AcmeRequestSemantics semantics,
            CancellationToken cancellationToken)
        {
            bool allowTransientRetry = semantics is AcmeRequestSemantics.SafeRead or AcmeRequestSemantics.IdempotentWrite;

            for (int transientAttempt = 0; ; transientAttempt++)
            {
                Exception? lastBadNonce = null;

                for (int badNonceAttempt = 0; badNonceAttempt < MaxBadNonceRetries; badNonceAttempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string nonce = await TakeNonceAsync(cancellationToken).ConfigureAwait(false);
                    string jws = JsonWebSignature.Encode(key, url, nonce, keyId, payload);

                    AcmeSendOutcome<T> outcome = await SendJwsOnceAsync(url, jws, accept: null, typeInfo, cancellationToken)
                        .ConfigureAwait(false);

                    switch (outcome.Kind)
                    {
                        case AcmeSendKind.Success:
                            return outcome.Response!;

                        case AcmeSendKind.BadNonce:
                            lastBadNonce = outcome.Exception;
                            AcmeLogMessages.AcmeBadNonceRetry(_logger, url);
                            continue;

                        case AcmeSendKind.Fatal:
                            throw outcome.Exception!;

                        case AcmeSendKind.Ambiguous:
                        case AcmeSendKind.TransientHttp:
                            if (!allowTransientRetry)
                            {
                                AcmeLogMessages.AcmeAmbiguousOutcome(_logger, url);
                                throw CreateAmbiguousException(url, outcome);
                            }

                            if (transientAttempt >= MaxTransientRetries)
                            {
                                InvalidateDirectory();
                                throw outcome.Exception
                                    ?? new AcmeCaException(
                                        $"The request to '{url}' failed after {transientAttempt + 1} attempts.",
                                        outcome.TransportException!);
                            }

                            TimeSpan delay = outcome.RetryDelay
                                ?? TimeSpan.FromSeconds(Math.Pow(2, transientAttempt));
                            if (delay <= TimeSpan.Zero || delay > MaxRetryDelay)
                            {
                                throw outcome.Exception
                                    ?? new AcmeCaException($"The request to '{url}' failed with a non-retryable delay.");
                            }

                            if (outcome.Kind == AcmeSendKind.TransientHttp)
                            {
                                AcmeLogMessages.AcmeServerErrorRetry(_logger, outcome.StatusCode ?? 0, delay);
                            }
                            else
                            {
                                AcmeLogMessages.AcmeTransportRetry(_logger, delay, outcome.TransportException!);
                            }

                            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                            goto NextTransientAttempt;

                        default:
                            throw new InvalidOperationException($"Unexpected ACME send kind '{outcome.Kind}'.");
                    }
                }

                throw new AcmeCaException(
                    $"The certificate authority repeatedly rejected the replay nonce for '{url}'.",
                    lastBadNonce ?? new AcmeCaException("badNonce"));

            NextTransientAttempt:
                ;
            }
        }

        /// <summary>POST-as-GET (empty JWS payload) with <see cref="AcmeRequestSemantics.SafeRead"/> retries.</summary>
        /// <typeparam name="T">Deserialized success body.</typeparam>
        /// <param name="key">Account key that signs the JWS.</param>
        /// <param name="keyId">Account URL (<c>kid</c>). Required.</param>
        /// <param name="url">Resource URL.</param>
        /// <param name="typeInfo">Source-generated metadata for <typeparamref name="T"/>.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The successful response.</returns>
        internal Task<AcmeResponse<T>> PostAsGetAsync<T>(
            AcmeAccountKey key,
            string keyId,
            Uri url,
            JsonTypeInfo<T> typeInfo,
            CancellationToken cancellationToken) =>
            PostAsync(key, keyId, url, string.Empty, typeInfo, AcmeRequestSemantics.SafeRead, cancellationToken);

        /// <summary>POST-as-GET returning the raw body (certificate PEM chain).</summary>
        /// <param name="key">Account key that signs the JWS.</param>
        /// <param name="keyId">Account URL (<c>kid</c>).</param>
        /// <param name="url">Certificate URL.</param>
        /// <param name="accept"><c>Accept</c> media type sent with the POST.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The raw body and parsed Link headers. The body is not deserialized as JSON.</returns>
        /// <exception cref="AcmeCaException">Thrown when badNonce persists for <see cref="MaxBadNonceRetries"/> attempts.</exception>
        internal async Task<AcmeRawResponse> PostAsGetRawAsync(
            AcmeAccountKey key,
            string keyId,
            Uri url,
            string accept,
            CancellationToken cancellationToken)
        {
            for (int transientAttempt = 0; ; transientAttempt++)
            {
                Exception? lastBadNonce = null;

                for (int badNonceAttempt = 0; badNonceAttempt < MaxBadNonceRetries; badNonceAttempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string nonce = await TakeNonceAsync(cancellationToken).ConfigureAwait(false);
                    string jws = JsonWebSignature.Encode(key, url, nonce, keyId, string.Empty);

                    AcmeSendOutcome<object> outcome = await SendJwsOnceAsync<object>(
                        url, jws, accept, typeInfo: null, cancellationToken).ConfigureAwait(false);

                    switch (outcome.Kind)
                    {
                        case AcmeSendKind.Success:
                            return new AcmeRawResponse(outcome.Response!.RawBody, outcome.Response.Links);
                        case AcmeSendKind.BadNonce:
                            lastBadNonce = outcome.Exception;
                            AcmeLogMessages.AcmeBadNonceRetry(_logger, url);
                            continue;
                        case AcmeSendKind.Fatal:
                            throw outcome.Exception!;
                        case AcmeSendKind.Ambiguous:
                        case AcmeSendKind.TransientHttp:
                            if (transientAttempt >= MaxTransientRetries)
                            {
                                InvalidateDirectory();
                                throw outcome.Exception
                                    ?? new AcmeCaException(
                                        $"The request to '{url}' failed after {transientAttempt + 1} attempts.",
                                        outcome.TransportException!);
                            }

                            TimeSpan delay = outcome.RetryDelay
                                ?? TimeSpan.FromSeconds(Math.Pow(2, transientAttempt));
                            if (delay <= TimeSpan.Zero || delay > MaxRetryDelay)
                            {
                                throw outcome.Exception
                                    ?? new AcmeCaException($"The request to '{url}' failed with a non-retryable delay.");
                            }

                            if (outcome.Kind == AcmeSendKind.TransientHttp)
                            {
                                AcmeLogMessages.AcmeServerErrorRetry(_logger, outcome.StatusCode ?? 0, delay);
                            }
                            else
                            {
                                AcmeLogMessages.AcmeTransportRetry(_logger, delay, outcome.TransportException!);
                            }

                            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                            goto NextTransient;
                        default:
                            throw new InvalidOperationException($"Unexpected ACME send kind '{outcome.Kind}'.");
                    }
                }

                throw new AcmeCaException(
                    $"The certificate authority repeatedly rejected the replay nonce for '{url}'.",
                    lastBadNonce ?? new AcmeCaException("badNonce"));

            NextTransient:
                ;
            }
        }

        /// <summary>
        /// Builds a POST whose body is the compact JWS and whose content type is exactly <see cref="JoseContentType"/>.
        /// </summary>
        /// <param name="url">Request URL.</param>
        /// <param name="jws">Serialized JWS JSON.</param>
        /// <param name="accept">Optional <c>Accept</c> value. <see langword="null"/> sends no Accept header.</param>
        /// <returns>The request. The caller owns disposal.</returns>
        private static HttpRequestMessage CreateJwsRequest(Uri url, string jws, string? accept)
        {
            var content = new StringContent(jws, Encoding.UTF8);

            // RFC 8555 §6.2 requires exactly "application/jose+json" (no charset parameter).
            content.Headers.ContentType = new MediaTypeHeaderValue(JoseContentType);

            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

            if (accept is not null)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            }

            return request;
        }

        /// <summary>
        /// Returns a cached replay nonce, or HEAD <c>newNonce</c> when the pool is empty.
        /// A failed HEAD invalidates the directory cache.
        /// </summary>
        /// <param name="cancellationToken">Cancels the directory fetch and the HEAD.</param>
        /// <returns>One nonce string.</returns>
        /// <exception cref="AcmeCaException">Thrown when <c>newNonce</c> is missing or the response has no <c>Replay-Nonce</c>.</exception>
        private async Task<string> TakeNonceAsync(CancellationToken cancellationToken)
        {
            if (_nonces.TryPop(out string? nonce))
            {
                return nonce;
            }

            AcmeDirectoryResource directory = await GetDirectoryAsync(cancellationToken).ConfigureAwait(false);
            Uri newNonce = directory.NewNonce
                ?? throw new AcmeCaException("The ACME directory does not advertise a newNonce endpoint.");

            using var request = new HttpRequestMessage(HttpMethod.Head, newNonce);
            using HttpClient client = CreateClient();

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       && !cancellationToken.IsCancellationRequested)
            {
                InvalidateDirectory();
                throw;
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    InvalidateDirectory();
                }

                CaptureNonce(response);
            }

            if (_nonces.TryPop(out nonce))
            {
                return nonce;
            }

            throw new AcmeCaException("The certificate authority did not supply a replay nonce.");
        }

        /// <summary>
        /// Sends one JWS POST and classifies the outcome. Does not retry.
        /// Caller cancellation is never classified as ambiguous.
        /// </summary>
        private async Task<AcmeSendOutcome<T>> SendJwsOnceAsync<T>(
            Uri url,
            string jws,
            string? accept,
            JsonTypeInfo<T>? typeInfo,
            CancellationToken cancellationToken)
        {
            using HttpRequestMessage request = CreateJwsRequest(url, jws, accept);
            return await SendOnceAsync(request, typeInfo, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends an unsigned request (directory GET) with the same transient retry policy as safe reads.
        /// </summary>
        private async Task<AcmeResponse<T>> SendUnsignedWithRetryAsync<T>(
            Func<HttpRequestMessage> requestFactory,
            JsonTypeInfo<T>? typeInfo,
            CancellationToken cancellationToken)
        {
            for (int attempt = 0; ; attempt++)
            {
                using HttpRequestMessage request = requestFactory();
                AcmeSendOutcome<T> outcome = await SendOnceAsync(request, typeInfo, cancellationToken).ConfigureAwait(false);

                switch (outcome.Kind)
                {
                    case AcmeSendKind.Success:
                        return outcome.Response!;
                    case AcmeSendKind.Fatal:
                        throw outcome.Exception!;
                    case AcmeSendKind.Ambiguous:
                    case AcmeSendKind.TransientHttp:
                        if (attempt >= MaxTransientRetries)
                        {
                            InvalidateDirectory();
                            throw outcome.Exception
                                ?? new AcmeCaException(
                                    $"The request to '{request.RequestUri}' failed after {attempt + 1} attempts.",
                                    outcome.TransportException!);
                        }

                        TimeSpan delay = outcome.RetryDelay ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        if (delay <= TimeSpan.Zero || delay > MaxRetryDelay)
                        {
                            throw outcome.Exception
                                ?? new AcmeCaException($"The request to '{request.RequestUri}' failed with a non-retryable delay.");
                        }

                        if (outcome.Kind == AcmeSendKind.TransientHttp)
                        {
                            AcmeLogMessages.AcmeServerErrorRetry(_logger, outcome.StatusCode ?? 0, delay);
                        }
                        else
                        {
                            AcmeLogMessages.AcmeTransportRetry(_logger, delay, outcome.TransportException!);
                        }

                        await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                        continue;
                    default:
                        throw new InvalidOperationException($"Unexpected ACME send kind '{outcome.Kind}'.");
                }
            }
        }

        /// <summary>
        /// Performs a single HTTP send and classifies success, badNonce, fatal protocol errors,
        /// retryable HTTP 5xx, or an ambiguous transport outcome. Does not retry.
        /// </summary>
        private async Task<AcmeSendOutcome<T>> SendOnceAsync<T>(
            HttpRequestMessage request,
            JsonTypeInfo<T>? typeInfo,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            try
            {
                using HttpClient client = CreateClient();
                response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // HttpClient may have transmitted the request; absence of a response is not proof of rejection.
                return AcmeSendOutcome<T>.Ambiguous(ex);
            }

            using (response)
            {
                CaptureNonce(response);
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                DateTimeOffset? retryAfter = ParseRetryAfter(response);

                if (response.IsSuccessStatusCode)
                {
                    T? content = typeInfo is null || body.Length == 0 ? default : Deserialize(body, typeInfo);
                    return AcmeSendOutcome<T>.Success(new AcmeResponse<T>(
                        response.StatusCode,
                        content,
                        response.Headers.Location,
                        ParseLinks(response),
                        body,
                        retryAfter));
                }

                AcmeProblem? problem = TryParseProblem(body);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    InvalidateDirectory();
                }

                if (problem?.Type == AcmeErrorTypes.BadNonce)
                {
                    return AcmeSendOutcome<T>.BadNonce(
                        CreateException(response.StatusCode, problem, retryAfter, request.RequestUri, body));
                }

                if (ShouldRetry(response.StatusCode, problem))
                {
                    TimeSpan? delay = retryAfter is { } retryAt ? retryAt - _time.GetUtcNow() : null;
                    return AcmeSendOutcome<T>.TransientHttp(
                        (int)response.StatusCode,
                        delay,
                        CreateException(response.StatusCode, problem, retryAfter, request.RequestUri, body));
                }

                return AcmeSendOutcome<T>.Fatal(
                    CreateException(response.StatusCode, problem, retryAfter, request.RequestUri, body));
            }
        }

        /// <summary>Builds <see cref="AcmeAmbiguousOutcomeException"/> from a classified send failure.</summary>
        private static AcmeAmbiguousOutcomeException CreateAmbiguousException<T>(Uri url, AcmeSendOutcome<T> outcome)
        {
            string message =
                $"The ACME request to '{url}' may have been accepted, but no trustworthy response was received. The request will not be replayed.";
            if (outcome.TransportException is not null)
            {
                return new AcmeAmbiguousOutcomeException(message, url, outcome.TransportException);
            }

            if (outcome.Exception is not null)
            {
                return new AcmeAmbiguousOutcomeException(message, url, outcome.Exception);
            }

            return new AcmeAmbiguousOutcomeException(message, url);
        }

        /// <summary>
        /// Returns whether the status is 500, 502, 503, or 504 and the problem is not <see cref="AcmeErrorTypes.BadNonce"/>.
        /// </summary>
        /// <param name="statusCode">HTTP status.</param>
        /// <param name="problem">Parsed problem, or <see langword="null"/> when the body was not a problem document.</param>
        /// <returns><see langword="true"/> when another attempt may be made for safe/idempotent operations.</returns>
        private static bool ShouldRetry(HttpStatusCode statusCode, AcmeProblem? problem)
        {
            if (problem?.Type == AcmeErrorTypes.BadNonce)
            {
                return false;
            }

            return statusCode is HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;
        }

        /// <summary>
        /// Builds <see cref="AcmeCaRateLimitException"/> for HTTP 429 or <see cref="AcmeErrorTypes.RateLimited"/>;
        /// otherwise <see cref="AcmeCaException"/>.
        /// </summary>
        private static AcmeCaException CreateException(
            HttpStatusCode statusCode,
            AcmeProblem? problem,
            DateTimeOffset? retryAfter,
            Uri? requestUri,
            string body)
        {
            string detail = problem?.Detail ?? (body.Length <= 512 ? body : body[..512]);
            string message = string.Create(
                CultureInfo.InvariantCulture,
                $"The certificate authority rejected the request to '{requestUri}' with status {(int)statusCode}: {detail}");

            if (statusCode == HttpStatusCode.TooManyRequests || problem?.Type == AcmeErrorTypes.RateLimited)
            {
                return new AcmeCaRateLimitException(message, problem?.Detail, retryAfter);
            }

            return new AcmeCaException(message, problem?.Type, problem?.Detail, (int)statusCode);
        }

        /// <summary>Deserializes a success body. A <see cref="JsonException"/> becomes <see cref="AcmeCaException"/>.</summary>
        private static T? Deserialize<T>(string body, JsonTypeInfo<T> typeInfo)
        {
            try
            {
                return JsonSerializer.Deserialize(body, typeInfo);
            }
            catch (JsonException ex)
            {
                throw new AcmeCaException("The certificate authority returned a response that could not be parsed.", ex);
            }
        }

        /// <summary>Parses a problem document. An empty body or invalid JSON returns <see langword="null"/>.</summary>
        private static AcmeProblem? TryParseProblem(string body)
        {
            if (body.Length == 0)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize(body, AcmeJsonContext.Default.AcmeProblem);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Pushes every non-empty <c>Replay-Nonce</c> header value onto <see cref="_nonces"/>.</summary>
        private void CaptureNonce(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Replay-Nonce", out IEnumerable<string>? values))
            {
                return;
            }

            foreach (string value in values)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    _nonces.Push(value);
                }
            }
        }

        /// <summary>
        /// Converts <c>Retry-After</c> to an absolute UTC time using <see cref="_time"/> when the header is a delta.
        /// </summary>
        private DateTimeOffset? ParseRetryAfter(HttpResponseMessage response)
        {
            RetryConditionHeaderValue? header = response.Headers.RetryAfter;
            if (header is null)
            {
                return null;
            }

            if (header.Date is { } date)
            {
                return date;
            }

            return header.Delta is { } delta ? _time.GetUtcNow() + delta : null;
        }

        /// <summary>Parses RFC 8288 <c>Link</c> headers into absolute URL and relation pairs. Malformed entries are skipped.</summary>
        private static List<AcmeLink> ParseLinks(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Link", out IEnumerable<string>? values))
            {
                return [];
            }

            var links = new List<AcmeLink>();
            foreach (string value in values)
            {
                foreach (string candidate in SplitLinkHeader(value))
                {
                    int start = candidate.IndexOf('<', StringComparison.Ordinal);
                    int end = candidate.IndexOf('>', StringComparison.Ordinal);
                    if (start < 0 || end <= start)
                    {
                        continue;
                    }

                    if (Uri.TryCreate(candidate[(start + 1)..end], UriKind.Absolute, out Uri? url))
                    {
                        links.Add(new AcmeLink(url, ExtractRelation(candidate[(end + 1)..])));
                    }
                }
            }

            return links;
        }

        /// <summary>Splits a Link header on commas that are outside angle brackets.</summary>
        private static IEnumerable<string> SplitLinkHeader(string value)
        {
            int depth = 0;
            int start = 0;

            for (int i = 0; i < value.Length; i++)
            {
                switch (value[i])
                {
                    case '<':
                        depth++;
                        break;
                    case '>':
                        depth--;
                        break;
                    case ',' when depth == 0:
                        yield return value[start..i];
                        start = i + 1;
                        break;
                }
            }

            if (start < value.Length)
            {
                yield return value[start..];
            }
        }

        /// <summary>Reads the <c>rel</c> parameter. Returns an empty string when it is absent.</summary>
        private static string ExtractRelation(string parameters)
        {
            foreach (string part in parameters.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!part.StartsWith("rel", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int equals = part.IndexOf('=', StringComparison.Ordinal);
                if (equals >= 0)
                {
                    return part[(equals + 1)..].Trim().Trim('"');
                }
            }

            return string.Empty;
        }

        /// <summary>Classification of a single HTTP attempt.</summary>
        private enum AcmeSendKind
        {
            /// <summary>Successful ACME response.</summary>
            Success,

            /// <summary>CA returned <see cref="AcmeErrorTypes.BadNonce"/>; the signed request was rejected.</summary>
            BadNonce,

            /// <summary>Definitive protocol or client error that must not be retried as a transient.</summary>
            Fatal,

            /// <summary>HTTP 5xx that safe/idempotent operations may retry with a fresh request.</summary>
            TransientHttp,

            /// <summary>Transport failure or timeout after the request may have been transmitted.</summary>
            Ambiguous,
        }

        /// <summary>Result of <see cref="SendOnceAsync{T}"/>.</summary>
        /// <typeparam name="T">Deserialized body type.</typeparam>
        private sealed class AcmeSendOutcome<T>
        {
            private AcmeSendOutcome(
                AcmeSendKind kind,
                AcmeResponse<T>? response,
                AcmeCaException? exception,
                Exception? transportException,
                int? statusCode,
                TimeSpan? retryDelay)
            {
                Kind = kind;
                Response = response;
                Exception = exception;
                TransportException = transportException;
                StatusCode = statusCode;
                RetryDelay = retryDelay;
            }

            /// <summary>Outcome kind.</summary>
            internal AcmeSendKind Kind { get; }

            /// <summary>Success response when <see cref="Kind"/> is <see cref="AcmeSendKind.Success"/>.</summary>
            internal AcmeResponse<T>? Response { get; }

            /// <summary>Protocol exception for badNonce, fatal, or transient HTTP failures.</summary>
            internal AcmeCaException? Exception { get; }

            /// <summary>Underlying transport exception for ambiguous outcomes.</summary>
            internal Exception? TransportException { get; }

            /// <summary>HTTP status for transient HTTP failures.</summary>
            internal int? StatusCode { get; }

            /// <summary>Suggested delay before a safe/idempotent retry.</summary>
            internal TimeSpan? RetryDelay { get; }

            /// <summary>Creates a success outcome.</summary>
            internal static AcmeSendOutcome<T> Success(AcmeResponse<T> response) =>
                new(AcmeSendKind.Success, response, null, null, null, null);

            /// <summary>Creates a badNonce outcome.</summary>
            internal static AcmeSendOutcome<T> BadNonce(AcmeCaException exception) =>
                new(AcmeSendKind.BadNonce, null, exception, null, null, null);

            /// <summary>Creates a fatal protocol outcome.</summary>
            internal static AcmeSendOutcome<T> Fatal(AcmeCaException exception) =>
                new(AcmeSendKind.Fatal, null, exception, null, null, null);

            /// <summary>Creates a transient HTTP 5xx outcome.</summary>
            /// <param name="statusCode">HTTP status.</param>
            /// <param name="retryDelay">Retry-After delay when present; <see langword="null"/> selects exponential backoff.</param>
            /// <param name="exception">Exception describing the 5xx response.</param>
            internal static AcmeSendOutcome<T> TransientHttp(int statusCode, TimeSpan? retryDelay, AcmeCaException exception) =>
                new(AcmeSendKind.TransientHttp, null, exception, null, statusCode, retryDelay);

            /// <summary>Creates an ambiguous transport outcome.</summary>
            internal static AcmeSendOutcome<T> Ambiguous(Exception transportException) =>
                new(AcmeSendKind.Ambiguous, null, null, transportException, null, null);
        }
    }
}
