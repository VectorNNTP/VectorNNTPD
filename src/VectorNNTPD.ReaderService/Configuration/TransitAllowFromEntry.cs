using System.Net;

namespace VectorNNTPD.ReaderService.Configuration
{
    /// <summary>
    /// A parsed Transit <c>AllowFrom</c> entry: literal prefix or DNS hostname.
    /// </summary>
    public abstract class TransitAllowFromEntry
    {
        private TransitAllowFromEntry(string original)
        {
            Original = original;
        }

        /// <summary>Gets the original configuration text.</summary>
        public string Original { get; }

        /// <summary>Literal IPv4/IPv6 address or CIDR prefix.</summary>
        public sealed class Literal : TransitAllowFromEntry
        {
            /// <summary>Initializes a literal prefix entry.</summary>
            public Literal(string original, IpPrefix prefix)
                : base(original)
            {
                Prefix = prefix;
            }

            /// <summary>Gets the parsed prefix.</summary>
            public IpPrefix Prefix { get; }
        }

        /// <summary>DNS hostname that must be resolved to A/AAAA addresses.</summary>
        public sealed class Hostname : TransitAllowFromEntry
        {
            /// <summary>Initializes a hostname entry.</summary>
            public Hostname(string original, string hostname)
                : base(original)
            {
                DnsName = hostname;
            }

            /// <summary>Gets the normalized hostname (no trailing dot).</summary>
            public string DnsName { get; }
        }

        /// <summary>Parses one AllowFrom entry.</summary>
        public static bool TryParse(string text, out TransitAllowFromEntry? entry, out string? error)
        {
            entry = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "AllowFrom entry must not be empty.";
                return false;
            }

            var trimmed = text.Trim();
            if (trimmed.Contains('*', StringComparison.Ordinal) || trimmed.Contains('+', StringComparison.Ordinal))
            {
                error = "AllowFrom does not support wildcards; use a hostname, IP address, or CIDR prefix.";
                return false;
            }

            if (IpPrefix.TryParse(trimmed, out var prefix))
            {
                entry = new Literal(trimmed, prefix);
                return true;
            }

            if (trimmed.Contains('/', StringComparison.Ordinal))
            {
                error = "AllowFrom CIDR prefix is not a valid IPv4 or IPv6 network.";
                return false;
            }

            if (IPAddress.TryParse(trimmed, out _))
            {
                error = "AllowFrom IP address is not valid.";
                return false;
            }

            var hostname = trimmed.TrimEnd('.');
            if (!NntpdOptionsValidator.IsValidDnsSuffix(hostname))
            {
                error = "AllowFrom entry is not a valid DNS hostname, IP address, or CIDR prefix.";
                return false;
            }

            entry = new Hostname(trimmed, hostname);
            return true;
        }
    }
}
