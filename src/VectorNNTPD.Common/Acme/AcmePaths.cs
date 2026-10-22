namespace VectorNNTPD.Common.Acme
{
    /// <summary>
    /// Common ACME filesystem layout under <c>AcmeStateDir</c>.
    /// </summary>
    /// <remarks>
    /// Account state is shared by every certificate identity. The persistent ACME
    /// transaction journal is one file per FQDN. Live certificates, DNS-01 recovery
    /// state, current pointers, and issuance locks are partitioned by certificate FQDN.
    /// <code>
    /// {stateDir}/
    ///   account/
    ///     private_key.der
    ///     registration.json
    ///     private_key.der.pending
    ///     registration.pending.json
    ///   journal/
    ///     {fqdn}.json
    ///   live/{fqdn}/
    ///     current
    ///     .issuance.lock
    ///     dns01/{entryId}.json
    ///     gens/{generation}/
    ///       certificate.pfx
    ///       complete
    /// </code>
    /// Production code never creates <c>live/gens</c>, <c>live/current</c>,
    /// <c>journal/acme/</c>, <c>journal/dns01/</c>, or <c>dns01/journal/</c>.
    /// </remarks>
    internal static class AcmePaths
    {
        /// <summary>Directory name <c>live</c> under the state root.</summary>
        private const string Live = "live";

        /// <summary>Directory name <c>gens</c> under an FQDN live directory.</summary>
        private const string Gens = "gens";

        /// <summary>File name <c>current</c> that stores the active generation id.</summary>
        private const string Current = "current";

        /// <summary>File name <c>complete</c> written after a generation round-trips successfully.</summary>
        private const string Complete = "complete";

        /// <summary>File name <c>certificate.pfx</c> inside a generation directory.</summary>
        private const string CertificatePfx = "certificate.pfx";

        /// <summary>File name <c>private_key.der</c> for the shared account key.</summary>
        private const string AccountPrivateKey = "private_key.der";

        /// <summary>Directory name <c>journal</c> for per-FQDN transaction files.</summary>
        private const string Journal = "journal";

        /// <summary>Directory name <c>dns01</c> under an FQDN live directory for recovery files.</summary>
        private const string Dns01 = "dns01";

        /// <summary>File name <c>.issuance.lock</c> under an FQDN live directory.</summary>
        private const string IssuanceLockFile = ".issuance.lock";

        /// <summary>Ensures the shared account and journal-root directories exist.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        internal static void EnsureStateLayout(string stateDir)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
            Directory.CreateDirectory(stateDir);
            Directory.CreateDirectory(AccountDir(stateDir));
            Directory.CreateDirectory(JournalRoot(stateDir));
        }

        /// <summary>
        /// Ensures FQDN-scoped live, generation, and DNS-01 recovery directories exist.
        /// </summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="fqdn">Certificate FQDN that owns the live partition.</param>
        internal static void EnsureCertificateIdentityLayout(string stateDir, string fqdn)
        {
            EnsureStateLayout(stateDir);
            var identity = CertificateIdentities.NormalizeFqdn(fqdn);
            Directory.CreateDirectory(LiveDir(stateDir, identity));
            Directory.CreateDirectory(GenerationsDir(stateDir, identity));
            Directory.CreateDirectory(Dns01RecoveryDir(stateDir, identity));
        }

        /// <summary>Account directory path.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <returns>The account directory.</returns>
        private static string AccountDir(string stateDir) => Path.Join(stateDir, "account");

        /// <summary>Account PKCS#8 DER private key path.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <returns>The account private-key path.</returns>
        internal static string AccountKeyPath(string stateDir) => Path.Join(AccountDir(stateDir), AccountPrivateKey);

        /// <summary>Account registration metadata JSON path.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <returns>The registration metadata path.</returns>
        internal static string AccountMetaPath(string stateDir) => Path.Join(AccountDir(stateDir), "registration.json");

        /// <summary>Pending account key path (crash recovery).</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <returns>The pending account-key path.</returns>
        internal static string AccountPendingKeyPath(string stateDir) =>
            Path.Join(AccountDir(stateDir), AccountPrivateKey + ".pending");

        /// <summary>Pending registration metadata path.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <returns>The pending registration metadata path.</returns>
        internal static string AccountPendingMetaPath(string stateDir) =>
            Path.Join(AccountDir(stateDir), "registration.pending.json");

        /// <summary>Common ACME journal root containing one <c>{fqdn}.json</c> file per identity.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <returns>The journal directory.</returns>
        internal static string JournalRoot(string stateDir) => Path.Join(stateDir, Journal);

        /// <summary>Persistent ACME transaction journal file for one FQDN.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="fqdn">Certificate FQDN that owns the journal.</param>
        /// <returns>The journal file path <c>journal/{fqdn}.json</c>.</returns>
        internal static string TransactionJournalPath(string stateDir, string fqdn) =>
            Path.Join(JournalRoot(stateDir), CertificateIdentities.NormalizeFqdn(fqdn) + ".json");

        /// <summary>FQDN-scoped live certificate root.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="fqdn">Certificate FQDN.</param>
        /// <returns>The live directory <c>live/{fqdn}</c>.</returns>
        internal static string LiveDir(string stateDir, string fqdn) =>
            Path.Join(stateDir, Live, CertificateIdentities.NormalizeFqdn(fqdn));

        /// <summary>FQDN-scoped certificate generations directory.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="fqdn">Certificate FQDN.</param>
        /// <returns>The generations directory <c>live/{fqdn}/gens</c>.</returns>
        internal static string GenerationsDir(string stateDir, string fqdn) =>
            Path.Join(LiveDir(stateDir, fqdn), Gens);

        /// <summary>FQDN-scoped current generation pointer file.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="fqdn">Certificate FQDN.</param>
        /// <returns>The current-pointer path <c>live/{fqdn}/current</c>.</returns>
        internal static string CurrentGenerationPointerPath(string stateDir, string fqdn) =>
            Path.Join(LiveDir(stateDir, fqdn), Current);

        /// <summary>Directory for one generation id under an FQDN.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="fqdn">Certificate FQDN.</param>
        /// <param name="generationId">Generation directory name.</param>
        /// <returns>The generation directory.</returns>
        internal static string GenerationDir(string stateDir, string fqdn, string generationId) =>
            Path.Join(GenerationsDir(stateDir, fqdn), generationId);

        /// <summary>Paths for a generation's PKCS#12/PFX material.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="fqdn">Certificate FQDN.</param>
        /// <param name="generationId">Generation directory name.</param>
        /// <returns>PFX path and generation id.</returns>
        internal static CertificatePaths GenerationCertificatePaths(string stateDir, string fqdn, string generationId)
        {
            var root = GenerationDir(stateDir, fqdn, generationId);
            return new CertificatePaths(Path.Join(root, CertificatePfx), generationId);
        }

        /// <summary>Completion marker for a generation.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="fqdn">Certificate FQDN.</param>
        /// <param name="generationId">Generation directory name.</param>
        /// <returns>The <c>complete</c> marker path.</returns>
        internal static string GenerationCompleteMarker(string stateDir, string fqdn, string generationId) =>
            Path.Join(GenerationDir(stateDir, fqdn, generationId), Complete);

        /// <summary>FQDN-scoped transient DNS-01 recovery directory.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="fqdn">Certificate FQDN.</param>
        /// <returns>The recovery directory <c>live/{fqdn}/dns01</c>.</returns>
        internal static string Dns01RecoveryDir(string stateDir, string fqdn) =>
            Path.Join(LiveDir(stateDir, fqdn), Dns01);

        /// <summary>FQDN-scoped issuance lock file path.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="fqdn">Certificate FQDN.</param>
        /// <returns>The lock path <c>live/{fqdn}/.issuance.lock</c>.</returns>
        internal static string IssuanceLockPath(string stateDir, string fqdn) =>
            Path.Join(LiveDir(stateDir, fqdn), IssuanceLockFile);
    }
}
