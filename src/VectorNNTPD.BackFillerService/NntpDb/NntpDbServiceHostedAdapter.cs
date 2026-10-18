using VectorNNTPD.Common.NntpDb;

namespace VectorNNTPD.BackFillerService.NntpDb
{
    /// <summary>
    /// Starts Common's <see cref="NntpDbService"/> before later hosted services, including provider-account refresh.
    /// </summary>
    /// <remarks>
    /// The service is not registered as <see cref="VectorNNTPD.Common.Core.IApplicationService"/>, so
    /// <see cref="VectorNNTPD.BackFillerService.Core.ApplicationServiceManager"/> does not start it again.
    /// </remarks>
    internal sealed class NntpDbServiceHostedAdapter : IHostedService
    {
        /// <summary>Shared database service. Start and stop are forwarded; this adapter does not dispose it.</summary>
        private readonly NntpDbService _service;

        /// <summary>Retains the shared database service.</summary>
        /// <param name="service">Service started and stopped with this adapter.</param>
        internal NntpDbServiceHostedAdapter(NntpDbService service)
        {
            ArgumentNullException.ThrowIfNull(service);
            _service = service;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken) => _service.StartAsync(cancellationToken);

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => _service.StopAsync(cancellationToken);
    }
}
