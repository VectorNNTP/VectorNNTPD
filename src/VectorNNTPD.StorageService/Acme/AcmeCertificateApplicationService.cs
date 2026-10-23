using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.StorageService.Acme
{

    /// <summary>
    /// StorageServer lifecycle wrapper around the shared ACME certificate implementation.
    /// </summary>
    /// <remarks>
    /// Delegates issuance, renewal, and persistence to Common
    /// <see cref="AcmeCertificateService"/>. Fail-fast readiness stays on this adapter.
    /// </remarks>
    public sealed class AcmeCertificateApplicationService : IApplicationService, IAsyncDisposable
    {
        private readonly AcmeCertificateService _inner;
        private readonly IAcmeCertificateReadiness _readiness;

        /// <summary>Initializes a new wrapper.</summary>
        public AcmeCertificateApplicationService(
            AcmeCertificateService inner,
            IAcmeCertificateReadiness readiness)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(readiness);
            _inner = inner;
            _readiness = readiness;
        }

        /// <inheritdoc />
        public string Name => AcmeCertificateService.Name;

        /// <inheritdoc />
        public Task? Execution => _inner.Execution;

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
            if (!_readiness.IsReady)
            {
                throw new InvalidOperationException(
                    "ACME certificate acquisition completed without publishing a usable certificate. StorageServer cannot start.");
            }
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);

        /// <inheritdoc />
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
