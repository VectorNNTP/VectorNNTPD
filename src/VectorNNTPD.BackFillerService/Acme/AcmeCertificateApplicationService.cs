using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.Acme;

namespace VectorNNTPD.BackFillerService.Acme
{
    /// <summary>
    /// BackFiller lifecycle wrapper around the shared ACME certificate implementation.
    /// </summary>
    /// <remarks>
    /// Delegates issuance, renewal, and persistence to Common
    /// <see cref="AcmeCertificateService"/>. BackFiller-specific fail-fast readiness
    /// and startup-journal recording stay on this adapter.
    /// </remarks>
    internal sealed class AcmeCertificateApplicationService : IApplicationService, IAsyncDisposable
    {
        /// <summary>Shared ACME service. Start, stop, execution, name, and dispose are forwarded to it.</summary>
        private readonly AcmeCertificateService _inner;

        /// <summary>Read after a successful inner start. Not owned and not disposed by this wrapper.</summary>
        private readonly IAcmeCertificateReadiness _readiness;

        /// <summary>Receives <see cref="BackFillerStartupStages.AcmeCertificateReady"/> after readiness passes. Not owned and not disposed here.</summary>
        private readonly IBackFillerStartupJournal _journal;

        /// <summary>Retains the inner ACME service, readiness gate, and startup journal.</summary>
        /// <param name="inner">Service that performs issuance and renewal.</param>
        /// <param name="readiness">Gate consulted after <paramref name="inner"/> start returns.</param>
        /// <param name="journal">Journal updated only when that gate is ready.</param>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        public AcmeCertificateApplicationService(
            AcmeCertificateService inner,
            IAcmeCertificateReadiness readiness,
            IBackFillerStartupJournal journal)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(readiness);
            ArgumentNullException.ThrowIfNull(journal);
            _inner = inner;
            _readiness = readiness;
            _journal = journal;
        }

        /// <inheritdoc />
        public string Name => AcmeCertificateService.Name;

        /// <inheritdoc />
        public Task? Execution => _inner.Execution;

        /// <summary>
        /// Starts the inner ACME service, then fails to startup when no usable certificate was published.
        /// </summary>
        /// <param name="cancellationToken">Forwarded only to <see cref="AcmeCertificateService.StartAsync"/>. The readiness check and journal write do not observe it.</param>
        /// <returns>A task that completes after <see cref="BackFillerStartupStages.AcmeCertificateReady"/> is recorded.</returns>
        /// <exception cref="InvalidOperationException">Inner start returned and <see cref="IAcmeCertificateReadiness.IsReady"/> is <see langword="false"/>.</exception>
        /// <remarks>
        /// Exceptions from the inner start propagate unchanged. This wrapper has no start latch of its own:
        /// each call that finds the certificate ready appends the journal stage again.
        /// A readiness failure does not stop or dispose <see cref="_inner"/>.
        /// </remarks>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
            if (!_readiness.IsReady)
            {
                throw new InvalidOperationException(
                    "ACME certificate acquisition completed without publishing a usable certificate. BackFiller cannot start.");
            }

            _journal.Record(BackFillerStartupStages.AcmeCertificateReady);
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);

        /// <summary>Disposes the inner ACME service.</summary>
        /// <returns>The dispose task returned by <see cref="AcmeCertificateService.DisposeAsync"/>.</returns>
        /// <remarks>Does not dispose <see cref="_readiness"/> or <see cref="_journal"/>.</remarks>
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
