using System.Net;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.PostFilter.Quota;

namespace VectorNNTPD.TransitService.PostFilter
{
    /// <summary>Compiles <see cref="PostFilterOptions"/> into an immutable snapshot.</summary>
    internal static class PostFilterPolicyCompiler
    {
        /// <summary>Builds a snapshot or throws when the options are not usable.</summary>
        public static PostFilterPolicySnapshot Compile(PostFilterOptions options, long revision = 0)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(options.Quota);
            ArgumentNullException.ThrowIfNull(options.SpamAssassin);
            ArgumentOutOfRangeException.ThrowIfNegative(revision);
            if (!Enum.IsDefined(options.Gate))
            {
                throw new InvalidOperationException($"PostFilter Gate is not a defined value: '{options.Gate}'.");
            }

            if (options.Quota.LongWindow <= TimeSpan.Zero || options.Quota.ShortWindow <= TimeSpan.Zero)
            {
                throw new InvalidOperationException("PostFilter quota windows must be positive.");
            }

            var windows = new PostFilterQuotaWindows(
                (long)options.Quota.LongWindow.TotalMilliseconds,
                (long)options.Quota.ShortWindow.TotalMilliseconds);
            windows.Validate();

            var sa = options.SpamAssassin;
            if (sa.Enabled)
            {
                if (sa.OnFailure is null)
                {
                    throw new InvalidOperationException(
                        "PostFilter SpamAssassin OnFailure is required when SpamAssassin is enabled.");
                }

                if (sa.Hosts.Length == 0)
                {
                    throw new InvalidOperationException(
                        "PostFilter SpamAssassin Hosts is required when SpamAssassin is enabled.");
                }

                var uniqueHosts = new HashSet<string>(StringComparer.Ordinal);
                foreach (var host in sa.Hosts)
                {
                    if (string.IsNullOrWhiteSpace(host))
                    {
                        throw new InvalidOperationException(
                            "PostFilter SpamAssassin Hosts contains a malformed empty entry.");
                    }

                    if (!uniqueHosts.Add(host.Trim()))
                    {
                        throw new InvalidOperationException(
                            $"PostFilter SpamAssassin Hosts contains a duplicate entry: '{host.Trim()}'.");
                    }
                }

                if (sa.Port is < 1 or > 65535)
                {
                    throw new InvalidOperationException("PostFilter SpamAssassin Port must be 1–65535.");
                }

                if (sa.OperationTimeout < PostFilterSpamAssassinOptions.MinOperationTimeout
                    || sa.OperationTimeout > PostFilterSpamAssassinOptions.MaxOperationTimeout)
                {
                    throw new InvalidOperationException(
                        "PostFilter SpamAssassin OperationTimeout must be between 1 second and 2 minutes when SpamAssassin is enabled.");
                }

                if (sa.ConnectTimeout <= TimeSpan.Zero || sa.ConnectTimeout > sa.OperationTimeout)
                {
                    throw new InvalidOperationException(
                        "PostFilter SpamAssassin ConnectTimeout must be positive and must not exceed OperationTimeout.");
                }

                if (string.IsNullOrWhiteSpace(sa.ProtocolVersion))
                {
                    throw new InvalidOperationException(
                        "PostFilter SpamAssassin ProtocolVersion is required when SpamAssassin is enabled.");
                }

                if (sa.MaxConnections < PostFilterSpamAssassinOptions.MinMaxConnections
                    || sa.MaxConnections > PostFilterSpamAssassinOptions.MaxMaxConnections)
                {
                    throw new InvalidOperationException(
                        "PostFilter SpamAssassin MaxConnections must be between 1 and 32 when SpamAssassin is enabled.");
                }

                if (!Enum.IsDefined(sa.HostSelection))
                {
                    throw new InvalidOperationException(
                        "PostFilter SpamAssassin HostSelection is not a defined strategy.");
                }
            }

            if (sa.MaxArticleSize < 0)
            {
                throw new InvalidOperationException(
                    "PostFilter SpamAssassin MaxArticleSize must be zero or positive.");
            }

            var ceilings = new PostFilterQuotaCeilings(
                options.Quota.MaxMessagesLong,
                options.Quota.MaxBytesLong,
                options.Quota.MaxIdenticalLong,
                options.Quota.MaxMessagesShort,
                options.Quota.MaxBytesShort,
                options.Quota.MaxIdenticalShort);
            ceilings.Validate();

            return new PostFilterPolicySnapshot(
                options.Gate,
                ToSet(options.DeniedAccounts, "DeniedAccounts"),
                ToNetworks(options.DeniedCidrs, "DeniedCidrs"),
                ToSet(options.AllowlistedAccounts, "AllowlistedAccounts"),
                ToNetworks(options.AllowlistedCidrs, "AllowlistedCidrs"),
                ParseArtTypes(options.RejectArtTypes, "RejectArtTypes"),
                windows,
                ceilings,
                sa.Enabled,
                sa.OnFailure,
                sa.MaxArticleSize,
                ParseArtTypes(sa.ExcludeArtTypes, "SpamAssassin:ExcludeArtTypes"),
                sa.Enabled ? sa.Hosts.Select(static host => host.Trim()).ToArray() : [],
                sa.Port,
                string.IsNullOrWhiteSpace(sa.ProtocolVersion)
                    ? PostFilterSpamAssassinOptions.DefaultProtocolVersion
                    : sa.ProtocolVersion.Trim(),
                sa.MaxConnections,
                sa.HostSelection,
                sa.ConnectTimeout,
                sa.OperationTimeout,
                PostFilterQuotaDefaults.HoldMilliseconds(sa.Enabled, sa.OperationTimeout),
                revision);
        }

        private static HashSet<string> ToSet(string[] values, string name)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException($"PostFilter {name} contains a malformed empty entry.");
                }

                var trimmed = value.Trim();
                var identifier = PostFilterAccountIdentity.FromPolicyEntry(trimmed);
                if (!set.Add(identifier))
                {
                    throw new InvalidOperationException($"PostFilter {name} contains a duplicate entry: '{trimmed}'.");
                }
            }

            return set;
        }

        private static List<IPNetwork> ToNetworks(string[] values, string name)
        {
            var networks = new List<IPNetwork>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException($"PostFilter {name} contains a malformed empty entry.");
                }

                var trimmed = value.Trim();
                if (!seen.Add(trimmed))
                {
                    throw new InvalidOperationException($"PostFilter {name} contains a duplicate entry: '{trimmed}'.");
                }

                if (!IPNetwork.TryParse(trimmed, out var network))
                {
                    throw new InvalidOperationException($"PostFilter {name} contains an invalid CIDR: '{value}'.");
                }

                networks.Add(network);
            }

            return networks;
        }

        private static ArticleType ParseArtTypes(string[] values, string name)
        {
            var mask = ArticleType.None;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException($"PostFilter {name} contains a malformed empty entry.");
                }

                var trimmed = value.Trim();
                if (!seen.Add(trimmed))
                {
                    throw new InvalidOperationException($"PostFilter {name} contains a duplicate entry: '{trimmed}'.");
                }

                if (!Enum.TryParse<ArticleType>(trimmed, ignoreCase: true, out var flag)
                    || !Enum.IsDefined(flag)
                    || flag == ArticleType.None)
                {
                    throw new InvalidOperationException($"PostFilter {name} contains an unknown ArtType: '{value}'.");
                }

                mask |= flag;
            }

            return mask;
        }
    }
}
