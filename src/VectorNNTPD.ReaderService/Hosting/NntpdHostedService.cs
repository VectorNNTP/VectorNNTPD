using System.Diagnostics;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.ReaderService.Logging;

namespace VectorNNTPD.ReaderService.Hosting
{
    /// <summary>
    /// Integrates <see cref="ApplicationLifecycle"/> with the .NET Generic Host.
    /// </summary>
    /// <remarks>
    /// Startup runs during <see cref="StartAsync"/> so a failed initialization prevents the host from
    /// reporting successful start. While running, the background execution waits for host shutdown or
    /// unexpected service termination. Fatal failures stop the host via
    /// <see cref="BackgroundServiceExceptionBehavior.StopHost"/>.
    /// </remarks>
    public sealed class NntpdHostedService : BackgroundService
    {
        private readonly ApplicationLifecycle _lifecycle;
        private readonly NntpdHostLifetime _hostLifetime;
        private readonly IOptions<NntpdOptions> _options;
        private readonly ILogger<NntpdHostedService> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="NntpdHostedService"/> class.
        /// </summary>
        public NntpdHostedService(
            ApplicationLifecycle lifecycle,
            NntpdHostLifetime hostLifetime,
            IOptions<NntpdOptions> options,
            ILogger<NntpdHostedService> logger)
        {
            ArgumentNullException.ThrowIfNull(lifecycle);
            ArgumentNullException.ThrowIfNull(hostLifetime);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);

            _lifecycle = lifecycle;
            _hostLifetime = hostLifetime;
            _options = options;
            _logger = logger;
        }

        /// <inheritdoc />
        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            HostingLogMessages.HostStartingApplication(_logger, _options.Value.ApplicationName);

            await _lifecycle.StartAsync(cancellationToken).ConfigureAwait(false);

            HostingLogMessages.ApplicationEnteredRunning(_logger, sw.ElapsedMilliseconds);

            await base.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await _lifecycle.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                HostingLogMessages.HostedServiceExecutionCanceled(_logger);
            }
            catch (InvalidOperationException ex)
            {
                HostingLogMessages.UnexpectedApplicationServiceTermination(_logger, ex);
                _hostLifetime.NotifyUnexpectedTermination();
                throw;
            }
        }

        /// <inheritdoc />
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            HostingLogMessages.HostStoppingApplication(_logger, _options.Value.ApplicationName);

            try
            {
                await _hostLifetime.RequestShutdownAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await base.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
