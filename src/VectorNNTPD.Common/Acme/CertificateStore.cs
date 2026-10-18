using System.Text.RegularExpressions;

namespace VectorNNTPD.Common.Acme
{
    /// <summary>
    /// Crash-safe FQDN-scoped live server certificate store using generation directories
    /// and a <c>current</c> pointer.
    /// Persists the TLS credential as PKCS#12/PFX (<c>certificate.pfx</c>).
    /// </summary>
    internal sealed partial class CertificateStore
    {
        /// <summary>32 lowercase hex characters, the <c>N</c> format of a <see cref="Guid"/>.</summary>
        /// <returns>The culture-invariant matcher for a generation id.</returns>
        [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
        private static partial Regex GenerationIdRegex();

        /// <summary>Shared ACME state root.</summary>
        private readonly string _stateDir;

        /// <summary>Normalized FQDN that owns this live partition.</summary>
        private readonly string _fqdn;

        /// <summary>Password used when the current generation is loaded or saved. Not logged. Replaced only after a new generation is current.</summary>
        private string _pfxPassword;

        /// <summary>DNS SANs required on every loaded or saved certificate.</summary>
        private readonly IReadOnlyList<string> _requiredDomains;

        /// <summary>Renewal window passed to <see cref="CertificateValidator.ValidatePfx"/> on load and save.</summary>
        private readonly TimeSpan _renewalThreshold;

        /// <summary>Initializes a new instance of the <see cref="CertificateStore"/> class.</summary>
        /// <param name="stateDir">Shared ACME state directory.</param>
        /// <param name="fqdn">Certificate FQDN that owns this live partition.</param>
        /// <param name="pfxPassword">PKCS#12 password (never logged).</param>
        /// <param name="requiredDomains">Required DNS SANs for round-trip validation.</param>
        /// <param name="renewalThreshold">Renewal threshold used when assessing reloaded material.</param>
        internal CertificateStore(
            string stateDir,
            string fqdn,
            string pfxPassword,
            IReadOnlyList<string> requiredDomains,
            TimeSpan renewalThreshold)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
            ArgumentNullException.ThrowIfNull(pfxPassword);
            ArgumentNullException.ThrowIfNull(requiredDomains);
#pragma warning disable CA1512 // Use ArgumentOutOfRangeException throw helper
            if (renewalThreshold <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(renewalThreshold));
            }
#pragma warning restore CA1512 // Use ArgumentOutOfRangeException throw helper

            _stateDir = stateDir;
            _fqdn = CertificateIdentities.NormalizeFqdn(fqdn);
            _pfxPassword = pfxPassword;
            _requiredDomains = requiredDomains;
            _renewalThreshold = renewalThreshold;
            AcmePaths.EnsureCertificateIdentityLayout(stateDir, _fqdn);
            Recover();
        }

        /// <summary>Gets the FQDN this store is scoped to.</summary>
        private string Fqdn => _fqdn;

        /// <summary>Gets the password that last loaded or saved the current generation. Not logged.</summary>
        internal string CurrentPfxPassword => _pfxPassword;

        /// <summary>Returns paths for the active generation (after recovery).</summary>
        internal CertificatePaths Paths()
        {
            Recover();
            var active = ReadCurrentId();
            if (active is not null && GenerationIsComplete(active))
            {
                return AcmePaths.GenerationCertificatePaths(_stateDir, _fqdn, active);
            }

            throw new AcmeStorageException("no_certificate", "no complete certificate generation");
        }

        /// <summary>Returns the active generation id, or <see langword="null"/> when none is current.</summary>
        internal string? CurrentGenerationId()
        {
            Recover();
            var active = ReadCurrentId();
            return active is not null && GenerationIsComplete(active) ? active : null;
        }

        /// <summary>
        /// Loads and validates the active PFX, or <see langword="null"/> when no complete generation exists.
        /// </summary>
        internal CertificateMaterial? Load()
        {
            Recover();
            var active = ReadCurrentId();
            if (active is null || !GenerationIsComplete(active))
            {
                return null;
            }

            var paths = AcmePaths.GenerationCertificatePaths(_stateDir, _fqdn, active);
            byte[] pfxBytes;
            try
            {
                pfxBytes = File.ReadAllBytes(paths.PfxPath);
            }
            catch (IOException ex)
            {
                throw new AcmeStorageException("certificate_read_failed", ex.GetType().Name);
            }

            if (pfxBytes.Length == 0)
            {
                throw new AcmeStorageException("incomplete_certificate", "empty certificate material");
            }

            var status = CertificateValidator.ValidatePfx(
                pfxBytes,
                _pfxPassword,
                _requiredDomains,
                _renewalThreshold);
            if (!status.Usable || status.Material is null)
            {
                throw new AcmeStorageException("invalid_certificate", status.Reason);
            }

            return status.Material;
        }

        /// <summary>
        /// Persists a new generation after a serialize → reload → validate round trip, then commits <c>current</c>.
        /// </summary>
        /// <param name="material">PFX bytes to write. They are validated before and after the write.</param>
        /// <param name="pfxPassword">
        /// Password for this write. <see langword="null"/> uses <see cref="CurrentPfxPassword"/>.
        /// A non-null value becomes <see cref="CurrentPfxPassword"/> only after the new generation is current.
        /// </param>
        /// <returns>Paths of the generation that is now current.</returns>
        /// <exception cref="AcmeCertificateException">Thrown when pre-validation or the reload round trip fails.</exception>
        /// <exception cref="AcmeStorageException">Thrown when the write fails after the new generation directory is removed.</exception>
        internal CertificatePaths Save(CertificateMaterial material, string? pfxPassword = null)
        {
            ArgumentNullException.ThrowIfNull(material);
            var password = pfxPassword ?? _pfxPassword;
            ArgumentException.ThrowIfNullOrWhiteSpace(password);
            AcmePaths.EnsureCertificateIdentityLayout(_stateDir, _fqdn);
            Recover();

            var preStatus = CertificateValidator.ValidatePfx(
                material.PfxBytes,
                password,
                _requiredDomains,
                _renewalThreshold);
            if (!preStatus.Usable || preStatus.Material is null)
            {
                throw new AcmeCertificateException("invalid_certificate", preStatus.Reason);
            }

            var generationId = Guid.NewGuid().ToString("N");
            var genRoot = AcmePaths.GenerationDir(_stateDir, _fqdn, generationId);
            try
            {
                Directory.CreateDirectory(genRoot);
                var paths = AcmePaths.GenerationCertificatePaths(_stateDir, _fqdn, generationId);
                AtomicFile.WriteBytes(paths.PfxPath, material.PfxBytes);

                byte[] reloaded;
                try
                {
                    reloaded = File.ReadAllBytes(paths.PfxPath);
                }
                catch (IOException ex)
                {
                    throw new AcmeStorageException("certificate_read_failed", ex.GetType().Name);
                }

                var roundTrip = CertificateValidator.ValidatePfx(
                    reloaded,
                    password,
                    _requiredDomains,
                    _renewalThreshold);
                if (!roundTrip.Usable || roundTrip.Material is null)
                {
                    throw new AcmeCertificateException("pfx_roundtrip_failed", roundTrip.Reason);
                }

                AtomicFile.WriteBytes(AcmePaths.GenerationCompleteMarker(_stateDir, _fqdn, generationId), "ok\n"u8);
                AtomicFile.WriteText(AcmePaths.CurrentGenerationPointerPath(_stateDir, _fqdn), generationId + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AcmeCertificateException)
            {
                TryDeleteDirectory(genRoot);
                if (ex is AcmeCertificateException)
                {
                    throw;
                }

                throw new AcmeStorageException("certificate_persist_failed", ex.GetType().Name);
            }

            _pfxPassword = password;
            CleanupNonCurrentGenerations(keep: generationId);
            return AcmePaths.GenerationCertificatePaths(_stateDir, _fqdn, generationId);
        }

        /// <summary>Idempotent recovery for interrupted promotions inside this FQDN only.</summary>
        internal void Recover()
        {
            AcmePaths.EnsureCertificateIdentityLayout(_stateDir, _fqdn);
            var pointer = AcmePaths.CurrentGenerationPointerPath(_stateDir, _fqdn);
            var currentId = ReadCurrentId();
            if (File.Exists(pointer) && currentId is null)
            {
                AtomicFile.TryDelete(pointer);
            }

            if (currentId is not null && GenerationIsComplete(currentId))
            {
                CleanupNonCurrentGenerations(keep: currentId);
                return;
            }

            if (currentId is not null)
            {
                AtomicFile.TryDelete(pointer);
            }

            CleanupNonCurrentGenerations(keep: null);
        }

        /// <summary>
        /// Reads the current-pointer file. Returns <see langword="null"/> when the file is missing, unreadable, or not a 32-hex id.
        /// </summary>
        /// <returns>The generation id, or <see langword="null"/>.</returns>
        private string? ReadCurrentId()
        {
            var path = AcmePaths.CurrentGenerationPointerPath(_stateDir, _fqdn);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var raw = File.ReadAllText(path).Trim();
                return GenerationIdRegex().IsMatch(raw) ? raw : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <summary>
        /// A generation is complete when the id matches <see cref="GenerationIdRegex"/>, the <c>complete</c> marker exists, and <c>certificate.pfx</c> exists and is non-empty.
        /// </summary>
        /// <param name="generationId">Directory name under <c>gens</c>.</param>
        /// <returns><see langword="false"/> when any of those checks fail, including an <see cref="IOException"/> reading the length.</returns>
        private bool GenerationIsComplete(string generationId)
        {
            if (!GenerationIdRegex().IsMatch(generationId))
            {
                return false;
            }

            var marker = AcmePaths.GenerationCompleteMarker(_stateDir, _fqdn, generationId);
            var paths = AcmePaths.GenerationCertificatePaths(_stateDir, _fqdn, generationId);
            if (!File.Exists(marker) || !File.Exists(paths.PfxPath))
            {
                return false;
            }

            try
            {
                return new FileInfo(paths.PfxPath).Length > 0;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>Deletes every generation directory except <paramref name="keep"/>. <see langword="null"/> deletes all of them. IO failures are ignored.</summary>
        /// <param name="keep">Generation id to retain, or <see langword="null"/>.</param>
        private void CleanupNonCurrentGenerations(string? keep)
        {
            var root = AcmePaths.GenerationsDir(_stateDir, _fqdn);
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var entry in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(entry);
                if (keep is not null && string.Equals(name, keep, StringComparison.Ordinal))
                {
                    continue;
                }

                TryDeleteDirectory(entry);
            }
        }

        /// <summary>Deletes <paramref name="path"/> recursively. <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/> are ignored.</summary>
        /// <param name="path">Generation directory.</param>
        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort.
            }
        }
    }
}
