using System.Net;
using DnsClient;
using DnsClient.Protocol;
using VectorNNTPD.TransitService.Networking.Proxy;

namespace VectorNNTPD.TransitService.Transit
{
    /// <summary>
    /// Resolves AllowFrom hostnames with DnsClient so A/AAAA record TTLs are available.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each lookup queries A and AAAA. DnsClient exposes <c>TimeToLive</c> on answer records;
    /// the refresh interval uses the minimum TTL among returned records, floored at 60 seconds
    /// by <see cref="TransitDnsAddressCache"/>.
    /// </para>
    /// <para>
    /// If a successful answer somehow omitted TTL information, <see cref="TransitDnsResolveResult.TimeToLive"/>
    /// is <see langword="null"/> and the cache falls back to the 60-second minimum. This resolver
    /// does not invent a fixed polling interval in place of record TTLs.
    /// </para>
    /// </remarks>
    public sealed class TransitDnsClientResolver : ITransitDnsResolver
    {
        private readonly ILookupClient _client;

        /// <summary>Initializes a resolver with the system DNS lookup client.</summary>
        public TransitDnsClientResolver()
            : this(new LookupClient())
        {
        }

        /// <summary>Initializes a resolver with an injected lookup client (tests).</summary>
        public TransitDnsClientResolver(ILookupClient client)
        {
            ArgumentNullException.ThrowIfNull(client);
            _client = client;
        }

        /// <inheritdoc />
        public async Task<TransitDnsResolveResult> ResolveAsync(string hostname, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
            var name = hostname.Trim().TrimEnd('.');

            IDnsQueryResponse a;
            IDnsQueryResponse aaaa;
            try
            {
                a = await _client.QueryAsync(name, QueryType.A, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                aaaa = await _client.QueryAsync(name, QueryType.AAAA, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch
            {
                return TransitDnsResolveResult.TransientFailure();
            }

            var aTransient = IsTransient(a);
            var aaaaTransient = IsTransient(aaaa);
            if (aTransient && aaaaTransient)
            {
                return TransitDnsResolveResult.TransientFailure();
            }

            var addresses = new List<IPAddress>();
            var ttlSeconds = int.MaxValue;
            var sawTtl = false;
            Collect(a, addresses, ref ttlSeconds, ref sawTtl);
            Collect(aaaa, addresses, ref ttlSeconds, ref sawTtl);

            if (addresses.Count == 0)
            {
                if (aTransient || aaaaTransient)
                {
                    return TransitDnsResolveResult.TransientFailure();
                }

                return TransitDnsResolveResult.Empty(sawTtl ? TimeSpan.FromSeconds(ttlSeconds) : null);
            }

            return TransitDnsResolveResult.Success(
                addresses,
                sawTtl ? TimeSpan.FromSeconds(ttlSeconds) : null);
        }

        private static bool IsTransient(IDnsQueryResponse response)
        {
            if (!response.HasError)
            {
                return false;
            }

            return response.Header.ResponseCode is not DnsHeaderResponseCode.NotExistentDomain;
        }

        private static void Collect(
            IDnsQueryResponse response,
            List<IPAddress> addresses,
            ref int ttlSeconds,
            ref bool sawTtl)
        {
            if (response.HasError)
            {
                return;
            }

            foreach (var record in response.Answers)
            {
                IPAddress? address = record switch
                {
                    ARecord a => a.Address,
                    AaaaRecord aaaa => aaaa.Address,
                    _ => null,
                };
                if (address is null)
                {
                    continue;
                }

                addresses.Add(TrustedProxyHosts.Canonicalize(address));
                if (record.TimeToLive > 0)
                {
                    sawTtl = true;
                    ttlSeconds = Math.Min(ttlSeconds, record.TimeToLive);
                }
            }
        }
    }
}
