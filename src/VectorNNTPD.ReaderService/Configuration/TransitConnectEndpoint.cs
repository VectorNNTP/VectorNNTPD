using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace VectorNNTPD.ReaderService.Configuration
{
    /// <summary>
    /// A parsed Transit <c>ConnectTo</c> endpoint (host plus required explicit port).
    /// </summary>
    /// <remarks>
    /// Supports DNS names, IPv4, and bracketed IPv6 with a port. This type does not
    /// establish outbound connections.
    /// </remarks>
    public sealed class TransitConnectEndpoint
    {
        private TransitConnectEndpoint(string host, int port, bool isIpAddress)
        {
            Host = host;
            Port = port;
            IsIpAddress = isIpAddress;
        }

        /// <summary>Gets the host name or IP address (unbracketed).</summary>
        public string Host { get; }

        /// <summary>Gets the explicit TCP port.</summary>
        public int Port { get; }

        /// <summary>Gets a value indicating whether <see cref="Host"/> is a literal IP address.</summary>
        public bool IsIpAddress { get; }

        /// <summary>
        /// Parses <c>host:port</c> or <c>[IPv6]:port</c>. An explicit port is required.
        /// </summary>
        public static bool TryParse(string text, out TransitConnectEndpoint? endpoint, out string? error)
        {
            endpoint = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "ConnectTo entry must not be empty.";
                return false;
            }

            var trimmed = text.Trim();
            string host;
            string portText;
            if (trimmed.StartsWith('['))
            {
                var close = trimmed.IndexOf(']');
                if (close <= 1)
                {
                    error = "ConnectTo IPv6 endpoint must be written as [address]:port.";
                    return false;
                }

                if (close == trimmed.Length - 1 || trimmed[close + 1] != ':')
                {
                    error = "ConnectTo IPv6 endpoint must include an explicit port after the closing bracket.";
                    return false;
                }

                host = trimmed[1..close];
                portText = trimmed[(close + 2)..];
            }
            else
            {
                var colon = trimmed.LastIndexOf(':');
                if (colon <= 0 || colon == trimmed.Length - 1)
                {
                    error = "ConnectTo endpoint must include an explicit port (host:port or [IPv6]:port).";
                    return false;
                }

                host = trimmed[..colon];
                portText = trimmed[(colon + 1)..];

                // Unbracketed IPv6 with a port is ambiguous (multiple colons). Require brackets.
                if (host.Contains(':', StringComparison.Ordinal))
                {
                    error = "ConnectTo IPv6 endpoint must be written as [address]:port.";
                    return false;
                }
            }

            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || port is < 1 or > 65535)
            {
                error = "ConnectTo port must be an integer in the range 1–65535.";
                return false;
            }

            if (IPAddress.TryParse(host, out var address))
            {
                if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                {
                    error = "ConnectTo must not use an any-address wildcard.";
                    return false;
                }

                var canonical = address.AddressFamily == AddressFamily.InterNetworkV6
                    ? address.ToString()
                    : address.ToString();
                endpoint = new TransitConnectEndpoint(canonical, port, isIpAddress: true);
                return true;
            }

            if (!NntpdOptionsValidator.IsValidDnsSuffix(host))
            {
                error = "ConnectTo host is not a valid DNS hostname or IP address.";
                return false;
            }

            endpoint = new TransitConnectEndpoint(host.Trim().TrimEnd('.'), port, isIpAddress: false);
            return true;
        }

        /// <inheritdoc />
        public override string ToString() =>
            Host.Contains(':', StringComparison.Ordinal)
                ? string.Create(CultureInfo.InvariantCulture, $"[{Host}]:{Port}")
                : string.Create(CultureInfo.InvariantCulture, $"{Host}:{Port}");
    }
}
