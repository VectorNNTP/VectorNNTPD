using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.BackFillerService.Configuration
{
    /// <summary>
    /// DNS-label and suffix validators used after the canonical FQDN is generated.
    /// </summary>
    /// <remarks>
    /// FQDN construction lives in <see cref="ApplicationFqdn"/>. BackFiller supplies
    /// the fixed prefix <see cref="BackFillerOptions.ApplicationPrefix"/>.
    /// </remarks>
    internal static class BackFillerIdentity
    {
        /// <summary>Minimum accepted <see cref="BackFillerOptions.ServerId"/> (shared <see cref="ServerIdRules"/>).</summary>
        internal const int MinimumServerId = ServerIdRules.MinimumInclusive;

        /// <summary>Maximum accepted <see cref="BackFillerOptions.ServerId"/> (shared <see cref="ServerIdRules"/>).</summary>
        internal const int MaximumServerId = ServerIdRules.MaximumInclusive;

        /// <summary>
        /// Returns whether <paramref name="label"/> is a valid DNS label.
        /// </summary>
        /// <param name="label">Candidate label.</param>
        /// <returns><see langword="true"/> when the label is valid.</returns>
        /// <remarks>
        /// Length must be 1–63. Only <c>a-z</c>, <c>0-9</c>, and <c>-</c> are accepted.
        /// A leading or trailing hyphen is rejected. Uppercase letters are rejected;
        /// callers that need case-folding must canonicalize first.
        /// </remarks>
        internal static bool IsValidDnsLabel(string label)
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
    }
}
