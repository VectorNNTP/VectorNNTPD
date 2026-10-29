using Microsoft.Extensions.Configuration;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.ReaderService.Configuration
{
    /// <summary>
    /// Applies root-level ACME directory and renewal values without replacing
    /// NNTPD-owned bind addresses, ports, or ACME state directory. Account email,
    /// certificate password, and the Cloudflare API key are published from
    /// <c>nntpsharedconfig</c> and are not read from <c>VECTOR__</c> variables.
    /// </summary>
    internal static class NntpdAcmeCloudflareOptionsOverlay
    {
        /// <summary>
        /// Overlays shared root directory and renewal settings, then restores
        /// <see cref="AcmeCloudflareOptions.AcmeStateDir"/> bound from <c>Nntpd</c>.
        /// Bind addresses and ports remain the values bound from <c>Nntpd</c>.
        /// </summary>
        /// <param name="options">The NNTPD options already bound from the <c>Nntpd</c> section.</param>
        /// <param name="configuration">The full configuration root.</param>
        public static void OverlaySharedFromRootPreservingApplicationAcme(
            NntpdOptions options,
            IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(configuration);

            var stateDir = options.AcmeStateDir;

            AcmeCloudflareOptions.OverlaySharedFromRoot(options, configuration, includeSharedSecrets: false);

            options.AcmeStateDir = stateDir;
        }
    }
}
