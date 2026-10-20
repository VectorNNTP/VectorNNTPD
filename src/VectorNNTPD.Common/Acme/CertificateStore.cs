using System.Text.RegularExpressions;

namespace VectorNNTPD.Common.Acme
{
    /// <summary>
    /// Crash-safe FQDN-scoped live server certificate store using generation directories
    /// and a <c>current</c> pointer.
    /// Persists the TLS credential as PKCS#12/PFX (<c>certificate.pfx</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recovery is conservative: an I/O or access failure while reading the pointer or inspecting a
    /// generation is never treated as proof that the certificate is absent or incomplete, and never
    /// triggers destructive cleanup. Destructive cleanup runs only when a complete current generation
    /// is positively established, or when a pointed-to generation is positively incomplete (marker/PFX
    /// missing or empty) — and then only that incomplete generation (plus its pointer) is removed.
    /// A missing pointer does not auto-promote orphans; a later successful Save GCs non-current
    /// generations once a new current id is committed.
    /// </para>
    /// <para>
    /// Public entry points acquire the FQDN <see cref="AcmeIssuanceLock"/>. Callers that already hold
    /// that lock (for example <see cref="CertificateManager"/>) must use the <c>*UnderLock</c> methods
    /// to avoid a same-process exclusive-lock deadlock.
    /// </para>
    /// </remarks>
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

        /// <summary>
        /// Optional filesystem seam for deterministic fault injection in tests.
        /// <see langword="null"/> uses the real filesystem.
        /// </summary>
        internal ICertificateStoreFileAccess? FileAccessForTests { get; set; }

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
            using var issuanceLock = AcmeIssuanceLock.Acquire(_stateDir, _fqdn);
            RecoverUnderLock();
        }

        /// <summary>Gets the FQDN this store is scoped to.</summary>
        private string Fqdn => _fqdn;

        /// <summary>Gets the password that last loaded or saved the current generation. Not logged.</summary>
        internal string CurrentPfxPassword => _pfxPassword;

        /// <summary>Returns paths for the active generation (after recovery).</summary>
        internal CertificatePaths Paths()
        {
            using var issuanceLock = AcmeIssuanceLock.Acquire(_stateDir, _fqdn);
            return PathsUnderLock();
        }

        /// <summary>Returns paths for the active generation. Caller must hold the FQDN issuance lock.</summary>
        internal CertificatePaths PathsUnderLock()
        {
            RecoverUnderLock();
            var active = TryGetCompleteCurrentIdUnderLock();
            if (active is not null)
            {
                return AcmePaths.GenerationCertificatePaths(_stateDir, _fqdn, active);
            }

            throw new AcmeStorageException("no_certificate", "no complete certificate generation");
        }

        /// <summary>Returns the active generation id, or <see langword="null"/> when none is current.</summary>
        internal string? CurrentGenerationId()
        {
            using var issuanceLock = AcmeIssuanceLock.Acquire(_stateDir, _fqdn);
            return CurrentGenerationIdUnderLock();
        }

        /// <summary>Returns the active generation id. Caller must hold the FQDN issuance lock.</summary>
        internal string? CurrentGenerationIdUnderLock()
        {
            RecoverUnderLock();
            return TryGetCompleteCurrentIdUnderLock();
        }

        /// <summary>
        /// Loads and validates the active PFX, or <see langword="null"/> when no complete generation exists.
        /// </summary>
        internal CertificateMaterial? Load()
        {
            using var issuanceLock = AcmeIssuanceLock.Acquire(_stateDir, _fqdn);
            return LoadUnderLock();
        }

        /// <summary>
        /// Loads and validates the active PFX. Caller must hold the FQDN issuance lock.
        /// </summary>
        internal CertificateMaterial? LoadUnderLock()
        {
            RecoverUnderLock();
            var active = TryGetCompleteCurrentIdUnderLock();
            if (active is null)
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
            catch (UnauthorizedAccessException ex)
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
            using var issuanceLock = AcmeIssuanceLock.Acquire(_stateDir, _fqdn);
            return SaveUnderLock(material, pfxPassword);
        }

        /// <summary>
        /// Persists a new generation. Caller must hold the FQDN issuance lock.
        /// </summary>
        internal CertificatePaths SaveUnderLock(CertificateMaterial material, string? pfxPassword = null)
        {
            ArgumentNullException.ThrowIfNull(material);
            var password = pfxPassword ?? _pfxPassword;
            ArgumentException.ThrowIfNullOrWhiteSpace(password);
            AcmePaths.EnsureCertificateIdentityLayout(_stateDir, _fqdn);
            RecoverUnderLock();

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
                catch (UnauthorizedAccessException ex)
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
            using var issuanceLock = AcmeIssuanceLock.Acquire(_stateDir, _fqdn);
            RecoverUnderLock();
        }

        /// <summary>
        /// Idempotent recovery. Caller must hold the FQDN issuance lock.
        /// </summary>
        /// <remarks>
        /// Decision tree (destructive steps only when positively safe):
        /// <list type="bullet">
        /// <item><description>Unreadable pointer or indeterminate generation inspection → throw; mutate nothing.</description></item>
        /// <item><description>Valid pointer + complete generation → GC other generations only.</description></item>
        /// <item><description>Valid pointer + positively incomplete generation → delete that pointer and that generation only.</description></item>
        /// <item><description>Malformed pointer (read succeeded, content invalid) → delete pointer only; leave all generations.</description></item>
        /// <item><description>
        /// Missing pointer → leave all generations (may still hold a recoverable complete PFX).
        /// Automatic promotion is not performed: a later successful <see cref="Save"/> /
        /// <see cref="SaveUnderLock"/> GCs non-current generations once a new current id is established.
        /// </description></item>
        /// </list>
        /// </remarks>
        internal void RecoverUnderLock()
        {
            AcmePaths.EnsureCertificateIdentityLayout(_stateDir, _fqdn);
            var pointerPath = AcmePaths.CurrentGenerationPointerPath(_stateDir, _fqdn);
            CurrentPointerState pointer = ReadCurrentPointerState();

            switch (pointer.Kind)
            {
                case CurrentPointerKind.Unreadable:
                    throw new AcmeStorageException(
                        "current_pointer_unreadable",
                        pointer.FailureDetail ?? "IOException");

                case CurrentPointerKind.Valid:
                    {
                        string generationId = pointer.GenerationId!;
                        GenerationInspection inspection = InspectGeneration(generationId);
                        if (inspection == GenerationInspection.Indeterminate)
                        {
                            throw new AcmeStorageException(
                                "generation_inspection_failed",
                                generationId);
                        }

                        if (inspection == GenerationInspection.Complete)
                        {
                            CleanupNonCurrentGenerations(keep: generationId);
                            return;
                        }

                        // Positively incomplete: remove the pointer first. Only delete the generation
                        // after the pointer is confirmed gone so a failed pointer delete cannot leave
                        // current referencing a removed directory.
                        if (!TryDeleteCurrentPointer(pointerPath))
                        {
                            throw new AcmeStorageException(
                                "current_pointer_delete_failed",
                                generationId);
                        }

                        TryDeleteDirectory(AcmePaths.GenerationDir(_stateDir, _fqdn, generationId));
                        return;
                    }

                case CurrentPointerKind.Malformed:
                    // Content was read successfully but is not a generation id. Safe to remove the
                    // pointer alone; generations may still hold a complete recoverable PFX.
                    if (!TryDeleteCurrentPointer(pointerPath))
                    {
                        throw new AcmeStorageException(
                            "current_pointer_delete_failed",
                            "malformed");
                    }

                    return;

                case CurrentPointerKind.Missing:
                    // No pointer must not imply "delete every generation". Orphans remain until a
                    // later Save establishes a new current generation and GCs non-current dirs.
                    return;

                default:
                    throw new InvalidOperationException($"Unexpected pointer kind {pointer.Kind}.");
            }
        }

        /// <summary>
        /// Returns the current generation id when the pointer is valid and that generation is complete.
        /// Throws when the pointer or generation cannot be inspected safely.
        /// </summary>
        private string? TryGetCompleteCurrentIdUnderLock()
        {
            CurrentPointerState pointer = ReadCurrentPointerState();
            return pointer.Kind switch
            {
                CurrentPointerKind.Missing or CurrentPointerKind.Malformed => null,
                CurrentPointerKind.Unreadable => throw new AcmeStorageException(
                    "current_pointer_unreadable",
                    pointer.FailureDetail ?? "IOException"),
                CurrentPointerKind.Valid => InspectGeneration(pointer.GenerationId!) switch
                {
                    GenerationInspection.Complete => pointer.GenerationId,
                    GenerationInspection.Incomplete => null,
                    GenerationInspection.Indeterminate => throw new AcmeStorageException(
                        "generation_inspection_failed",
                        pointer.GenerationId!),
                    _ => null,
                },
                _ => null,
            };
        }

        /// <summary>Reads the current-pointer file without collapsing I/O failure into "missing".</summary>
        private CurrentPointerState ReadCurrentPointerState()
        {
            var path = AcmePaths.CurrentGenerationPointerPath(_stateDir, _fqdn);
            ICertificateStoreFileAccess io = FileAccessForTests ?? RealCertificateStoreFileAccess.Instance;

            bool exists;
            try
            {
                exists = io.FileExists(path);
            }
            catch (IOException ex)
            {
                return CurrentPointerState.Unreadable(ex.GetType().Name);
            }
            catch (UnauthorizedAccessException ex)
            {
                return CurrentPointerState.Unreadable(ex.GetType().Name);
            }

            if (!exists)
            {
                return CurrentPointerState.Missing();
            }

            try
            {
                var raw = io.ReadAllText(path).Trim();
                return GenerationIdRegex().IsMatch(raw)
                    ? CurrentPointerState.Valid(raw)
                    : CurrentPointerState.Malformed();
            }
            catch (IOException ex)
            {
                return CurrentPointerState.Unreadable(ex.GetType().Name);
            }
            catch (UnauthorizedAccessException ex)
            {
                return CurrentPointerState.Unreadable(ex.GetType().Name);
            }
        }

        /// <summary>
        /// Inspects whether a generation is positively complete, positively incomplete, or indeterminate.
        /// </summary>
        private GenerationInspection InspectGeneration(string generationId)
        {
            if (!GenerationIdRegex().IsMatch(generationId))
            {
                return GenerationInspection.Incomplete;
            }

            ICertificateStoreFileAccess io = FileAccessForTests ?? RealCertificateStoreFileAccess.Instance;
            var marker = AcmePaths.GenerationCompleteMarker(_stateDir, _fqdn, generationId);
            var paths = AcmePaths.GenerationCertificatePaths(_stateDir, _fqdn, generationId);

            try
            {
                if (!io.FileExists(marker) || !io.FileExists(paths.PfxPath))
                {
                    return GenerationInspection.Incomplete;
                }

                return io.GetFileLength(paths.PfxPath) > 0
                    ? GenerationInspection.Complete
                    : GenerationInspection.Incomplete;
            }
            catch (IOException)
            {
                return GenerationInspection.Indeterminate;
            }
            catch (UnauthorizedAccessException)
            {
                return GenerationInspection.Indeterminate;
            }
        }

        /// <summary>
        /// Best-effort delete of the current pointer, returning whether the file is gone afterward.
        /// </summary>
        /// <param name="pointerPath">Path to <c>current</c>.</param>
        /// <returns>
        /// <see langword="true"/> only when a post-delete existence check positively observes the file as absent.
        /// An I/O or access failure while inspecting existence returns <see langword="false"/> (not treated as absent).
        /// </returns>
        private bool TryDeleteCurrentPointer(string pointerPath)
        {
            ICertificateStoreFileAccess io = FileAccessForTests ?? RealCertificateStoreFileAccess.Instance;
            io.TryDelete(pointerPath);
            try
            {
                return !io.FileExists(pointerPath);
            }
            catch (IOException)
            {
                // Inspection failed — must not treat as proof the pointer is gone.
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Deletes every generation directory except <paramref name="keep"/>. IO failures are ignored.</summary>
        /// <param name="keep">Generation id to retain. Must not be null — callers never pass null after #183.</param>
        private void CleanupNonCurrentGenerations(string keep)
        {
            ArgumentException.ThrowIfNullOrEmpty(keep);
            var root = AcmePaths.GenerationsDir(_stateDir, _fqdn);
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var entry in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, keep, StringComparison.Ordinal))
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

        /// <summary>Outcome of reading the <c>current</c> pointer file.</summary>
        private enum CurrentPointerKind
        {
            /// <summary>Pointer file does not exist.</summary>
            Missing,

            /// <summary>Pointer contains a valid 32-hex generation id.</summary>
            Valid,

            /// <summary>Pointer was read but content is not a valid generation id.</summary>
            Malformed,

            /// <summary>Pointer exists (or existence is unknown) but could not be read safely.</summary>
            Unreadable,
        }

        /// <summary>Structured result of <see cref="ReadCurrentPointerState"/>.</summary>
        private readonly struct CurrentPointerState
        {
            private CurrentPointerState(CurrentPointerKind kind, string? generationId, string? failureDetail)
            {
                Kind = kind;
                GenerationId = generationId;
                FailureDetail = failureDetail;
            }

            public CurrentPointerKind Kind { get; }

            public string? GenerationId { get; }

            public string? FailureDetail { get; }

            public static CurrentPointerState Missing() => new(CurrentPointerKind.Missing, null, null);

            public static CurrentPointerState Valid(string generationId) =>
                new(CurrentPointerKind.Valid, generationId, null);

            public static CurrentPointerState Malformed() => new(CurrentPointerKind.Malformed, null, null);

            public static CurrentPointerState Unreadable(string detail) =>
                new(CurrentPointerKind.Unreadable, null, detail);
        }

        /// <summary>Result of inspecting one generation directory.</summary>
        private enum GenerationInspection
        {
            /// <summary>Marker and non-empty PFX are present.</summary>
            Complete,

            /// <summary>Missing marker/PFX, empty PFX, or invalid id — positively incomplete.</summary>
            Incomplete,

            /// <summary>Existence or length could not be determined because of I/O or access failure.</summary>
            Indeterminate,
        }
    }

    /// <summary>
    /// Narrow filesystem seam used by <see cref="CertificateStore"/> for pointer and generation inspection.
    /// </summary>
    internal interface ICertificateStoreFileAccess
    {
        /// <summary>Returns whether <paramref name="path"/> exists.</summary>
        bool FileExists(string path);

        /// <summary>Reads the entire text of <paramref name="path"/>.</summary>
        string ReadAllText(string path);

        /// <summary>Returns the length in bytes of <paramref name="path"/>.</summary>
        long GetFileLength(string path);

        /// <summary>
        /// Best-effort delete of <paramref name="path"/>.
        /// Implementations may ignore <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/>.
        /// </summary>
        /// <param name="path">File to delete when present.</param>
        void TryDelete(string path);
    }

    /// <summary>Default <see cref="ICertificateStoreFileAccess"/> backed by <see cref="File"/> / <see cref="FileInfo"/>.</summary>
    internal sealed class RealCertificateStoreFileAccess : ICertificateStoreFileAccess
    {
        /// <summary>Shared instance.</summary>
        internal static RealCertificateStoreFileAccess Instance { get; } = new();

        /// <inheritdoc />
        public bool FileExists(string path) => File.Exists(path);

        /// <inheritdoc />
        public string ReadAllText(string path) => File.ReadAllText(path);

        /// <inheritdoc />
        public long GetFileLength(string path) => new FileInfo(path).Length;

        /// <inheritdoc />
        public void TryDelete(string path) => AtomicFile.TryDelete(path);
    }
}
