using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VectorNNTPD.Common.Acme.Protocol;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Acme
{
    /// <summary>
    /// ACME v2 issuer using the owned protocol client with DNS-01 via <see cref="Dns01Solver"/>.
    /// </summary>
    /// <remarks>
    /// Account keys remain PKCS#8 DER on disk. Persisted TLS credentials remain PKCS#12/PFX via BCL.
    /// After DNS-01 challenges are triggered, the issuer polls until the ACME order is
    /// <c>ready</c> (or fails) before finalize.
    /// </remarks>
    internal sealed class AcmeIssuer : ICertificateIssuer
    {
        /// <summary>Named <see cref="IHttpClientFactory"/> client for ACME HTTP.</summary>
        public const string HttpClientName = "AcmeDirectory";

        /// <summary>RSA size, in bits, for both the account key and the leaf key.</summary>
        private const int KeySizeBits = 2048;

        /// <summary>Directory URL, contact email, PFX password, and renewal threshold.</summary>
        private readonly IOptions<AcmeCloudflareOptions> _options;

        /// <summary>Loads or creates the persisted ACME account key.</summary>
        private readonly AccountStore _accountStore;

        /// <summary>Places, waits for, and deletes DNS-01 TXT records.</summary>
        private readonly Dns01Solver _dnsSolver;

        /// <summary>Creates the HTTP client used by <see cref="AcmeHttpTransport"/>.</summary>
        private readonly IHttpClientFactory _httpClientFactory;

        /// <summary>Logger passed to the transport and client.</summary>
        private readonly ILogger<AcmeIssuer> _logger;

        /// <summary>Optional journal. Validation started/succeeded events are skipped when null or when no transaction is active.</summary>
        private readonly AcmeTransactionJournal? _journal;

        /// <summary>How long to wait for the order to become ready. Default is <see cref="AcmeOrderReadiness.DefaultTimeout"/>.</summary>
        private readonly TimeSpan _readinessTimeout;

        /// <summary>Delay between readiness polls. Default is <see cref="AcmeOrderReadiness.DefaultInterval"/>.</summary>
        private readonly TimeSpan _readinessInterval;

        /// <summary>
        /// Password supplied by <see cref="CertificateManager"/> for the next <see cref="IssueAsync"/>.
        /// Cleared when that call reads it. <see langword="null"/> means read <see cref="AcmeCloudflareOptions.AcmeCertificatePassword"/>.
        /// </summary>
        private string? _preparedIssuePassword;

        /// <summary>Uses <see cref="AcmeOrderReadiness.DefaultTimeout"/> and <see cref="AcmeOrderReadiness.DefaultInterval"/>.</summary>
        /// <param name="options">ACME directory, email, password, and renewal threshold.</param>
        /// <param name="accountStore">Persisted account key store.</param>
        /// <param name="dnsSolver">DNS-01 publisher for this certificate FQDN.</param>
        /// <param name="httpClientFactory">Factory for <see cref="HttpClientName"/>.</param>
        /// <param name="logger">Issuer logger.</param>
        /// <param name="journal">Optional transaction journal. <see langword="null"/> skips issuer journal events.</param>
        internal AcmeIssuer(
            IOptions<AcmeCloudflareOptions> options,
            AccountStore accountStore,
            Dns01Solver dnsSolver,
            IHttpClientFactory httpClientFactory,
            ILogger<AcmeIssuer> logger,
            AcmeTransactionJournal? journal = null)
            : this(
                options,
                accountStore,
                dnsSolver,
                httpClientFactory,
                logger,
                readinessTimeout: null,
                readinessInterval: null,
                journal: journal)
        {
        }

        /// <summary>Test constructor with injectable readiness poll timing.</summary>
        /// <param name="options">ACME directory, email, password, and renewal threshold.</param>
        /// <param name="accountStore">Persisted account key store.</param>
        /// <param name="dnsSolver">DNS-01 publisher.</param>
        /// <param name="httpClientFactory">Factory for <see cref="HttpClientName"/>.</param>
        /// <param name="logger">Issuer logger.</param>
        /// <param name="readinessTimeout">Order-ready wait. <see langword="null"/> uses <see cref="AcmeOrderReadiness.DefaultTimeout"/>.</param>
        /// <param name="readinessInterval">Order-ready poll delay. <see langword="null"/> uses <see cref="AcmeOrderReadiness.DefaultInterval"/>.</param>
        /// <param name="journal">Optional transaction journal.</param>
        internal AcmeIssuer(
            IOptions<AcmeCloudflareOptions> options,
            AccountStore accountStore,
            Dns01Solver dnsSolver,
            IHttpClientFactory httpClientFactory,
            ILogger<AcmeIssuer> logger,
            TimeSpan? readinessTimeout,
            TimeSpan? readinessInterval,
            AcmeTransactionJournal? journal = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(accountStore);
            ArgumentNullException.ThrowIfNull(dnsSolver);
            ArgumentNullException.ThrowIfNull(httpClientFactory);
            ArgumentNullException.ThrowIfNull(logger);
            _options = options;
            _accountStore = accountStore;
            _dnsSolver = dnsSolver;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _journal = journal;
            _readinessTimeout = readinessTimeout ?? AcmeOrderReadiness.DefaultTimeout;
            _readinessInterval = readinessInterval ?? AcmeOrderReadiness.DefaultInterval;
        }

        /// <summary>
        /// Encrypts the next <see cref="IssueAsync"/> PFX with <paramref name="password"/> instead of a later options read.
        /// </summary>
        /// <param name="password">PKCS#12 password for that attempt. Not logged.</param>
        /// <exception cref="ArgumentException"><paramref name="password"/> is null or whitespace.</exception>
        internal void UsePasswordForNextIssue(string password)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(password);
            _preparedIssuePassword = password;
        }

        /// <summary>
        /// Registers or reuses the ACME account, orders the DNS identifiers, publishes dns-01 TXT records,
        /// waits until the order is ready, finalizes a PKCS#10 CSR, and returns a validated PKCS#12.
        /// DNS records created for this call are deleted before return, including on failure.
        /// Account registration itself runs with <see cref="CancellationToken.None"/>.
        /// </summary>
        /// <param name="domains">DNS names for the order and CSR. Empty throws category <c>missing_identities</c>.</param>
        /// <param name="cancellationToken">Cancels order, DNS, and finalize work. A cancelled call still attempts DNS cleanup.</param>
        /// <returns>PFX material that passed <see cref="CertificateValidator.RequireValidPfx"/>.</returns>
        /// <remarks>
        /// When <see cref="UsePasswordForNextIssue"/> was called, that password encrypts the PFX.
        /// Otherwise the password is read from options at the start of this call.
        /// </remarks>
        public async Task<CertificateMaterial> IssueAsync(
            IReadOnlyList<string> domains,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(domains);
            if (domains.Count == 0)
            {
                throw new AcmeOrderException("missing_identities", "no domains");
            }

            var options = _options.Value;
            var renewalThreshold = TimeSpan.FromDays(options.AcmeRenewalThresholdDays);
            var password = _preparedIssuePassword ?? options.AcmeCertificatePassword;
            _preparedIssuePassword = null;
            var account = EnsureAccount(options, cancellationToken);
            var directoryUri = new Uri(options.AcmeDirectoryUrl.Trim(), UriKind.Absolute);

            using AcmeAccountKey accountKey = AcmeAccountKey.ImportPkcs8Der(account.PrivateKeyDer);
            var http = new AcmeHttpTransport(
                _httpClientFactory,
                HttpClientName,
                directoryUri,
                _logger,
                TimeProvider.System);
            var acme = new AcmeClient(http, accountKey, _logger, TimeProvider.System);
            acme.BindExistingAccount(account.AccountUri);

            using AcmeCertificateKey certKey = AcmeCertificateKey.CreateRsa(KeySizeBits);
            AcmeOrder order;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identifiers = domains
                    .Select(static d => new AcmeIdentifier { Type = AcmeIdentifierTypes.Dns, Value = d })
                    .ToArray();
                order = await acme.CreateOrderAsync(identifiers, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AcmeOrderException("new_order_failed", AcmeProblemDiagnostics.FormatException(ex));
            }

            if (order.Resource.Authorizations is null || order.Resource.Authorizations.Count == 0)
            {
                throw new AcmeOrderException("no_dns01_challenge", "missing authorizations");
            }

            var authzUrls = order.Resource.Authorizations.ToArray();
            var specs = new List<Dns01ChallengeSpec>(authzUrls.Length);
            var challengeUrls = new List<Uri>(authzUrls.Length);

            try
            {
                foreach (Uri authzUrl in authzUrls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AcmeAuthorizationResource resource = await acme.GetAuthorizationAsync(authzUrl, cancellationToken)
                        .ConfigureAwait(false);
                    var domain = resource.Identifier?.Value
                        ?? throw new AcmeOrderException("no_dns01_challenge", "missing identifier");
                    AcmeChallengeResource dns = resource.Challenges?.FirstOrDefault(static c =>
                            string.Equals(c.Type, "dns-01", StringComparison.OrdinalIgnoreCase))
                        ?? throw new AcmeOrderException("no_dns01_challenge", domain);
                    if (string.IsNullOrWhiteSpace(dns.Token) || dns.Url is null)
                    {
                        throw new AcmeOrderException("no_dns01_challenge", domain);
                    }

                    specs.Add(new Dns01ChallengeSpec(domain, accountKey.GetDnsRecordValue(dns.Token)));
                    challengeUrls.Add(dns.Url);
                }

                await _dnsSolver.PlaceAsync(specs, cancellationToken).ConfigureAwait(false);
                AcmeLogMessages.Dns01RecordsPlaced(_logger, specs.Count);

                await _dnsSolver.WaitPropagatedAsync(specs, cancellationToken).ConfigureAwait(false);
                AcmeLogMessages.Dns01VisibilityConfirmed(_logger);

                foreach (Uri challengeUrl in challengeUrls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await acme.SubmitChallengeAsync(challengeUrl, cancellationToken).ConfigureAwait(false);
                }

                _journal?.RecordAcmeValidationStarted();
                AcmeLogMessages.Dns01ChallengesTriggered(_logger, (int)_readinessTimeout.TotalSeconds);

                await WaitForOrderReadyAsync(acme, order.Location, authzUrls, cancellationToken).ConfigureAwait(false);
                _journal?.RecordAcmeValidationSucceeded();
                AcmeLogMessages.OrderReady(_logger);

                cancellationToken.ThrowIfCancellationRequested();
                string certificatePem;
                try
                {
                    Uri finalizeUrl = order.Resource.Finalize
                        ?? throw new AcmeOrderException("finalize_failed", "missing finalize url");
                    byte[] csr = CsrBuilder.CreateDnsSigningRequest(domains, certKey);

                    // Refresh finalize URL from the ready order resource when present.
                    AcmeOrderResource readyOrder = await acme.GetOrderAsync(order.Location, cancellationToken)
                        .ConfigureAwait(false);
                    finalizeUrl = readyOrder.Finalize ?? finalizeUrl;

                    _ = await acme.FinalizeOrderAsync(order.Location, finalizeUrl, csr, cancellationToken)
                        .ConfigureAwait(false);
                    AcmeOrderResource validOrder = await acme.WaitForOrderAsync(
                            order.Location,
                            timeout: TimeSpan.FromMinutes(3),
                            pollInterval: TimeSpan.FromSeconds(2),
                            cancellationToken)
                        .ConfigureAwait(false);
                    Uri certificateUrl = validOrder.Certificate
                        ?? throw new AcmeOrderException("finalize_failed", "missing certificate url");
                    AcmeCertificate certificate = await acme.DownloadCertificateAsync(certificateUrl, cancellationToken)
                        .ConfigureAwait(false);
                    certificatePem = certificate.Pem;
                }
                catch (Exception ex) when (ex is AcmeCaException or CryptographicException)
                {
                    var orderStatus = "unknown";
                    try
                    {
                        AcmeOrderResource resource = await acme.GetOrderAsync(order.Location, CancellationToken.None)
                            .ConfigureAwait(false);
                        orderStatus = resource.Status ?? "unknown";
                    }
                    catch (Exception statusEx) when (statusEx is not OperationCanceledException)
                    {
                        // Best-effort status for diagnostics.
                    }

                    throw new AcmeOrderException(
                        "finalize_failed",
                        $"{AcmeProblemDiagnostics.FormatException(ex)} order_status={orderStatus}");
                }

                AcmeLogMessages.CertificateFinalized(_logger);

                await CleanupDnsAsync(cancellationToken).ConfigureAwait(false);

                var pfxBytes = BuildPfx(certificatePem, certKey, password);
                return CertificateValidator.RequireValidPfx(
                    pfxBytes,
                    password,
                    domains,
                    renewalThreshold);
            }
            catch (OperationCanceledException)
            {
                await BestEffortDnsCleanupAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (AcmeException)
            {
                await BestEffortDnsCleanupAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await BestEffortDnsCleanupAsync(cancellationToken).ConfigureAwait(false);
                throw new AcmeOrderException("challenge_failed", AcmeProblemDiagnostics.FormatException(ex));
            }
        }

        /// <summary>Polls the order and its authorizations until ready, invalid, or the readiness timeout.</summary>
        /// <param name="acme">Bound ACME client.</param>
        /// <param name="orderUrl">Order Location URL.</param>
        /// <param name="authorizationUrls">Authorization URLs from the created order.</param>
        /// <param name="cancellationToken">Cancels each poll.</param>
        private async Task WaitForOrderReadyAsync(
            AcmeClient acme,
            Uri orderUrl,
            IReadOnlyList<Uri> authorizationUrls,
            CancellationToken cancellationToken)
        {
            await AcmeOrderReadiness.WaitUntilReadyAsync(
                    async ct => await CaptureOrderViewAsync(acme, orderUrl, authorizationUrls, ct).ConfigureAwait(false),
                    _readinessTimeout,
                    _readinessInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>POST-as-GET the order and each authorization, keeping the dns-01 challenge error when present.</summary>
        /// <param name="acme">Bound ACME client.</param>
        /// <param name="orderUrl">Order Location URL.</param>
        /// <param name="authorizationUrls">Authorization URLs to fetch.</param>
        /// <param name="cancellationToken">Cancels each fetch.</param>
        /// <returns>A snapshot for <see cref="AcmeOrderReadiness.Evaluate"/>.</returns>
        private static async Task<AcmeOrderReadiness.OrderView> CaptureOrderViewAsync(
            AcmeClient acme,
            Uri orderUrl,
            IReadOnlyList<Uri> authorizationUrls,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcmeOrderResource orderResource = await acme.GetOrderAsync(orderUrl, cancellationToken).ConfigureAwait(false);
            var authzViews = new List<AcmeOrderReadiness.AuthorizationView>(authorizationUrls.Count);
            foreach (Uri authzUrl in authorizationUrls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AcmeAuthorizationResource resource = await acme.GetAuthorizationAsync(authzUrl, cancellationToken)
                    .ConfigureAwait(false);
                AcmeChallengeResource? dnsChallenge = resource.Challenges?
                    .FirstOrDefault(static c =>
                        string.Equals(c.Type, "dns-01", StringComparison.OrdinalIgnoreCase));
                AcmeProblem? error = dnsChallenge?.Error
                    ?? resource.Challenges?.Select(static c => c.Error).FirstOrDefault(static e => e is not null);

                authzViews.Add(
                    new AcmeOrderReadiness.AuthorizationView(
                        resource.Identifier?.Value,
                        resource.Status ?? string.Empty,
                        error?.Type,
                        error?.Detail,
                        error?.Status));
            }

            return new AcmeOrderReadiness.OrderView(
                orderResource.Status ?? string.Empty,
                authzViews);
        }

        /// <summary>Loads or creates the account for <paramref name="options"/>'s directory URL.</summary>
        /// <param name="options">Supplies the directory URL and contact email used at registration.</param>
        /// <param name="cancellationToken">Passed to <see cref="AccountStore.EnsureRegistered"/>.</param>
        /// <returns>The persisted account, including the PKCS#8 key.</returns>
        private AcmeAccountState EnsureAccount(AcmeCloudflareOptions options, CancellationToken cancellationToken)
        {
            return _accountStore.EnsureRegistered(
                options.AcmeDirectoryUrl.Trim(),
                GenerateAccountKeyDer,
                keyDer => RegisterAccount(options, keyDer),
                cancellationToken);
        }

        /// <summary>Creates a new <see cref="KeySizeBits"/>-bit RSA key and returns its PKCS#8 DER encoding.</summary>
        /// <returns>The private key bytes. The temporary <see cref="RSA"/> is disposed.</returns>
        private static byte[] GenerateAccountKeyDer()
        {
            using var rsa = RSA.Create(KeySizeBits);
            return rsa.ExportPkcs8PrivateKey();
        }

        /// <summary>
        /// Calls <c>newAccount</c> with <c>termsOfServiceAgreed</c> true, an optional <c>mailto:</c> contact, and no external account binding.
        /// The call blocks on <see cref="CancellationToken.None"/>. The stored registration body is empty.
        /// </summary>
        /// <param name="options">Directory URL and email.</param>
        /// <param name="keyDer">PKCS#8 account key.</param>
        /// <returns>The account Location URL and an empty registration body.</returns>
        private (string AccountUri, string RegistrationBody) RegisterAccount(AcmeCloudflareOptions options, byte[] keyDer)
        {
            try
            {
                var directoryUri = new Uri(options.AcmeDirectoryUrl.Trim(), UriKind.Absolute);
                using AcmeAccountKey accountKey = AcmeAccountKey.ImportPkcs8Der(keyDer);
                var http = new AcmeHttpTransport(
                    _httpClientFactory,
                    HttpClientName,
                    directoryUri,
                    _logger,
                    TimeProvider.System);
                var acme = new AcmeClient(http, accountKey, _logger, TimeProvider.System);
                string email = options.AcmeEmail.Trim();
                IReadOnlyList<string> contacts = string.IsNullOrWhiteSpace(email)
                    ? []
                    : ["mailto:" + email];
                string location = acme.RegisterAccountAsync(
                        contacts,
                        termsOfServiceAgreed: true,
                        externalAccountBinding: null,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                if (string.IsNullOrWhiteSpace(location))
                {
                    throw new AcmeAccountException("registration_failed", "empty account_uri");
                }

                AcmeLogMessages.AccountRegistered(_logger);
                return (location, string.Empty);
            }
            catch (AcmeAccountException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AcmeAccountException("registration_failed", AcmeProblemDiagnostics.FormatException(ex));
            }
        }

        /// <summary>
        /// Pairs the first PEM certificate with <paramref name="certKey"/> and exports leaf plus the remaining certificates as a PFX.
        /// A blank or empty chain throws category <c>empty_certificate</c>.
        /// </summary>
        /// <param name="certificateChainPem">PEM chain from the CA. The first certificate is the leaf.</param>
        /// <param name="certKey">Leaf private key. Not disposed.</param>
        /// <param name="password">PFX password.</param>
        /// <returns>PKCS#12 bytes.</returns>
        private static byte[] BuildPfx(string certificateChainPem, AcmeCertificateKey certKey, string password)
        {
            if (string.IsNullOrWhiteSpace(certificateChainPem))
            {
                throw new AcmeOrderException("empty_certificate", "missing_leaf");
            }

            var chain = new X509Certificate2Collection();
            chain.ImportFromPem(certificateChainPem);
            if (chain.Count == 0)
            {
                throw new AcmeOrderException("empty_certificate", "missing_leaf");
            }

            string keyPem = certKey.ExportPem();
            using X509Certificate2 leafPem = chain[0];
            using X509Certificate2 leafWithKey = X509Certificate2.CreateFromPem(
                leafPem.ExportCertificatePem(),
                keyPem);

            var intermediates = new List<X509Certificate2>();
            try
            {
                for (var i = 1; i < chain.Count; i++)
                {
                    intermediates.Add(chain[i]);
                }

                return PfxCrypto.ExportPfx(leafWithKey, intermediates, password);
            }
            finally
            {
                // leafPem/leafWithKey disposed via using; intermediates were borrowed from the collection.
            }
        }

        /// <summary>Deletes TXT records created by this issuance. Cleanup failures become category <c>txt_cleanup_failed</c>.</summary>
        /// <param name="cancellationToken">Cancels cleanup. Cancellation propagates.</param>
        private async Task CleanupDnsAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _dnsSolver.CleanupAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AcmeOrderException("txt_cleanup_failed", AcmeProblemDiagnostics.FormatException(ex));
            }
        }

        /// <summary>
        /// Deletes TXT records after a failed issuance.
        /// Cancellation is ignored unless <paramref name="cancellationToken"/> itself is already cancelled. Other failures are ignored.
        /// </summary>
        /// <param name="cancellationToken">Passed to cleanup. <see cref="CancellationToken.None"/> is used when the caller was cancelled.</param>
        private async Task BestEffortDnsCleanupAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _dnsSolver.CleanupAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Ignore clean-up cancel during best-effort.
            }
            catch
            {
                // Best-effort.
            }
        }
    }
}
