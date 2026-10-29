using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.ReaderService.Acme
{
    /// <summary>
    /// NNTPD lifecycle wrapper around the shared ACME certificate implementation.
    /// </summary>
    public sealed class AcmeCertificateApplicationService : IApplicationService, IAsyncDisposable
    {
        private readonly AcmeCertificateService _inner;

        /// <summary>Initializes a new wrapper.</summary>
        public AcmeCertificateApplicationService(AcmeCertificateService inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
        }

        /// <inheritdoc />
        public string Name => AcmeCertificateService.Name;

        /// <inheritdoc />
        public Task? Execution => _inner.Execution;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken) => _inner.StartAsync(cancellationToken);

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);

        /// <inheritdoc />
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
