using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Logging;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Networking.Listeners;

namespace VectorNNTPD.StorageService.Hosting
{

    /// <summary>
    /// Integrates <see cref="ApplicationLifecycle"/> with the .NET Generic Host.
    /// </summary>
    public sealed class StorageServerHostedService : BackgroundService
    {
        private readonly ApplicationLifecycle _lifecycle;
        private readonly StorageServerHostShutdown _hostShutdown;
        private readonly IOptions<StorageServerOptions> _options;
        private readonly IOptions<AcmeCloudflareOptions> _acmeOptions;
        private readonly IBindAddressResolver _bindAddressResolver;
        private readonly ILogger<StorageServerHostedService> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="StorageServerHostedService"/> class.
        /// </summary>
        public StorageServerHostedService(
            ApplicationLifecycle lifecycle,
            StorageServerHostShutdown hostShutdown,
            IOptions<StorageServerOptions> options,
            IOptions<AcmeCloudflareOptions> acmeOptions,
            IBindAddressResolver bindAddressResolver,
            ILogger<StorageServerHostedService> logger)
        {
            ArgumentNullException.ThrowIfNull(lifecycle);
            ArgumentNullException.ThrowIfNull(hostShutdown);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(acmeOptions);
            ArgumentNullException.ThrowIfNull(bindAddressResolver);
            ArgumentNullException.ThrowIfNull(logger);

            _lifecycle = lifecycle;
            _hostShutdown = hostShutdown;
            _options = options;
            _acmeOptions = acmeOptions;
            _bindAddressResolver = bindAddressResolver;
            _logger = logger;
        }

        /// <inheritdoc />
        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            HostingLogMessages.HostStartingApplication(_logger, _options.Value.ApplicationName);
            LogBindAddressDiagnostics();

            await _lifecycle.StartAsync(cancellationToken).ConfigureAwait(false);

            HostingLogMessages.ApplicationEnteredRunning(_logger, sw.ElapsedMilliseconds);

            await base.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        private void LogBindAddressDiagnostics()
        {
            var acme = _acmeOptions.Value;
            var tokens = acme.BindAddress is { Length: > 0 } ? acme.BindAddress : ["*"];
            var addresses = tokens
                .Where(static token => !string.IsNullOrWhiteSpace(token)
                    && !AcmeCloudflareOptions.IsBindAddressWildcard(token)
                    && IPAddress.TryParse(token.Trim(), out _))
                .Select(static token => IPAddress.Parse(token.Trim()))
                .ToList();

            var ipv4Configured = string.Join(
                ", ",
                addresses
                    .Where(static a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(IpAddressEligibility.ToDnsContent));
            var ipv6Configured = string.Join(
                ", ",
                addresses
                    .Where(static a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    .Select(IpAddressEligibility.ToDnsContent));
            HostingLogMessages.BindAddressesConfigured(
                _logger,
                string.Join(", ", tokens),
                string.IsNullOrEmpty(ipv4Configured) ? "(none)" : ipv4Configured,
                string.IsNullOrEmpty(ipv6Configured) ? "(none)" : ipv6Configured);

            var bindings = ListenEndpointPlanner.Plan(tokens, acme.BindPortTls);
            var endpoints = string.Join(
                ", ",
                bindings.Select(static b => FormatEndpoint(b.Address, b.Port)));
            HostingLogMessages.TlsListenerEndpointsPlanned(
                _logger,
                string.IsNullOrEmpty(endpoints) ? "(none)" : endpoints);

            var resolved = _bindAddressResolver.Resolve(_acmeOptions.Value);
            var aRecords = string.Join(", ", resolved.IPv4.Select(IpAddressEligibility.ToDnsContent));
            var aaaaRecords = string.Join(", ", resolved.IPv6.Select(IpAddressEligibility.ToDnsContent));
            HostingLogMessages.CloudflareDnsAddressesResolved(
                _logger,
                string.IsNullOrEmpty(aRecords) ? "(none)" : aRecords,
                string.IsNullOrEmpty(aaaaRecords) ? "(none)" : aaaaRecords);
        }

        private static string FormatEndpoint(IPAddress address, int port)
        {
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return $"[{IpAddressEligibility.ToDnsContent(address)}]:{port}";
            }

            return $"{IpAddressEligibility.ToDnsContent(address)}:{port}";
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
                _hostShutdown.NotifyUnexpectedTermination();
                throw;
            }
        }

        /// <inheritdoc />
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            HostingLogMessages.HostStoppingApplication(_logger, _options.Value.ApplicationName);

            try
            {
                await _hostShutdown.RequestShutdownAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await base.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
