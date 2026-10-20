namespace VectorNNTPD.Common.Acme
{
    /// <summary>
    /// Cross-process exclusive lock for one certificate FQDN's issuance and promotion.
    /// </summary>
    /// <remarks>
    /// The lock file is <c>live/{fqdn}/.issuance.lock</c>. Distinct FQDNs use distinct files
    /// and can issue concurrently. The same FQDN is mutually exclusive across processes.
    /// Acquisition delegates to <see cref="AcmeExclusiveFileLock"/> so contention classification
    /// matches journal locks: only recognized sharing/lock violations retry; unrelated
    /// filesystem failures propagate immediately.
    /// </remarks>
    internal sealed class AcmeIssuanceLock : IAsyncDisposable, IDisposable
    {
        /// <summary>Underlying exclusive lock file for this FQDN.</summary>
        private readonly AcmeExclusiveFileLock _fileLock;

        /// <summary>Non-zero after <see cref="Dispose"/> has run.</summary>
        private int _disposed;

        /// <summary>Takes ownership of an already held <paramref name="fileLock"/>.</summary>
        /// <param name="fileLock">FQDN exclusive lock. Disposed by <see cref="Dispose"/>.</param>
        private AcmeIssuanceLock(AcmeExclusiveFileLock fileLock)
        {
            _fileLock = fileLock;
        }

        /// <summary>
        /// Acquires the FQDN-scoped issuance lock synchronously, waiting until it is available or cancelled.
        /// </summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="fqdn">Certificate FQDN that owns the lock.</param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>A held lock that must be disposed to release.</returns>
        /// <remarks>
        /// Used by <see cref="CertificateStore"/> for constructor recovery and synchronous Load/Save/Recover.
        /// Prefer <see cref="AcquireAsync"/> on async issuance paths.
        /// </remarks>
        internal static AcmeIssuanceLock Acquire(
            string stateDir,
            string fqdn,
            CancellationToken cancellationToken = default)
        {
            var path = ResolveLockPath(stateDir, fqdn);
            return new AcmeIssuanceLock(AcmeExclusiveFileLock.Acquire(path, cancellationToken));
        }

        /// <summary>
        /// Acquires the FQDN-scoped issuance lock, waiting until it is available or cancelled.
        /// </summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="fqdn">Certificate FQDN that owns the lock.</param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>A held lock that must be disposed to release.</returns>
        internal static async Task<AcmeIssuanceLock> AcquireAsync(
            string stateDir,
            string fqdn,
            CancellationToken cancellationToken)
        {
            var path = ResolveLockPath(stateDir, fqdn);
            var fileLock = await AcmeExclusiveFileLock
                .AcquireAsync(path, cancellationToken)
                .ConfigureAwait(false);
            return new AcmeIssuanceLock(fileLock);
        }

        /// <summary>
        /// Attempts one exclusive open of the FQDN issuance lock file.
        /// </summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="fqdn">Certificate FQDN that owns the lock.</param>
        /// <param name="acquired">Held lock when this method returns <see langword="true"/>.</param>
        /// <returns>
        /// <see langword="false"/> when another holder owns the file (recognized contention).
        /// Unrelated filesystem failures are thrown.
        /// </returns>
        /// <remarks>
        /// Exposed for tests that must prove synchronous and asynchronous classification stay aligned
        /// without entering a wait loop.
        /// </remarks>
        internal static bool TryAcquireOnce(
            string stateDir,
            string fqdn,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AcmeIssuanceLock? acquired)
        {
            var path = ResolveLockPath(stateDir, fqdn);
            if (!AcmeExclusiveFileLock.TryAcquireOnce(path, out AcmeExclusiveFileLock? fileLock))
            {
                acquired = null;
                return false;
            }

            acquired = new AcmeIssuanceLock(fileLock);
            return true;
        }

        /// <summary>Normalizes <paramref name="fqdn"/> and returns <c>live/{fqdn}/.issuance.lock</c>.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="fqdn">Certificate FQDN that owns the lock.</param>
        /// <returns>Absolute or rooted issuance lock path under <paramref name="stateDir"/>.</returns>
        private static string ResolveLockPath(string stateDir, string fqdn)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
            var identity = CertificateIdentities.NormalizeFqdn(fqdn);
            AcmePaths.EnsureCertificateIdentityLayout(stateDir, identity);
            return AcmePaths.IssuanceLockPath(stateDir, identity);
        }

        /// <summary>Closes the lock stream, releasing the FQDN issuance lock. A second call does nothing.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _fileLock.Dispose();
        }

        /// <summary>Releases the lock synchronously and returns a completed task.</summary>
        /// <returns>A completed <see cref="ValueTask"/>.</returns>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
