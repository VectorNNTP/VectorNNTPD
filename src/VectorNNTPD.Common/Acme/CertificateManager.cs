using System.Security.Cryptography.X509Certificates;

namespace VectorNNTPD.Common.Acme
{
    /// <summary>
    /// Owns server certificate lifecycle: evaluate, issue, renew, and expose validated PFX material.
    /// </summary>
    internal sealed class CertificateManager
    {
        /// <summary>Normalized certificate FQDN that owns this manager's store and journal.</summary>
        private readonly string _fqdn;

        /// <summary>Shared ACME state root passed to the store, journal, and issuance lock.</summary>
        private readonly string _stateDir;

        /// <summary>Directory URL recorded on each journal transaction.</summary>
        private readonly string _acmeDirectoryUrl;

        /// <summary>SAN set from <see cref="CertificateIdentities.ForFqdn"/>, including the news hostname when requested.</summary>
        private readonly IReadOnlyList<string> _domains;

        /// <summary>FQDN-scoped live certificate store.</summary>
        private readonly CertificateStore _store;

        /// <summary>Issuer called when the live certificate is missing, invalid, or due for renewal.</summary>
        private readonly ICertificateIssuer _issuer;

        /// <summary>Journal that records the request, persist, promote, and failure events.</summary>
        private readonly AcmeTransactionJournal _journal;

        /// <summary>Password that matches <see cref="_current"/> when that material was published. Not logged.</summary>
        private string _pfxPassword;

        /// <summary>Material and the password that encrypts it, published together after a successful load or save.</summary>
        private PublishedCredential? _published;

        /// <summary>
        /// Invoked after a new generation is current and <see cref="PublishCredential"/> has stored the matching password.
        /// Null in production. Tests use it to fail the journal step after the certificate is already durable.
        /// </summary>
        internal Action? AfterPersistForTests { get; set; }

        /// <summary>How long before not-after a usable certificate is treated as due for renewal.</summary>
        private readonly TimeSpan _renewalThreshold;

        /// <summary>Logger for reuse, renewal, and ready events.</summary>
        private readonly ILogger<CertificateManager> _logger;

        /// <summary>Serializes ensure and renew on this instance. The cross-process lock is separate.</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Last material accepted by ensure or renew. <see langword="null"/> until then.</summary>
        private CertificateMaterial? _current;

        /// <summary>Count of successful persists on this instance. Logged by <see cref="AcmeLogMessages.ServerCertificateReady"/>; not the filesystem generation id.</summary>
        private int _generation;

        /// <summary>Initializes a new instance of the <see cref="CertificateManager"/> class.</summary>
        /// <param name="fqdn">Certificate FQDN that owns this manager.</param>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="acmeDirectoryUrl">ACME directory URL recorded in the journal.</param>
        /// <param name="store">FQDN-scoped live certificate store.</param>
        /// <param name="issuer">Certificate issuer.</param>
        /// <param name="pfxPassword">PKCS#12 password.</param>
        /// <param name="renewalThreshold">Renewal threshold.</param>
        /// <param name="logger">Logger.</param>
        /// <param name="includeNewsHostname">Whether to include <see cref="CertificateIdentities.NewsHostname"/>.</param>
        /// <param name="journal">
        /// Optional shared transaction journal. When omitted, this manager creates one
        /// for <paramref name="fqdn"/>. Production wiring shares the instance with
        /// <see cref="Dns01Solver"/> and <see cref="AcmeIssuer"/>.
        /// </param>
        internal CertificateManager(
            string fqdn,
            string stateDir,
            string acmeDirectoryUrl,
            CertificateStore store,
            ICertificateIssuer issuer,
            string pfxPassword,
            TimeSpan renewalThreshold,
            ILogger<CertificateManager> logger,
            bool includeNewsHostname = true,
            AcmeTransactionJournal? journal = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
            ArgumentException.ThrowIfNullOrWhiteSpace(acmeDirectoryUrl);
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(issuer);
            ArgumentNullException.ThrowIfNull(pfxPassword);
            ArgumentNullException.ThrowIfNull(logger);
#pragma warning disable CA1512 // Use ArgumentOutOfRangeException throw helper
            if (renewalThreshold <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(renewalThreshold));
            }
#pragma warning restore CA1512 // Use ArgumentOutOfRangeException throw helper

            _fqdn = CertificateIdentities.NormalizeFqdn(fqdn);
            _stateDir = stateDir;
            _acmeDirectoryUrl = acmeDirectoryUrl.Trim();
            _domains = CertificateIdentities.ForFqdn(_fqdn, includeNewsHostname);
            _store = store;
            _issuer = issuer;
            _journal = journal ?? new AcmeTransactionJournal(_stateDir, _fqdn);
            _pfxPassword = pfxPassword;
            _renewalThreshold = renewalThreshold;
            _logger = logger;
        }

        /// <summary>Gets the required certificate DNS identities.</summary>
        private IReadOnlyList<string> Domains => _domains;

        /// <summary>Gets the monotonic generation counter after successful persist.</summary>
        private int Generation => _generation;

        /// <summary>Gets the current in-memory material, if any.</summary>
        internal CertificateMaterial? CurrentMaterial => _current;

        /// <summary>Assesses on-disk certificate without contacting ACME.</summary>
        /// <param name="now">UTC time passed to validation. <see langword="null"/> uses <see cref="DateTimeOffset.UtcNow"/>.</param>
        /// <returns>Usable material, or a non-usable status whose reason is <c>absent</c> or the storage/certificate category.</returns>
        internal CertificateStatus EvaluateExisting(DateTimeOffset? now = null)
        {
            try
            {
                var loaded = _store.Load();
                if (loaded is null)
                {
                    return new CertificateStatus(false, true, null, "absent");
                }

                return CertificateValidator.ValidatePfx(
                    loaded.PfxBytes,
                    _store.CurrentPfxPassword,
                    _domains,
                    _renewalThreshold,
                    now);
            }
            catch (AcmeStorageException ex)
            {
                return new CertificateStatus(false, true, null, ex.Category);
            }
            catch (AcmeCertificateException ex)
            {
                return new CertificateStatus(false, true, null, ex.Category);
            }
        }

        /// <summary>Reuses a usable certificate or issues a new one (startup path).</summary>
        /// <param name="cancellationToken">Cancels the in-process gate wait, the issuance lock wait, and issuance.</param>
        /// <returns>The reused or newly stored material.</returns>
        internal async Task<CertificateMaterial> EnsureCertificateAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var issuanceLock = await AcmeIssuanceLock
                    .AcquireAsync(_stateDir, _fqdn, cancellationToken)
                    .ConfigureAwait(false);
                var status = EvaluateExisting();
                if (status is { Usable: true, Material: not null, DueForRenewal: false })
                {
                    PublishCredential(status.Material, _store.CurrentPfxPassword);
                    AcmeLogMessages.ReusingExistingCertificate(_logger, status.Material.NotAfter);
                    return status.Material;
                }

                if (status is { Usable: true, DueForRenewal: true })
                {
                    AcmeLogMessages.ExistingCertificateDueForRenewal(_logger);
                }
                else
                {
                    AcmeLogMessages.NoUsableCertificate(_logger, status.Reason);
                }

                return await IssueAndPersistAsync(cancellationToken, _pfxPassword).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Renews when absent, invalid, or past the renewal threshold.
        /// Returns <see langword="true"/> when a new certificate was persisted, including when a later journal write fails.
        /// When a previously usable certificate exists and issuance fails before the new generation is current, that material is kept and the result is <see langword="false"/>.
        /// </summary>
        /// <param name="cancellationToken">Cancels the gate wait, the issuance lock wait, and issuance. Cancellation is not swallowed.</param>
        /// <param name="now">UTC time for the due check. <see langword="null"/> uses <see cref="DateTimeOffset.UtcNow"/>.</param>
        /// <param name="pfxPassword">
        /// Password for this attempt's new PFX. <see langword="null"/> or whitespace keeps <see cref="CurrentPfxPassword"/>.
        /// The existing certificate is still loaded with the password that last saved it.
        /// </param>
        /// <returns><see langword="true"/> when a new certificate was persisted.</returns>
        internal async Task<bool> RenewIfDueAsync(
            CancellationToken cancellationToken,
            DateTimeOffset? now = null,
            string? pfxPassword = null)
        {
            var issuePassword = string.IsNullOrWhiteSpace(pfxPassword) ? _pfxPassword : pfxPassword;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var issuanceLock = await AcmeIssuanceLock
                    .AcquireAsync(_stateDir, _fqdn, cancellationToken)
                    .ConfigureAwait(false);
                var status = EvaluateExisting(now);
                if (status is { Usable: true, Material: not null, DueForRenewal: false })
                {
                    PublishCredential(status.Material, _store.CurrentPfxPassword);
                    return false;
                }

                var prior = status.Usable ? status.Material : null;
                var publishedBefore = Volatile.Read(ref _published);
                try
                {
                    _ = await IssueAndPersistAsync(cancellationToken, issuePassword).ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex) when (prior is not null && ex is not OperationCanceledException)
                {
                    if (!ReferenceEquals(Volatile.Read(ref _published), publishedBefore))
                    {
                        return true;
                    }

                    _current = prior;
                    AcmeLogMessages.RenewalFailedPreservingExisting(_logger, AcmeFailureSanitizer.Sanitize(ex));
                    return false;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Gets the password that matches <see cref="CurrentMaterial"/>. Not logged.</summary>
        internal string CurrentPfxPassword => _pfxPassword;

        /// <summary>
        /// Loads an <see cref="X509Certificate2"/> with private key from the current live PFX.
        /// Caller owns disposal. The certificate remains usable after the file path is closed.
        /// Uses the material and password published together by the last successful load or save.
        /// </summary>
        internal X509Certificate2 CreateTlsCertificate()
        {
            var published = Volatile.Read(ref _published);
            if (published is not null)
            {
                return PfxCrypto.LoadCertificate(published.Material.PfxBytes, published.Password);
            }

            var material = _current ?? EvaluateExisting().Material
                ?? throw new AcmeCertificateException("no_certificate", "live material missing");
            return PfxCrypto.LoadCertificate(material.PfxBytes, _pfxPassword);
        }

        /// <summary>
        /// Starts a journal transaction, issues, validates, saves, and completes the transaction.
        /// Failures other than cancellation call <see cref="AcmeTransactionJournal.CompleteFailure"/> and are rethrown.
        /// </summary>
        /// <param name="cancellationToken">Passed to <see cref="ICertificateIssuer.IssueAsync"/>. Journal writes use their own token.</param>
        /// <param name="pfxPassword">Password for the issued PFX, its validation, the store, and the journal. Not logged.</param>
        /// <returns>The validated material now stored as current.</returns>
        /// <exception cref="AcmeCertificateException">Thrown with category <c>invalid_certificate</c> when the issued PFX fails validation.</exception>
        private async Task<CertificateMaterial> IssueAndPersistAsync(CancellationToken cancellationToken, string pfxPassword)
        {
            if (_issuer is AcmeIssuer acmeIssuer)
            {
                acmeIssuer.UsePasswordForNextIssue(pfxPassword);
            }

            var transactionId = _journal.BeginTransaction(
                _domains,
                _acmeDirectoryUrl,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            try
            {
                var material = await _issuer.IssueAsync(_domains, cancellationToken).ConfigureAwait(false);
                var status = CertificateValidator.ValidatePfx(
                    material.PfxBytes,
                    pfxPassword,
                    _domains,
                    _renewalThreshold);
                if (!status.Usable || status.Material is null)
                {
                    throw new AcmeCertificateException("invalid_certificate", status.Reason);
                }

                _journal.RecordCertificateIssued(status.Material);
                var persisted = _store.Save(status.Material, pfxPassword);
                PublishCredential(status.Material, pfxPassword);
                AfterPersistForTests?.Invoke();
                _journal.RecordCertificatePersisted(persisted.GenerationId, status.Material);
                _journal.RecordCertificatePromoted(persisted.GenerationId);
                _journal.CompleteSuccess(
                    transactionId,
                    status.Material,
                    persisted.GenerationId,
                    pfxPassword,
                    DateTimeOffset.UtcNow);
                _generation++;
                AcmeLogMessages.ServerCertificateReady(_logger, _generation, status.Material.NotAfter);
                return status.Material;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var category = ex is AcmeException acme ? acme.Category : ex.GetType().Name;
                _journal.CompleteFailure(
                    transactionId,
                    category,
                    AcmeFailureSanitizer.Sanitize(ex),
                    DateTimeOffset.UtcNow);
                throw;
            }
        }

        /// <summary>Publishes <paramref name="material"/> and <paramref name="password"/> together for <see cref="CreateTlsCertificate"/>.</summary>
        /// <param name="material">PFX that <paramref name="password"/> can decrypt.</param>
        /// <param name="password">Password for <paramref name="material"/>. Not logged.</param>
        private void PublishCredential(CertificateMaterial material, string password)
        {
            _current = material;
            _pfxPassword = password;
            Volatile.Write(ref _published, new PublishedCredential(material, password));
        }

        /// <summary>One PFX and the password that encrypts it.</summary>
        /// <param name="Material">Validated certificate material.</param>
        /// <param name="Password">PKCS#12 password for <paramref name="Material"/>. Not logged.</param>
        private sealed record PublishedCredential(CertificateMaterial Material, string Password);
    }
}
