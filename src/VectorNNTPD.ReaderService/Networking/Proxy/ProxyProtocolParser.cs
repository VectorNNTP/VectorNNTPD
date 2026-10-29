using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VectorNNTPD.ReaderService.Networking.Proxy
{
    /// <summary>Incremental parse status for a PROXY v1/v2 preamble buffer.</summary>
    internal enum ProxyParseStatus
    {
        NeedMore,
        Ok,
        Error,
    }

    /// <summary>Successful PROXY preamble parse result (before leftover slicing).</summary>
    internal readonly struct ProxyParseResult
    {
        public ProxyParseResult(
            ConnectionClientIdentity identity,
            int headerLength,
            int version,
            bool localCommand)
        {
            Identity = identity;
            HeaderLength = headerLength;
            Version = version;
            LocalCommand = localCommand;
        }

        public ConnectionClientIdentity Identity { get; }

        public int HeaderLength { get; }

        public int Version { get; }

        public bool LocalCommand { get; }
    }

    /// <summary>
    /// Byte-oriented PROXY protocol v1/v2 parser (HAProxy PROXY protocol specification).
    /// </summary>
    /// <remarks>
    /// Normative: <c>docs/standards/haproxy/proxy-protocol.txt</c>. Does not perform trust checks;
    /// callers must only invoke this for TCP peers already classified as trusted proxies.
    /// </remarks>
    internal static class ProxyProtocolParser
    {
        /// <summary>Spec §2.1: worst-case v1 line including CRLF.</summary>
        public const int MaxV1LineOctets = 107;

        /// <summary>Spec §2.2: address length is uint16 — full protocol maximum.</summary>
        public const int MaxV2AddressOctets = 65_535;

        /// <summary>Maximum gather buffer for a v2 header (signature + length).</summary>
        public const int MaxV2HeaderOctets = 16 + MaxV2AddressOctets;

        /// <summary>Default preamble timeout (spec recommends at least 3 seconds).</summary>
        public static readonly TimeSpan DefaultPreambleTimeout = TimeSpan.FromSeconds(5);

        private static readonly byte[] V2Signature =
        [
            0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A,
        ];

        private static readonly byte[] V1Prefix = "PROXY "u8.ToArray();

        private const byte Pp2TypeCrc32C = 0x03;

        /// <summary>
        /// Attempts to parse <paramref name="buffer"/> as a complete PROXY preamble.
        /// </summary>
        public static ProxyParseStatus TryParse(
            ReadOnlySpan<byte> buffer,
            IPEndPoint tcpPeer,
            out ProxyParseResult result,
            out string error)
        {
            result = default;
            error = string.Empty;

            if (buffer.IsEmpty)
            {
                return ProxyParseStatus.NeedMore;
            }

            if (buffer[0] == 0x0D)
            {
                return TryParseV2(buffer, tcpPeer, out result, out error);
            }

            if (buffer[0] == (byte)'P')
            {
                return TryParseV1(buffer, tcpPeer, out result, out error);
            }

            error = "PROXY header required";
            return ProxyParseStatus.Error;
        }

        private static ProxyParseStatus TryParseV1(
            ReadOnlySpan<byte> buffer,
            IPEndPoint tcpPeer,
            out ProxyParseResult result,
            out string error)
        {
            result = default;
            error = string.Empty;

            if (buffer.Length < V1Prefix.Length)
            {
                if (V1Prefix.AsSpan().StartsWith(buffer))
                {
                    return ProxyParseStatus.NeedMore;
                }

                error = "PROXY v1 invalid signature";
                return ProxyParseStatus.Error;
            }

            if (!buffer.StartsWith(V1Prefix))
            {
                error = "PROXY v1 invalid signature";
                return ProxyParseStatus.Error;
            }

            var crlf = buffer.IndexOf("\r\n"u8);
            if (crlf < 0)
            {
                if (buffer.IndexOf((byte)'\n') >= 0)
                {
                    error = "PROXY v1 requires CRLF terminator";
                    return ProxyParseStatus.Error;
                }

                var cr = buffer.IndexOf((byte)'\r');
                if (cr >= 0 && cr + 1 < buffer.Length)
                {
                    error = "PROXY v1 requires CRLF terminator";
                    return ProxyParseStatus.Error;
                }

                if (buffer.Length >= MaxV1LineOctets)
                {
                    error = "PROXY v1 missing CRLF within 107 octets";
                    return ProxyParseStatus.Error;
                }

                return ProxyParseStatus.NeedMore;
            }

            if (crlf + 2 > MaxV1LineOctets)
            {
                error = "PROXY v1 line exceeds 107 octets";
                return ProxyParseStatus.Error;
            }

            var line = buffer[..crlf];
            var headerLength = crlf + 2;
            var parts = SplitAsciiSpaces(line);
            if (parts.Count < 2 || !parts[0].Span.SequenceEqual("PROXY"u8) || parts[1].IsEmpty)
            {
                error = "PROXY v1 malformed signature";
                return ProxyParseStatus.Error;
            }

            var family = parts[1].Span;
            if (family.SequenceEqual("UNKNOWN"u8))
            {
                // Spec §2.1: ignore remainder before CRLF; use real connection endpoints.
                result = new ProxyParseResult(
                    ConnectionClientIdentity.FromTrustedProxy(tcpPeer, tcpPeer, proxyProtocolVersion: 1),
                    headerLength,
                    version: 1,
                    localCommand: false);
                return ProxyParseStatus.Ok;
            }

            foreach (var part in parts)
            {
                if (part.IsEmpty)
                {
                    error = "PROXY v1 malformed spacing";
                    return ProxyParseStatus.Error;
                }
            }

            if (!family.SequenceEqual("TCP4"u8) && !family.SequenceEqual("TCP6"u8))
            {
                error = "PROXY v1 unsupported family";
                return ProxyParseStatus.Error;
            }

            if (parts.Count != 6)
            {
                error = "PROXY v1 expected 6 fields";
                return ProxyParseStatus.Error;
            }

            var isV4 = family.SequenceEqual("TCP4"u8);
            if (!TryParseV1Address(parts[2].Span, isV4, out var src) || !TryParseV1Address(parts[3].Span, isV4, out _))
            {
                error = "PROXY v1 invalid address";
                return ProxyParseStatus.Error;
            }

            if (!TryParseV1Port(parts[4].Span, out var sport) || !TryParseV1Port(parts[5].Span, out _))
            {
                error = "PROXY v1 invalid port";
                return ProxyParseStatus.Error;
            }

            var client = new IPEndPoint(src, sport);
            result = new ProxyParseResult(
                ConnectionClientIdentity.FromTrustedProxy(tcpPeer, client, proxyProtocolVersion: 1),
                headerLength,
                version: 1,
                localCommand: false);
            return ProxyParseStatus.Ok;
        }

        private static ProxyParseStatus TryParseV2(
            ReadOnlySpan<byte> buffer,
            IPEndPoint tcpPeer,
            out ProxyParseResult result,
            out string error)
        {
            result = default;
            error = string.Empty;

            if (buffer.Length < V2Signature.Length)
            {
                if (V2Signature.AsSpan().StartsWith(buffer))
                {
                    return ProxyParseStatus.NeedMore;
                }

                error = "PROXY v2 invalid signature";
                return ProxyParseStatus.Error;
            }

            if (!buffer.StartsWith(V2Signature))
            {
                error = "PROXY v2 invalid signature";
                return ProxyParseStatus.Error;
            }

            if (buffer.Length < 16)
            {
                return ProxyParseStatus.NeedMore;
            }

            var verCmd = buffer[12];
            var version = verCmd >> 4;
            var command = verCmd & 0x0F;
            if (version != 0x2)
            {
                error = "PROXY v2 invalid version";
                return ProxyParseStatus.Error;
            }

            var famProto = buffer[13];
            var family = famProto >> 4;
            var protocol = famProto & 0x0F;
            var addrLen = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(14, 2));
            var total = 16 + addrLen;
            if (buffer.Length < total)
            {
                return ProxyParseStatus.NeedMore;
            }

            var fullHeader = buffer[..total];
            var payload = buffer.Slice(16, addrLen);

            if (command == 0x0)
            {
                if (family is >= 0 and <= 3 && protocol is >= 0 and <= 2)
                {
                    var localMin = AddressBlockMinimum(famProto);
                    var tlvOff = 16 + (payload.Length >= localMin ? localMin : payload.Length);
                    if (!TryValidateV2Tlvs(fullHeader, tlvOff, out error))
                    {
                        return ProxyParseStatus.Error;
                    }
                }

                result = new ProxyParseResult(
                    ConnectionClientIdentity.FromTrustedProxy(tcpPeer, tcpPeer, proxyProtocolVersion: 2),
                    total,
                    version: 2,
                    localCommand: true);
                return ProxyParseStatus.Ok;
            }

            if (command != 0x1)
            {
                error = "PROXY v2 unsupported command";
                return ProxyParseStatus.Error;
            }

            if (family is < 0 or > 3 || protocol is < 0 or > 2)
            {
                error = "PROXY v2 unsupported address family/protocol";
                return ProxyParseStatus.Error;
            }

            var listedMin = AddressBlockMinimumOrNull(famProto);
            if (listedMin is null)
            {
                // Valid nibbles but unlisted combination — UNSPEC fallback.
                result = new ProxyParseResult(
                    ConnectionClientIdentity.FromTrustedProxy(tcpPeer, tcpPeer, proxyProtocolVersion: 2),
                    total,
                    version: 2,
                    localCommand: false);
                return ProxyParseStatus.Ok;
            }

            var tlvOffset = 16 + (payload.Length >= listedMin.Value ? listedMin.Value : payload.Length);
            if (!TryValidateV2Tlvs(fullHeader, tlvOffset, out error))
            {
                return ProxyParseStatus.Error;
            }

            if (famProto == 0x00)
            {
                result = new ProxyParseResult(
                    ConnectionClientIdentity.FromTrustedProxy(tcpPeer, tcpPeer, proxyProtocolVersion: 2),
                    total,
                    version: 2,
                    localCommand: false);
                return ProxyParseStatus.Ok;
            }

            if (famProto == 0x11)
            {
                if (payload.Length < 12)
                {
                    error = "PROXY v2 address block truncated";
                    return ProxyParseStatus.Error;
                }

                var src = new IPAddress(payload[..4]);
                var sport = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(8, 2));
                var client = new IPEndPoint(src, sport);
                result = new ProxyParseResult(
                    ConnectionClientIdentity.FromTrustedProxy(tcpPeer, client, proxyProtocolVersion: 2),
                    total,
                    version: 2,
                    localCommand: false);
                return ProxyParseStatus.Ok;
            }

            if (famProto == 0x21)
            {
                if (payload.Length < 36)
                {
                    error = "PROXY v2 address block truncated";
                    return ProxyParseStatus.Error;
                }

                var src = new IPAddress(payload[..16]);
                var sport = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(32, 2));
                var client = new IPEndPoint(src, sport);
                result = new ProxyParseResult(
                    ConnectionClientIdentity.FromTrustedProxy(tcpPeer, client, proxyProtocolVersion: 2),
                    total,
                    version: 2,
                    localCommand: false);
                return ProxyParseStatus.Ok;
            }

            // UDP/UNIX listed combinations: fall back to real endpoints (spec §2.2).
            result = new ProxyParseResult(
                ConnectionClientIdentity.FromTrustedProxy(tcpPeer, tcpPeer, proxyProtocolVersion: 2),
                total,
                version: 2,
                localCommand: false);
            return ProxyParseStatus.Ok;
        }

        private static int AddressBlockMinimum(int famProto) => AddressBlockMinimumOrNull(famProto) ?? 0;

        private static int? AddressBlockMinimumOrNull(int famProto) => famProto switch
        {
            0x00 => 0,
            0x11 => 12,
            0x12 => 12,
            0x21 => 36,
            0x22 => 36,
            0x31 => 216,
            0x32 => 216,
            _ => null,
        };

        private static bool TryValidateV2Tlvs(ReadOnlySpan<byte> fullHeader, int tlvOffset, out string error)
        {
            error = string.Empty;
            var end = fullHeader.Length;
            var pos = tlvOffset;
            uint? crcValue = null;
            int? crcValueOffset = null;

            while (pos < end)
            {
                if (end - pos < 3)
                {
                    error = "PROXY v2 truncated TLV header";
                    return false;
                }

                var tlvType = fullHeader[pos];
                var tlvLen = (fullHeader[pos + 1] << 8) | fullHeader[pos + 2];
                var valueOff = pos + 3;
                var valueEnd = valueOff + tlvLen;
                if (valueEnd > end)
                {
                    error = "PROXY v2 TLV length exceeds header";
                    return false;
                }

                if (tlvType == Pp2TypeCrc32C)
                {
                    if (tlvLen != 4)
                    {
                        error = "PROXY v2 CRC32C TLV must be 4 octets";
                        return false;
                    }

                    if (crcValue is not null)
                    {
                        error = "PROXY v2 duplicate CRC32C TLV";
                        return false;
                    }

                    crcValue = BinaryPrimitives.ReadUInt32BigEndian(fullHeader.Slice(valueOff, 4));
                    crcValueOffset = valueOff;
                }

                pos = valueEnd;
            }

            if (crcValue is not null && crcValueOffset is not null)
            {
                var mutable = fullHeader.ToArray();
                mutable.AsSpan(crcValueOffset.Value, 4).Clear();
                var calculated = Crc32C.Compute(mutable);
                if (calculated != crcValue.Value)
                {
                    error = "PROXY v2 CRC32C mismatch";
                    return false;
                }
            }

            return true;
        }

        private static bool TryParseV1Address(ReadOnlySpan<byte> token, bool ipv4, out IPAddress address)
        {
            address = IPAddress.None;
            if (ipv4)
            {
                return TryParseV1Ipv4(token, out address);
            }

            return TryParseV1Ipv6(token, out address);
        }

        private static bool TryParseV1Ipv4(ReadOnlySpan<byte> token, out IPAddress address)
        {
            address = IPAddress.None;
            Span<byte> octets = stackalloc byte[4];
            var octetIndex = 0;
            var start = 0;
            for (var i = 0; i <= token.Length; i++)
            {
                if (i == token.Length || token[i] == (byte)'.')
                {
                    if (octetIndex >= 4)
                    {
                        return false;
                    }

                    if (!TryParseDecimalNoLeadingZero(token[start..i], out var value) || value > 255)
                    {
                        return false;
                    }

                    octets[octetIndex++] = (byte)value;
                    start = i + 1;
                }
            }

            if (octetIndex != 4)
            {
                return false;
            }

            address = new IPAddress(octets);
            return true;
        }

        private static bool TryParseV1Ipv6(ReadOnlySpan<byte> token, out IPAddress address)
        {
            address = IPAddress.None;
            if (!Ascii.IsValid(token))
            {
                return false;
            }

            var text = Encoding.ASCII.GetString(token);
            if (text.Contains('.') || text.Split("::").Length > 2)
            {
                return false;
            }

            string[] groups;
            if (text.Contains("::", StringComparison.Ordinal))
            {
                var parts = text.Split("::", 2);
                var left = parts[0].Length == 0 ? [] : parts[0].Split(':', StringSplitOptions.RemoveEmptyEntries);
                var right = parts[1].Length == 0 ? [] : parts[1].Split(':', StringSplitOptions.RemoveEmptyEntries);
                groups = [.. left, .. right];
            }
            else
            {
                groups = text.Split(':');
                if (groups.Length != 8)
                {
                    return false;
                }
            }

            foreach (var group in groups)
            {
                if (group.Length is < 1 or > 4)
                {
                    return false;
                }

                foreach (var ch in group)
                {
                    if (!Uri.IsHexDigit(ch))
                    {
                        return false;
                    }
                }
            }

            return IPAddress.TryParse(text, out address!)
                   && address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        private static bool TryParseV1Port(ReadOnlySpan<byte> token, out int port)
        {
            port = 0;
            if (!TryParseDecimalNoLeadingZero(token, out var value) || value > 65535)
            {
                return false;
            }

            port = value;
            return true;
        }

        private static bool TryParseDecimalNoLeadingZero(ReadOnlySpan<byte> token, out int value)
        {
            value = 0;
            if (token.IsEmpty)
            {
                return false;
            }

            if (token.Length > 1 && token[0] == (byte)'0')
            {
                return false;
            }

            foreach (var b in token)
            {
                if (b is < (byte)'0' or > (byte)'9')
                {
                    return false;
                }

                value = (value * 10) + (b - '0');
            }

            return true;
        }

        private static List<ReadOnlyMemory<byte>> SplitAsciiSpaces(ReadOnlySpan<byte> line)
        {
            // Materialize into owned segments for field indexing (v1 lines are ≤107 octets).
            var owned = line.ToArray();
            var parts = new List<ReadOnlyMemory<byte>>(6);
            var start = 0;
            for (var i = 0; i <= owned.Length; i++)
            {
                if (i == owned.Length || owned[i] == (byte)' ')
                {
                    parts.Add(owned.AsMemory(start, i - start));
                    start = i + 1;
                }
            }

            return parts;
        }
    }
}
