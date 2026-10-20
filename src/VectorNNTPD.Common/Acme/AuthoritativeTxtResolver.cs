using System.Net;
using VectorNNTPD.Common.Dns;

namespace VectorNNTPD.Common.Acme
{
    /// <summary>
    /// Resolves challenge TXT visibility by querying authoritative nameservers for the configured DNS apex
    /// (recursion disabled on TXT queries). Injectable for offline tests via <see cref="IAuthoritativeTxtResolver"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>NS discovery (product contract):</b> discovers NS for the configured DNS apex (<c>DnsSuffix</c>) only.
    /// Challenge hostnames are not walked to find a delegation cut; zone-apex configuration is authoritative.
    /// </para>
    /// <para>
    /// <b>TXT visibility (product contract):</b> requires the expected token on every reachable authoritative
    /// address that answers successfully (intersection of successful answers). Unreachable or failed servers
    /// are skipped and do not vote. Live queries require AA=1, a matching question/answer owner name, and
    /// exact TXT character-string bytes (no trim or quote stripping).
    /// </para>
    /// <para>
    /// Bootstrap/recursive DNS is used only to discover apex NS names and resolve NS A/AAAA via
    /// <see cref="ZoneApexNameserverDiscovery"/>. TXT queries are sent directly to those addresses via
    /// <see cref="AuthoritativeTxtClient"/> (UDP then TCP, RD=0).
    /// </para>
    /// </remarks>
    internal sealed class AuthoritativeTxtResolver : IAuthoritativeTxtResolver
    {
        /// <summary>Normalized DNS apex whose NS set is queried. Challenge names are not used to discover a cut.</summary>
        private readonly string _zoneApex;

        /// <summary>Logger for skipped authoritative queries.</summary>
        private readonly ILogger<AuthoritativeTxtResolver> _logger;

        /// <summary>Test override for NS endpoints. <see langword="null"/> discovers them from <see cref="_zoneApex"/>.</summary>
        private readonly Func<CancellationToken, Task<IReadOnlyList<IPEndPoint>>>? _nameserverProvider;

        /// <summary>Test override for TXT answers. <see langword="null"/> queries UDP then TCP with RD=0.</summary>
        private readonly Func<IPAddress, string, CancellationToken, Task<IReadOnlyList<string>>>? _txtQuery;

        /// <summary>Uses live apex NS discovery and live TXT queries.</summary>
        /// <param name="zoneApex">DNS apex (<c>DnsSuffix</c>). Wildcards and empty labels throw <see cref="AcmeConfigurationException"/>.</param>
        /// <param name="logger">Logger for skipped queries.</param>
        internal AuthoritativeTxtResolver(string zoneApex, ILogger<AuthoritativeTxtResolver> logger)
            : this(zoneApex, logger, nameserverProvider: null, txtQuery: null)
        {
        }

        /// <summary>Test constructor with injectable authoritative endpoints.</summary>
        /// <param name="zoneApex">DNS apex. Still normalized, even when endpoints are injected.</param>
        /// <param name="logger">Logger for skipped queries.</param>
        /// <param name="nameserverProvider">Endpoint source. <see langword="null"/> uses live discovery.</param>
        internal AuthoritativeTxtResolver(
            string zoneApex,
            ILogger<AuthoritativeTxtResolver> logger,
            Func<CancellationToken, Task<IReadOnlyList<IPEndPoint>>>? nameserverProvider)
            : this(zoneApex, logger, nameserverProvider, txtQuery: null)
        {
        }

        /// <summary>Test constructor with injectable endpoints and TXT answers (no live DNS).</summary>
        /// <param name="zoneApex">DNS apex. Still normalized.</param>
        /// <param name="logger">Logger for skipped queries.</param>
        /// <param name="nameserverProvider">Endpoint source. <see langword="null"/> uses live discovery.</param>
        /// <param name="txtQuery">TXT answer source. <see langword="null"/> uses live UDP/TCP queries.</param>
        internal AuthoritativeTxtResolver(
            string zoneApex,
            ILogger<AuthoritativeTxtResolver> logger,
            Func<CancellationToken, Task<IReadOnlyList<IPEndPoint>>>? nameserverProvider,
            Func<IPAddress, string, CancellationToken, Task<IReadOnlyList<string>>>? txtQuery)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(zoneApex);
            ArgumentNullException.ThrowIfNull(logger);
            _zoneApex = DnsZoneCoverage.NormalizeDnsHostname(zoneApex, "DNSSuffix");
            _logger = logger;
            _nameserverProvider = nameserverProvider;
            _txtQuery = txtQuery;
        }

        /// <summary>
        /// Returns the intersection of TXT strings from every authoritative address that answers.
        /// Failed servers are skipped. An empty nameserver list throws <see cref="AcmeChallengeException"/> category <c>ns_discovery_failed</c>.
        /// When every query fails, the result is empty.
        /// </summary>
        /// <param name="name">TXT owner name. A trailing dot is removed before the query.</param>
        /// <param name="cancellationToken">Cancels discovery and each query.</param>
        /// <returns>TXT values present on every successful answer. Empty when no server answered.</returns>
        public async Task<IReadOnlyList<string>> LookupTxtAsync(string name, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            cancellationToken.ThrowIfCancellationRequested();

            var servers = _nameserverProvider is not null
                ? await _nameserverProvider(cancellationToken).ConfigureAwait(false)
                : await DiscoverAuthoritativeEndpointsAsync(cancellationToken).ConfigureAwait(false);

            if (servers.Count == 0)
            {
                throw new AcmeChallengeException("ns_discovery_failed", "no authoritative nameservers");
            }

            HashSet<string>? intersection = null;
            var successCount = 0;
            foreach (var server in servers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    IReadOnlyList<string> raw = _txtQuery is not null
                        ? await _txtQuery(server.Address, name.TrimEnd('.'), cancellationToken).ConfigureAwait(false)
                        : await AuthoritativeTxtClient
                            .QueryTxtAsync(server.Address, name.TrimEnd('.'), cancellationToken, _logger)
                            .ConfigureAwait(false);
                    // Exact wire/presentation bytes from the parser; do not trim or strip quotes.
                    var values = raw
                        .Where(static t => t.Length > 0)
                        .ToHashSet(StringComparer.Ordinal);
                    intersection = intersection is null
                        ? values
                        : intersection.Intersect(values, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
                    successCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AcmeLogMessages.AuthoritativeTxtLookupFailed(_logger, ex, name, server);
                }
            }

            if (successCount == 0 || intersection is null)
            {
                return [];
            }

            return (string[])[.. intersection];
        }

        /// <summary>Resolves apex NS addresses and returns them as port-53 endpoints. An empty set throws category <c>ns_discovery_failed</c>.</summary>
        /// <param name="cancellationToken">Cancels NS discovery.</param>
        /// <returns>One endpoint per discovered address.</returns>
        private async Task<IReadOnlyList<IPEndPoint>> DiscoverAuthoritativeEndpointsAsync(
            CancellationToken cancellationToken)
        {
            IReadOnlyList<IPAddress> addresses = await ZoneApexNameserverDiscovery
                .DiscoverAddressesAsync(_zoneApex, _logger, cancellationToken)
                .ConfigureAwait(false);

            if (addresses.Count == 0)
            {
                throw new AcmeChallengeException("ns_discovery_failed", "empty NS set");
            }

            var endpoints = new List<IPEndPoint>(addresses.Count);
            foreach (IPAddress address in addresses)
            {
                endpoints.Add(new IPEndPoint(address, 53));
            }

            return endpoints;
        }
    }
}
