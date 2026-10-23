using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.StorageService.Configuration
{

    /// <summary>
    /// DNS-label and suffix validators used after the canonical FQDN is generated.
    /// </summary>
    public static class StorageServerIdentity
    {
        /// <summary>Minimum accepted <see cref="StorageServerOptions.ServerId"/>.</summary>
        public const int MinimumServerId = ServerIdRules.MinimumInclusive;

        /// <summary>Maximum accepted <see cref="StorageServerOptions.ServerId"/>.</summary>
        public const int MaximumServerId = ServerIdRules.MaximumInclusive;

        /// <summary>
        /// Returns whether <paramref name="label"/> is a valid DNS label.
        /// </summary>
        public static bool IsValidDnsLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label) || label.Length > 63)
            {
                return false;
            }

            if (label[0] is '-' || label[^1] is '-')
            {
                return false;
            }

            foreach (var ch in label)
            {
                if (ch is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns whether <paramref name="suffix"/> is a syntactically valid DNS suffix.
        /// </summary>
        public static bool IsValidDnsSuffix(string suffix)
        {
            if (string.IsNullOrWhiteSpace(suffix)
                || suffix.Contains(' ', StringComparison.Ordinal)
                || suffix.Contains("://", StringComparison.Ordinal)
                || suffix.Length > ApplicationFqdn.MaximumLength)
            {
                return false;
            }

            var labels = suffix.Split('.', StringSplitOptions.None);
            if (labels.Length < 2)
            {
                return false;
            }

            foreach (var label in labels)
            {
                if (!IsValidDnsLabel(label))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
