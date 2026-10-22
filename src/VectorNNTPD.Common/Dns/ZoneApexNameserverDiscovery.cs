using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.Common.Dns
{
    /// <summary>
    /// Discovers authoritative nameserver addresses for a configured DNS zone apex via recursive bootstrap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Product contract:</b> queries NS for the configured zone apex (<c>DnsSuffix</c>) only. There is no
    /// label-walk from challenge hostnames; apex configuration remains authoritative for ACME DNS-01.
    /// </para>
    /// <para>
    /// Bootstrap uses OS-configured or public recursive resolvers (RD=1) solely to obtain apex NS names and
    /// resolve NS hostnames (glue, then recursive wire A/AAAA, then OS stub). Callers must query the returned
    /// addresses directly for TXT with RD=0 (see <see cref="AuthoritativeTxtClient"/>).
    /// </para>
    /// <para>
    /// Supported record types: NS, A, and AAAA only. This type does not resolve TXT and does not expose a
    /// generic <c>ResolveAsync</c> API.
    /// </para>
    /// </remarks>
    internal static class ZoneApexNameserverDiscovery
    {
        /// <summary>UDP receive window, in milliseconds, for each recursive NS, A, and AAAA query.</summary>
        private const int UdpTimeoutMs = 5_000;

        /// <summary>
        /// OS DNS servers discovered at type initialization, or <c>1.1.1.1</c> and <c>8.8.8.8</c> when none qualify or discovery throws.
        /// </summary>
        private static readonly IPAddress[] RecursiveResolvers = ResolveRecursiveResolvers();

        /// <summary>Exception from OS resolver discovery, when public fallback resolvers are in use. Null otherwise.</summary>
        private static Exception? s_recursiveResolverDiscoveryException;

        /// <summary>Non-zero after the public-fallback event has been logged once.</summary>
        private static int s_loggedRecursiveResolverFallback;

        /// <summary>
        /// Resolves distinct authoritative NS addresses for <paramref name="zoneApex"/>.
        /// </summary>
        /// <param name="zoneApex">Configured zone apex. A trailing dot is removed. There is no label walk.</param>
        /// <param name="logger">Optional logger for fallback and empty-result diagnostics. Null suppresses those events.</param>
        /// <param name="cancellationToken">Cancels resolver queries and hostname resolution. Cancellation propagates.</param>
        /// <returns>Distinct NS addresses; empty when discovery fails.</returns>
        internal static async Task<IReadOnlyList<IPAddress>> DiscoverAddressesAsync(
            string zoneApex,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(zoneApex);
            LogRecursiveResolverFallbackIfNeeded(logger);

            string apex = zoneApex.TrimEnd('.');

            foreach (IPAddress resolver in RecursiveResolvers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] query = DnsQueryBuilder.Build(apex, DnsRecordType.Ns, out ushort queryId, recursionDesired: true);
                DnsUdpExchange.Result exchange = await DnsUdpExchange
                    .QueryAsync(resolver, query, UdpTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                if (exchange.Buffer is null)
                {
                    continue;
                }

                if (!TryParseNsResponse(exchange.Buffer, queryId, out List<string> nsHostnames, out Dictionary<string, List<IPAddress>> glue))
                {
                    continue;
                }

                if (nsHostnames.Count == 0)
                {
                    continue;
                }

                List<IPAddress> result = [];
                foreach (string ns in nsHostnames)
                {
                    if (glue.TryGetValue(NormalizeDnsName(ns), out List<IPAddress>? ips))
                    {
                        foreach (IPAddress ip in ips)
                        {
                            AddUnique(result, ip);
                        }
                    }
                    else
                    {
                        IReadOnlyList<IPAddress> resolved = await ResolveNsHostnameViaWireThenOsAsync(ns, logger, cancellationToken)
                            .ConfigureAwait(false);
                        foreach (IPAddress ip in resolved)
                        {
                            AddUnique(result, ip);
                        }
                    }
                }

                if (result.Count > 0)
                {
                    return result;
                }
            }

            if (logger is not null)
            {
                DnsLogMessages.ZoneApexNsDiscoveryFailed(logger, zoneApex);
            }

            return [];
        }

        /// <summary>
        /// Reads DNS server addresses from operational NICs, skipping any, loopback, and duplicates.
        /// </summary>
        /// <returns>
        /// Those addresses, or <c>1.1.1.1</c> and <c>8.8.8.8</c> when the list is empty or enumeration throws.
        /// A thrown exception is stored for a one-time fallback log.
        /// </returns>
        private static IPAddress[] ResolveRecursiveResolvers()
        {
            try
            {
                List<IPAddress> servers = [];
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    IPInterfaceProperties props = ni.GetIPProperties();
                    foreach (IPAddress ip in props.DnsAddresses)
                    {
                        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
                            ip.Equals(IPAddress.Loopback) || ip.Equals(IPAddress.IPv6Loopback))
                        {
                            continue;
                        }

                        bool exists = false;
                        for (int i = 0; i < servers.Count; i++)
                        {
                            if (servers[i].Equals(ip))
                            {
                                exists = true;
                                break;
                            }
                        }

                        if (!exists)
                        {
                            servers.Add(ip);
                        }
                    }
                }

                if (servers.Count > 0)
                {
                    return [.. servers];
                }
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                s_recursiveResolverDiscoveryException = ex;
            }

            return
            [
                IPAddress.Parse("1.1.1.1"),
                IPAddress.Parse("8.8.8.8"),
            ];
        }

        /// <summary>
        /// Logs the stored OS-discovery failure once. Later calls and a null <paramref name="logger"/> do nothing.
        /// </summary>
        /// <param name="logger">Caller logger. Null skips the event without consuming the one-time flag.</param>
        private static void LogRecursiveResolverFallbackIfNeeded(ILogger? logger)
        {
            if (logger is null || s_recursiveResolverDiscoveryException is null)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref s_loggedRecursiveResolverFallback, 1, 0) != 0)
            {
                return;
            }

            Exception ex = s_recursiveResolverDiscoveryException;
            if (logger.IsEnabled(LogLevel.Debug))
            {
                string exceptionType = ex.GetType().Name;
                DnsLogMessages.RecursiveResolverDiscoveryFallback(logger, exceptionType, ex);
            }
        }

        /// <summary>Appends <paramref name="ip"/> when <paramref name="list"/> does not already contain an equal address.</summary>
        /// <param name="list">Destination address list.</param>
        /// <param name="ip">Address to add.</param>
        private static void AddUnique(List<IPAddress> list, IPAddress ip)
        {
            foreach (IPAddress existing in list)
            {
                if (existing.Equals(ip))
                {
                    return;
                }
            }

            list.Add(ip);
        }

        /// <summary>Removes one trailing dot and lowercases <paramref name="name"/> with the invariant culture.</summary>
        /// <param name="name">DNS name used as a glue or comparison key.</param>
        /// <returns>The normalized name.</returns>
        private static string NormalizeDnsName(string name)
            => name.TrimEnd('.').ToLowerInvariant();

        /// <summary>
        /// Resolves an NS hostname with recursive A and AAAA queries, then the OS stub resolver if every recursive resolver returns nothing.
        /// </summary>
        /// <param name="host">NS hostname.</param>
        /// <param name="logger">Optional logger for the OS stub failure. Null suppresses that event.</param>
        /// <param name="cancellationToken">Cancels the queries and the OS lookup.</param>
        /// <returns>Addresses from the first recursive resolver that returns any, or the OS result.</returns>
        private static async Task<IReadOnlyList<IPAddress>> ResolveNsHostnameViaWireThenOsAsync(
            string host,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            List<IPAddress> wire = [];
            foreach (IPAddress resolver in RecursiveResolvers)
            {
                byte[] q4 = DnsQueryBuilder.Build(host, DnsRecordType.A, out ushort id4, recursionDesired: true);
                DnsUdpExchange.Result r4 = await DnsUdpExchange
                    .QueryAsync(resolver, q4, UdpTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                if (r4.Buffer is not null)
                {
                    CollectAddressAnswers(r4.Buffer, id4, DnsRecordType.A, 4, wire);
                }

                byte[] q6 = DnsQueryBuilder.Build(host, DnsRecordType.Aaaa, out ushort id6, recursionDesired: true);
                DnsUdpExchange.Result r6 = await DnsUdpExchange
                    .QueryAsync(resolver, q6, UdpTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                if (r6.Buffer is not null)
                {
                    CollectAddressAnswers(r6.Buffer, id6, DnsRecordType.Aaaa, 16, wire);
                }

                if (wire.Count > 0)
                {
                    return wire;
                }
            }

            return await ResolveHostAddressesAsync(host, logger, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Appends answer-section A or AAAA rdata whose type, class, and length match the query.
        /// A malformed header or question section adds nothing.
        /// </summary>
        /// <param name="buffer">DNS response.</param>
        /// <param name="expectedId">Transaction id that must match the response.</param>
        /// <param name="expectedType"><see cref="DnsRecordType.A"/> or <see cref="DnsRecordType.Aaaa"/>.</param>
        /// <param name="expectedRdLength">4 for A and 16 for AAAA.</param>
        /// <param name="dest">Receives matching addresses. Existing entries are kept.</param>
        private static void CollectAddressAnswers(
            byte[] buffer,
            ushort expectedId,
            ushort expectedType,
            int expectedRdLength,
            List<IPAddress> dest)
        {
            ReadOnlySpan<byte> span = buffer;
            if (!DnsResponseHeader.TryReadCounts(
                    span,
                    expectedId,
                    out ushort qdCount,
                    out ushort anCount,
                    out _,
                    out _))
            {
                return;
            }

            int offset = DnsWireFormat.HeaderSize;
            if (!DnsResponseHeader.TrySkipQuestions(span, ref offset, qdCount))
            {
                return;
            }

            for (int i = 0; i < anCount; i++)
            {
                if (!TryConsumeResourceRecord(
                        span,
                        ref offset,
                        out _,
                        out ushort rrType,
                        out ushort rrClass,
                        out int rdataStart,
                        out ushort rdLength))
                {
                    return;
                }

                if (rrClass != DnsRecordType.ClassIn || rrType != expectedType || rdLength != expectedRdLength)
                {
                    continue;
                }

                if (rdataStart + rdLength <= span.Length)
                {
                    dest.Add(new IPAddress(span.Slice(rdataStart, rdLength)));
                }
            }
        }

        /// <summary>
        /// Resolves <paramref name="host"/> with <see cref="System.Net.Dns.GetHostAddressesAsync(string, System.Threading.CancellationToken)"/>
        /// and keeps IPv4 and IPv6 results.
        /// </summary>
        /// <param name="host">NS hostname.</param>
        /// <param name="logger">Optional logger. A non-cancellation failure is logged and becomes an empty list.</param>
        /// <param name="cancellationToken">Cancels the OS lookup. Cancellation propagates.</param>
        /// <returns>IPv4 and IPv6 addresses, or empty when the OS returns none or throws.</returns>
        private static async Task<IReadOnlyList<IPAddress>> ResolveHostAddressesAsync(
            string host,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            try
            {
                IPAddress[] all = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                if (all.Length == 0)
                {
                    return [];
                }

                List<IPAddress> ips = new(all.Length);
                foreach (IPAddress ip in all)
                {
                    if (ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    {
                        ips.Add(ip);
                    }
                }

                return ips;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                if (logger is not null && logger.IsEnabled(LogLevel.Debug))
                {
                    string exceptionType = ex.GetType().Name;
                    DnsLogMessages.NsHostnameOsResolveFailed(logger, host, exceptionType, ex);
                }

                return [];
            }
        }

        /// <summary>
        /// Reads NS names and A/AAAA glue from the answer, authority, and additional sections.
        /// </summary>
        /// <param name="buffer">DNS response to an NS query.</param>
        /// <param name="expectedId">Transaction id that must match.</param>
        /// <param name="nsHostnames">Receives distinct NS hostnames. Replaced on entry.</param>
        /// <param name="glue">Receives additional A/AAAA addresses keyed by normalized owner name. Replaced on entry.</param>
        /// <returns><see langword="false"/> when the header or question section cannot be read. Section parse stops early still return <see langword="true"/>.</returns>
        private static bool TryParseNsResponse(
            byte[] buffer,
            ushort expectedId,
            out List<string> nsHostnames,
            out Dictionary<string, List<IPAddress>> glue)
        {
            nsHostnames = [];
            glue = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);
            ReadOnlySpan<byte> span = buffer;

            if (!DnsResponseHeader.TryReadCounts(
                    span,
                    expectedId,
                    out ushort qdCount,
                    out ushort anCount,
                    out ushort authorityCount,
                    out ushort additionalCount))
            {
                return false;
            }

            int offset = DnsWireFormat.HeaderSize;
            if (!DnsResponseHeader.TrySkipQuestions(span, ref offset, qdCount))
            {
                return false;
            }

            ProcessSection(span, ref offset, anCount, nsHostnames, glue);
            ProcessSection(span, ref offset, authorityCount, nsHostnames, glue);
            ProcessSection(span, ref offset, additionalCount, nsHostnames, glue);
            return true;
        }

        /// <summary>
        /// Scans one DNS section for IN NS names and IN A/AAAA glue. A truncated record stops the section.
        /// </summary>
        /// <param name="span">Full DNS message.</param>
        /// <param name="offset">Start of the section. Advanced past every record that is consumed.</param>
        /// <param name="count">Record count from the header.</param>
        /// <param name="nsHostnames">Receives distinct NS target names.</param>
        /// <param name="glue">Receives A/AAAA addresses keyed by normalized owner name.</param>
        private static void ProcessSection(
            ReadOnlySpan<byte> span,
            ref int offset,
            int count,
            List<string> nsHostnames,
            Dictionary<string, List<IPAddress>> glue)
        {
            for (int i = 0; i < count; i++)
            {
                if (!TryConsumeResourceRecord(
                        span,
                        ref offset,
                        out string owner,
                        out ushort rrType,
                        out ushort rrClass,
                        out int rdataStart,
                        out ushort rdLength))
                {
                    return;
                }

                if (rrClass != DnsRecordType.ClassIn)
                {
                    continue;
                }

                if (rrType == DnsRecordType.Ns)
                {
                    int p = rdataStart;
                    if (!DnsNameCodec.TryReadDomainName(span, ref p, out string nsdname) || p != rdataStart + rdLength)
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(nsdname) && !nsHostnames.Contains(nsdname, StringComparer.OrdinalIgnoreCase))
                    {
                        nsHostnames.Add(nsdname);
                    }
                }
                else if (rrType == DnsRecordType.A && rdLength == 4)
                {
                    AddGlue(glue, owner, new IPAddress(span.Slice(rdataStart, rdLength)));
                }
                else if (rrType == DnsRecordType.Aaaa && rdLength == 16)
                {
                    AddGlue(glue, owner, new IPAddress(span.Slice(rdataStart, rdLength)));
                }
            }
        }

        /// <summary>Adds <paramref name="ip"/> under the normalized <paramref name="owner"/> when that address is not already present.</summary>
        /// <param name="glue">Glue map keyed by normalized owner name.</param>
        /// <param name="owner">Record owner name.</param>
        /// <param name="ip">A or AAAA rdata.</param>
        private static void AddGlue(Dictionary<string, List<IPAddress>> glue, string owner, IPAddress ip)
        {
            string key = NormalizeDnsName(owner);
            if (!glue.TryGetValue(key, out List<IPAddress>? list))
            {
                list = [];
                glue[key] = list;
            }

            foreach (IPAddress existing in list)
            {
                if (existing.Equals(ip))
                {
                    return;
                }
            }

            list.Add(ip);
        }

        /// <summary>
        /// Reads one resource record's owner, type, class, and rdata bounds, then advances <paramref name="offset"/> past the rdata.
        /// TTL is skipped. Compression in the owner name is followed by <see cref="DnsNameCodec.TryReadDomainName"/>.
        /// </summary>
        /// <param name="packet">Full DNS message.</param>
        /// <param name="offset">Start of the record. On success, the first byte after rdata.</param>
        /// <param name="ownerName">Uncompressed owner name.</param>
        /// <param name="rrType">TYPE field.</param>
        /// <param name="rrClass">CLASS field.</param>
        /// <param name="rdataStart">Offset of RDATA.</param>
        /// <param name="rdLength">RDLENGTH.</param>
        /// <returns><see langword="false"/> when the name or fixed fields do not fit. Out parameters other than the name are then zero.</returns>
        private static bool TryConsumeResourceRecord(
            ReadOnlySpan<byte> packet,
            ref int offset,
            out string ownerName,
            out ushort rrType,
            out ushort rrClass,
            out int rdataStart,
            out ushort rdLength)
        {
            rrType = 0;
            rrClass = 0;
            rdataStart = 0;
            rdLength = 0;

            if (!DnsNameCodec.TryReadDomainName(packet, ref offset, out ownerName))
            {
                return false;
            }

            if (offset + DnsWireFormat.ResourceRecordFixedFieldsSize > packet.Length)
            {
                return false;
            }

            rrType = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            rrClass = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            rdLength = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
            offset += DnsWireFormat.ResourceRecordFixedFieldsSize;
            rdataStart = offset;
            if (offset + rdLength > packet.Length)
            {
                return false;
            }

            offset += rdLength;
            return true;
        }
    }
}
