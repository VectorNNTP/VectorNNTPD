using System.Text;
using System.Text.Json;

namespace VectorNNTPD.Common.Acme.Protocol
{
    /// <summary>
    /// ACME v2 protocol client for directory, account, order, authorization, challenge, finalize, and certificate download.
    /// </summary>
    internal sealed class AcmeClient
    {
        /// <summary><c>Accept</c> value sent when downloading a certificate chain.</summary>
        private const string PemChainContentType = "application/pem-certificate-chain";

        /// <summary>Upper bound applied to a CA Retry-After delay during order polling.</summary>
        private static readonly TimeSpan MaxPollDelay = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Bound for finalize recovery polling after an ambiguous finalize POST.
        /// The order may remain <see cref="AcmeStatus.Ready"/> briefly even when finalize was accepted.
        /// </summary>
        private static readonly TimeSpan FinalizeRecoveryTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Minimum delay between finalize recovery polls when the CA supplies no Retry-After.</summary>
        private static readonly TimeSpan FinalizeRecoveryPollInterval = TimeSpan.FromSeconds(1);

        /// <summary>Transport that signs and sends JWS requests.</summary>
        private readonly AcmeHttpTransport _http;

        /// <summary>Account key used for every JWS. Not disposed by this client.</summary>
        private readonly AcmeAccountKey _accountKey;

        /// <summary>Logger for the account-registered event.</summary>
        private readonly ILogger _logger;

        /// <summary>Clock used for the order-poll deadline and Retry-After delays.</summary>
        private readonly TimeProvider _time;

        /// <summary>Account Location URL bound as <c>kid</c>. <see langword="null"/> until registration or <see cref="BindExistingAccount"/>.</summary>
        private string? _keyId;

        /// <summary>Stores the transport, account key, logger, and clock. Does not contact the CA.</summary>
        /// <param name="http">JWS transport aimed at one directory.</param>
        /// <param name="accountKey">Key that signs requests. The caller retains ownership.</param>
        /// <param name="logger">Logger for account registration.</param>
        /// <param name="time">Clock for order polling.</param>
        internal AcmeClient(AcmeHttpTransport http, AcmeAccountKey accountKey, ILogger logger, TimeProvider time)
        {
            _http = http;
            _accountKey = accountKey;
            _logger = logger;
            _time = time;
        }

        /// <summary>Gets the bound account URL (kid), or throws if not registered/bound.</summary>
        internal string KeyId => _keyId ?? throw new InvalidOperationException("The ACME account has not been registered yet.");

        /// <summary>
        /// Binds a previously persisted ACME account URL (kid) without calling newAccount.
        /// </summary>
        /// <param name="accountUrl">Account Location URL. Whitespace throws <see cref="ArgumentException"/>.</param>
        internal void BindExistingAccount(string accountUrl)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountUrl);
            _keyId = accountUrl.Trim();
        }

        /// <summary>Fetches the ACME directory.</summary>
        /// <param name="cancellationToken">Passed to the transport directory cache.</param>
        /// <returns>The cached or newly fetched directory.</returns>
        private Task<AcmeDirectoryResource> GetDirectoryAsync(CancellationToken cancellationToken) =>
            _http.GetDirectoryAsync(cancellationToken);

        /// <summary>POST-as-GET for an order resource.</summary>
        /// <param name="orderUrl">Order Location URL.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The order JSON. An empty body throws <see cref="AcmeCaException"/>.</returns>
        internal async Task<AcmeOrderResource> GetOrderAsync(Uri orderUrl, CancellationToken cancellationToken)
        {
            AcmeResponse<AcmeOrderResource> response = await _http.PostAsGetAsync(
                _accountKey, KeyId, orderUrl, AcmeJsonContext.Default.AcmeOrderResource, cancellationToken)
                .ConfigureAwait(false);

            return response.Content ?? throw new AcmeCaException("The certificate authority returned an empty order.");
        }

        /// <summary>
        /// Registers a new account (or reuses one for this key) and returns the account Location URL.
        /// The JWS uses the account JWK because no <c>kid</c> is sent. The Location URL is stored as <see cref="KeyId"/>.
        /// Same-key registration is idempotent (RFC 8555 §7.3), so the transport may retry with a fresh JWS.
        /// </summary>
        /// <param name="contacts">Contact URIs. An empty list omits the <c>contact</c> field.</param>
        /// <param name="termsOfServiceAgreed">Value of <c>termsOfServiceAgreed</c>.</param>
        /// <param name="externalAccountBinding">Optional EAB. <see langword="null"/> omits <c>externalAccountBinding</c>.</param>
        /// <param name="cancellationToken">Cancels the directory fetch and the POST.</param>
        /// <returns>The account Location URL.</returns>
        internal async Task<string> RegisterAccountAsync(
            IReadOnlyList<string> contacts,
            bool termsOfServiceAgreed,
            ExternalAccountBinding? externalAccountBinding,
            CancellationToken cancellationToken)
        {
            AcmeDirectoryResource directory = await _http.GetDirectoryAsync(cancellationToken).ConfigureAwait(false);
            Uri newAccount = directory.NewAccount
                ?? throw new AcmeCaException("The ACME directory does not advertise a newAccount endpoint.");

            string payload = BuildAccountPayload(_accountKey, contacts, termsOfServiceAgreed, externalAccountBinding, newAccount);

            AcmeResponse<AcmeAccountResource> response = await _http.PostAsync(
                _accountKey,
                keyId: null,
                newAccount,
                payload,
                AcmeJsonContext.Default.AcmeAccountResource,
                AcmeRequestSemantics.IdempotentWrite,
                cancellationToken).ConfigureAwait(false);

            Uri location = response.Location
                ?? throw new AcmeCaException("The certificate authority did not return an account URL.");

            _keyId = location.AbsoluteUri;
            AcmeLogMessages.AcmeProtocolAccountRegistered(_logger, _keyId);
            return _keyId;
        }

        /// <summary>
        /// Creates a new order for the given identifiers.
        /// After an ambiguous <c>newOrder</c> outcome, does not submit a second <c>newOrder</c>.
        /// ACME does not correlate a lost <c>newOrder</c> response to a specific order URL; a unique
        /// identifier match on the account orders list is not reliable under concurrency, pre-existing
        /// orders, or incomplete/paginated lists, so recovery fails closed with an explicit ambiguous error.
        /// </summary>
        /// <param name="identifiers">Order identifiers. No profile is requested.</param>
        /// <param name="cancellationToken">Cancels the directory fetch and the POST.</param>
        /// <returns>The order Location URL and the created order JSON.</returns>
        internal async Task<AcmeOrder> CreateOrderAsync(
            IReadOnlyList<AcmeIdentifier> identifiers,
            CancellationToken cancellationToken)
        {
            AcmeDirectoryResource directory = await _http.GetDirectoryAsync(cancellationToken).ConfigureAwait(false);
            Uri newOrder = directory.NewOrder
                ?? throw new AcmeCaException("The ACME directory does not advertise a newOrder endpoint.");

            string payload = BuildOrderPayload(identifiers);

            try
            {
                AcmeResponse<AcmeOrderResource> response = await _http.PostAsync(
                    _accountKey,
                    KeyId,
                    newOrder,
                    payload,
                    AcmeJsonContext.Default.AcmeOrderResource,
                    AcmeRequestSemantics.StateChanging,
                    cancellationToken).ConfigureAwait(false);

                AcmeOrderResource order = response.Content
                    ?? throw new AcmeCaException("The certificate authority returned an empty order.");
                Uri location = response.Location
                    ?? throw new AcmeCaException("The certificate authority did not return an order URL.");

                return new AcmeOrder(location, order);
            }
            catch (AcmeAmbiguousOutcomeException ambiguous)
            {
                // Best-effort inspection for operator diagnostics; never adopt an order by identifier match alone.
                string detail = await DescribeUnrecoverableNewOrderAsync(identifiers, cancellationToken)
                    .ConfigureAwait(false);
                throw new AcmeAmbiguousOutcomeException(
                    $"newOrder may have been accepted, but the resulting order could not be identified reliably ({detail}). A second newOrder was not sent.",
                    ambiguous.RequestUri,
                    ambiguous);
            }
        }

        /// <summary>POST-as-GET for an authorization resource.</summary>
        /// <param name="authorizationUrl">Authorization URL from the order.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The authorization JSON. An empty body throws <see cref="AcmeCaException"/>.</returns>
        internal async Task<AcmeAuthorizationResource> GetAuthorizationAsync(Uri authorizationUrl, CancellationToken cancellationToken)
        {
            AcmeResponse<AcmeAuthorizationResource> response = await _http.PostAsGetAsync(
                _accountKey, KeyId, authorizationUrl, AcmeJsonContext.Default.AcmeAuthorizationResource, cancellationToken)
                .ConfigureAwait(false);

            return response.Content ?? throw new AcmeCaException("The certificate authority returned an empty authorization.");
        }

        /// <summary>
        /// Submits a challenge for validation (empty JSON object payload).
        /// After an ambiguous outcome, POST-as-GETs the challenge URL and inspects challenge status
        /// (<c>pending</c>, <c>processing</c>, <c>valid</c>, <c>invalid</c> per RFC 8555 §7.1.5).
        /// Does not replay the challenge POST.
        /// </summary>
        /// <param name="challengeUrl">Challenge URL.</param>
        /// <param name="cancellationToken">Cancels the POST of <c>{}</c>.</param>
        /// <returns>A task that completes when the CA accepts the POST or recovery observes progress.</returns>
        internal async Task SubmitChallengeAsync(Uri challengeUrl, CancellationToken cancellationToken)
        {
            try
            {
                await _http.PostAsync(
                    _accountKey,
                    KeyId,
                    challengeUrl,
                    "{}",
                    AcmeJsonContext.Default.AcmeChallengeResource,
                    AcmeRequestSemantics.StateChanging,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AcmeAmbiguousOutcomeException ambiguous)
            {
                AcmeChallengeResource challenge = await GetChallengeAsync(challengeUrl, cancellationToken).ConfigureAwait(false);
                if (IsChallengeAccepted(challenge.Status))
                {
                    return;
                }

                if (string.Equals(challenge.Status, AcmeStatus.Invalid, StringComparison.Ordinal))
                {
                    throw new AcmeCaException(
                        $"The certificate authority rejected the challenge: {challenge.Error?.Detail ?? "no detail supplied"}",
                        challenge.Error?.Type,
                        challenge.Error?.Detail,
                        challenge.Error?.Status);
                }

                throw new AcmeAmbiguousOutcomeException(
                    $"Challenge submission may have been accepted, but challenge status is '{challenge.Status ?? "null"}'. The challenge POST was not replayed.",
                    ambiguous.RequestUri,
                    ambiguous);
            }
        }

        /// <summary>
        /// Finalizes an order with a PKCS#10 CSR.
        /// After an ambiguous outcome, polls the known order URL until <c>processing</c>, <c>valid</c>,
        /// <c>invalid</c>, the recovery deadline, or cancellation.
        /// <see cref="AcmeStatus.Ready"/> is not treated as proof that finalize was rejected; finalize is not resubmitted.
        /// </summary>
        /// <param name="orderUrl">Order Location URL used for recovery polling.</param>
        /// <param name="finalizeUrl">Order <c>finalize</c> URL.</param>
        /// <param name="certificateSigningRequest">DER PKCS#10 bytes, sent as base64url <c>csr</c>.</param>
        /// <param name="cancellationToken">Cancels the POST and recovery polls.</param>
        /// <returns>The order JSON from the finalize response or from recovery polling.</returns>
        internal async Task<AcmeOrderResource> FinalizeOrderAsync(
            Uri orderUrl,
            Uri finalizeUrl,
            byte[] certificateSigningRequest,
            CancellationToken cancellationToken)
        {
            string payload = BuildFinalizePayload(certificateSigningRequest);

            try
            {
                AcmeResponse<AcmeOrderResource> response = await _http.PostAsync(
                    _accountKey,
                    KeyId,
                    finalizeUrl,
                    payload,
                    AcmeJsonContext.Default.AcmeOrderResource,
                    AcmeRequestSemantics.StateChanging,
                    cancellationToken).ConfigureAwait(false);

                return response.Content ?? throw new AcmeCaException("The certificate authority returned an empty order.");
            }
            catch (AcmeAmbiguousOutcomeException ambiguous)
            {
                return await PollFinalizeRecoveryAsync(orderUrl, ambiguous, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Polls the order until status is <c>valid</c> or <c>invalid</c>.</summary>
        /// <param name="orderUrl">Order Location URL.</param>
        /// <param name="timeout">Deadline measured with <see cref="_time"/>.</param>
        /// <param name="pollInterval">Minimum delay between polls. A shorter Retry-After is raised to this value; a longer one is capped at <see cref="MaxPollDelay"/>.</param>
        /// <param name="cancellationToken">Cancels a poll or the delay.</param>
        /// <returns>The order once <see cref="AcmeStatus.Valid"/>.</returns>
        /// <exception cref="AcmeCaException">Thrown when the status is <see cref="AcmeStatus.Invalid"/> or the deadline passes.</exception>
        internal async Task<AcmeOrderResource> WaitForOrderAsync(
            Uri orderUrl,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = _time.GetUtcNow() + timeout;

            while (true)
            {
                AcmeResponse<AcmeOrderResource> response = await _http.PostAsGetAsync(
                    _accountKey, KeyId, orderUrl, AcmeJsonContext.Default.AcmeOrderResource, cancellationToken)
                    .ConfigureAwait(false);
                AcmeOrderResource order = response.Content
                    ?? throw new AcmeCaException("The certificate authority returned an empty order.");

                switch (order.Status)
                {
                    case AcmeStatus.Valid:
                        return order;
                    case AcmeStatus.Invalid:
                        throw new AcmeCaException(
                            $"The certificate authority rejected the order: {order.Error?.Detail ?? "no detail supplied"}",
                            order.Error?.Type,
                            order.Error?.Detail,
                            order.Error?.Status);
                    default:
                        break;
                }

                if (_time.GetUtcNow() >= deadline)
                {
                    throw new AcmeCaException("Timed out waiting for the certificate authority to issue the certificate.");
                }

                await Task.Delay(PollDelay(response.RetryAfter, pollInterval), _time, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>Downloads the PEM certificate chain (RFC 8555 §7.4.2).</summary>
        /// <param name="certificateUrl">Order <c>certificate</c> URL.</param>
        /// <param name="cancellationToken">Cancels the POST-as-GET.</param>
        /// <returns>The PEM body and Link URLs whose relation is <c>alternate</c>.</returns>
        internal async Task<AcmeCertificate> DownloadCertificateAsync(Uri certificateUrl, CancellationToken cancellationToken)
        {
            AcmeRawResponse response = await _http.PostAsGetRawAsync(
                _accountKey, KeyId, certificateUrl, PemChainContentType, cancellationToken)
                .ConfigureAwait(false);

            var alternates = new List<Uri>();
            foreach (AcmeLink link in response.Links)
            {
                if (string.Equals(link.Relation, "alternate", StringComparison.OrdinalIgnoreCase))
                {
                    alternates.Add(link.Url);
                }
            }

            return new AcmeCertificate(response.Body, alternates);
        }

        /// <summary>
        /// Polls <paramref name="orderUrl"/> after an ambiguous finalize without resubmitting the CSR.
        /// <see cref="AcmeStatus.Ready"/> and <see cref="AcmeStatus.Pending"/> keep polling until the bound,
        /// cancellation, <see cref="AcmeStatus.Processing"/>/<see cref="AcmeStatus.Valid"/>, or <see cref="AcmeStatus.Invalid"/>.
        /// </summary>
        private async Task<AcmeOrderResource> PollFinalizeRecoveryAsync(
            Uri orderUrl,
            AcmeAmbiguousOutcomeException ambiguous,
            CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = _time.GetUtcNow() + FinalizeRecoveryTimeout;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                AcmeResponse<AcmeOrderResource> response = await _http.PostAsGetAsync(
                    _accountKey, KeyId, orderUrl, AcmeJsonContext.Default.AcmeOrderResource, cancellationToken)
                    .ConfigureAwait(false);
                AcmeOrderResource order = response.Content
                    ?? throw new AcmeCaException("The certificate authority returned an empty order.");

                if (IsFinalizeAccepted(order.Status))
                {
                    return order;
                }

                if (string.Equals(order.Status, AcmeStatus.Invalid, StringComparison.Ordinal))
                {
                    throw new AcmeCaException(
                        $"The certificate authority rejected the order: {order.Error?.Detail ?? "no detail supplied"}",
                        order.Error?.Type,
                        order.Error?.Detail,
                        order.Error?.Status);
                }

                // ready/pending/unknown: not proof of rejection; do not resubmit finalize.
                if (_time.GetUtcNow() >= deadline)
                {
                    throw new AcmeAmbiguousOutcomeException(
                        $"Finalize may have been accepted, but the order remained '{order.Status ?? "null"}' until the recovery deadline. Finalize was not resubmitted.",
                        ambiguous.RequestUri,
                        ambiguous);
                }

                await Task.Delay(
                        PollDelay(response.RetryAfter, FinalizeRecoveryPollInterval),
                        _time,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Inspects the account orders list for diagnostic detail after an ambiguous <c>newOrder</c>.
        /// Never returns an order URL: a unique identifier match is not proof that this request created that order.
        /// </summary>
        private async Task<string> DescribeUnrecoverableNewOrderAsync(
            IReadOnlyList<AcmeIdentifier> identifiers,
            CancellationToken cancellationToken)
        {
            try
            {
                AcmeResponse<AcmeAccountResource> accountResponse = await _http.PostAsGetAsync(
                    _accountKey,
                    KeyId,
                    new Uri(KeyId, UriKind.Absolute),
                    AcmeJsonContext.Default.AcmeAccountResource,
                    cancellationToken).ConfigureAwait(false);

                Uri? ordersUrl = accountResponse.Content?.Orders;
                if (ordersUrl is null)
                {
                    return "account orders URL is absent";
                }

                AcmeResponse<AcmeOrdersListResource> listResponse = await _http.PostAsGetAsync(
                    _accountKey,
                    KeyId,
                    ordersUrl,
                    AcmeJsonContext.Default.AcmeOrdersListResource,
                    cancellationToken).ConfigureAwait(false);

                if (HasNextOrdersPage(listResponse.Links))
                {
                    return "orders list is incomplete (Link rel=next present)";
                }

                IReadOnlyList<Uri>? orderUrls = listResponse.Content?.Orders;
                if (orderUrls is null || orderUrls.Count == 0)
                {
                    return "orders list is empty";
                }

                int matches = 0;
                foreach (Uri orderUrl in orderUrls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AcmeOrderResource resource = await GetOrderAsync(orderUrl, cancellationToken).ConfigureAwait(false);
                    if (string.Equals(resource.Status, AcmeStatus.Invalid, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (IdentifiersMatch(identifiers, resource.Identifiers))
                    {
                        matches++;
                    }
                }

                if (matches == 0)
                {
                    return "no non-invalid order matched the requested identifiers";
                }

                if (matches > 1)
                {
                    return "multiple non-invalid orders matched the requested identifiers";
                }

                return "a single matching order is not attributable to this request under ACME";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AcmeCaException)
            {
                return "orders list could not be inspected";
            }
            catch (HttpRequestException)
            {
                return "orders list could not be inspected";
            }
            catch (UriFormatException)
            {
                return "orders list could not be inspected";
            }
            catch (OperationCanceledException)
            {
                // HttpClient timeout or non-caller cancellation during best-effort inspection.
                return "orders list could not be inspected";
            }
        }

        /// <summary>Returns whether the orders list response advertises another page via Link <c>rel=next</c>.</summary>
        private static bool HasNextOrdersPage(IReadOnlyList<AcmeLink> links)
        {
            foreach (AcmeLink link in links)
            {
                if (string.Equals(link.Relation, "next", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>POST-as-GET for a challenge resource used after an ambiguous challenge POST.</summary>
        private async Task<AcmeChallengeResource> GetChallengeAsync(Uri challengeUrl, CancellationToken cancellationToken)
        {
            AcmeResponse<AcmeChallengeResource> response = await _http.PostAsGetAsync(
                _accountKey, KeyId, challengeUrl, AcmeJsonContext.Default.AcmeChallengeResource, cancellationToken)
                .ConfigureAwait(false);

            return response.Content ?? throw new AcmeCaException("The certificate authority returned an empty challenge.");
        }

        /// <summary>Returns whether challenge status shows the POST was accepted for validation.</summary>
        private static bool IsChallengeAccepted(string? status) =>
            string.Equals(status, AcmeStatus.Processing, StringComparison.Ordinal)
            || string.Equals(status, AcmeStatus.Valid, StringComparison.Ordinal);

        /// <summary>Returns whether order status shows finalize was accepted.</summary>
        private static bool IsFinalizeAccepted(string? status) =>
            string.Equals(status, AcmeStatus.Processing, StringComparison.Ordinal)
            || string.Equals(status, AcmeStatus.Valid, StringComparison.Ordinal);

        /// <summary>
        /// Returns whether both identifier collections contain the same (type, value) pairs, ignoring order.
        /// </summary>
        private static bool IdentifiersMatch(
            IReadOnlyList<AcmeIdentifier> requested,
            IReadOnlyList<AcmeIdentifier>? actual)
        {
            if (actual is null || actual.Count != requested.Count)
            {
                return false;
            }

            var remaining = new List<(string Type, string Value)>(actual.Count);
            foreach (AcmeIdentifier identifier in actual)
            {
                remaining.Add((identifier.Type, identifier.Value));
            }

            foreach (AcmeIdentifier identifier in requested)
            {
                int index = remaining.FindIndex(candidate =>
                    string.Equals(candidate.Type, identifier.Type, StringComparison.Ordinal)
                    && string.Equals(candidate.Value, identifier.Value, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    return false;
                }

                remaining.RemoveAt(index);
            }

            return remaining.Count == 0;
        }

        /// <summary>
        /// Chooses the next poll delay. No Retry-After uses <paramref name="pollInterval"/>.
        /// A requested delay below that interval is raised to it, and one above <see cref="MaxPollDelay"/> is capped.
        /// </summary>
        private TimeSpan PollDelay(DateTimeOffset? retryAfter, TimeSpan pollInterval)
        {
            if (retryAfter is not { } at)
            {
                return pollInterval;
            }

            TimeSpan requested = at - _time.GetUtcNow();
            if (requested < pollInterval)
            {
                return pollInterval;
            }

            return requested > MaxPollDelay ? MaxPollDelay : requested;
        }

        /// <summary>
        /// Builds the <c>newAccount</c> JSON. When <paramref name="externalAccountBinding"/> is set, appends an HS256 <c>externalAccountBinding</c> JWS over the account JWK.
        /// </summary>
        private static string BuildAccountPayload(
            AcmeAccountKey accountKey,
            IReadOnlyList<string> contacts,
            bool termsOfServiceAgreed,
            ExternalAccountBinding? externalAccountBinding,
            Uri newAccountUrl)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("termsOfServiceAgreed", termsOfServiceAgreed);

                if (contacts.Count > 0)
                {
                    writer.WriteStartArray("contact");
                    foreach (string contact in contacts)
                    {
                        writer.WriteStringValue(contact);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            if (externalAccountBinding is null)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }

            string body = Encoding.UTF8.GetString(stream.ToArray());
            string binding = JsonWebSignature.EncodeHmac(
                externalAccountBinding.GetHmacKey(),
                externalAccountBinding.KeyId,
                newAccountUrl,
                accountKey.Jwk);

            return body[..^1] + ",\"externalAccountBinding\":" + binding + "}";
        }

        /// <summary>Writes <c>{"identifiers":[{"type","value"}, ...]}</c>.</summary>
        private static string BuildOrderPayload(IReadOnlyList<AcmeIdentifier> identifiers)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("identifiers");

                foreach (AcmeIdentifier identifier in identifiers)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", identifier.Type);
                    writer.WriteString("value", identifier.Value);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>Writes <c>{"csr":"..."}</c> with the CSR as unpadded base64url.</summary>
        private static string BuildFinalizePayload(byte[] certificateSigningRequest)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("csr", Base64Url.Encode(certificateSigningRequest));
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
