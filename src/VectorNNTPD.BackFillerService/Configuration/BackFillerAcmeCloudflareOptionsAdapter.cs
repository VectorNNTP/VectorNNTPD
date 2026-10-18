using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.BackFillerService.Configuration
{
    /// <summary>
    /// Copies BackFiller-owned bind, ACME, and DNS settings onto Common
    /// <see cref="AcmeCloudflareOptions"/>.
    /// </summary>
    /// <remarks>
    /// Common does not decide where application configuration lives. BackFiller
    /// binds those values from section <see cref="BackFillerOptions.SectionName"/>
    /// and this adapter feeds the reusable ACME/Cloudflare/networking types.
    /// The Cloudflare API key, ACME PKCS#12 password, and ACME account email are
    /// published from <c>nntpsharedconfig</c> and are not copied from
    /// <c>VECTOR__</c> variables. Root-level
    /// <c>BindAddress</c>, <c>BindPort</c>, <c>BindPortTls</c>,
    /// <c>CloudFlareZoneId</c>, <c>DnsSuffix</c>, and ACME directory keys are ignored.
    /// Cleartext <c>BindPort</c> is not a BackFiller setting and is not copied.
    /// </remarks>
    internal static class BackFillerAcmeCloudflareOptionsAdapter
    {
        /// <summary>
        /// Applies nested BackFiller settings and root secrets onto
        /// <paramref name="destination"/>.
        /// </summary>
        /// <param name="destination">Common options instance to populate.</param>
        /// <param name="source">Validated BackFiller application options.</param>
        /// <param name="configuration">Full configuration root. The Cloudflare operation timeout is the only root value copied.</param>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        internal static void Apply(
            AcmeCloudflareOptions destination,
            BackFillerOptions source,
            IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(configuration);

            destination.BindAddress = source.BindAddress is { Length: > 0 } ? source.BindAddress : [];
            destination.BindPortTls = source.BindPortTls ?? 0;
            destination.AcmeDirectoryUrl = string.IsNullOrWhiteSpace(source.AcmeDirectoryUrl)
                ? AcmeCloudflareOptions.DefaultAcmeDirectoryUrl
                : source.AcmeDirectoryUrl.Trim();
            destination.AcmeRenewalThresholdDays = source.AcmeRenewalThresholdDays;
            destination.AcmeStateDir = ResolveAcmeStateDir(source);
            destination.DnsSuffix = source.DnsSuffix;
            destination.CloudFlareZoneId = source.CloudFlareZoneId;
            destination.Fqdn = source.Fqdn;
            destination.IncludeNewsHostnameInCertificate = false;

            OverlayCloudFlareOperationTimeout(destination, configuration);
        }

        /// <summary>
        /// Copies a parseable Cloudflare operation timeout from the configuration root.
        /// Does not copy the API key, ACME account email, certificate password, bind
        /// settings, zone id, DNS suffix, or ACME directory.
        /// </summary>
        /// <param name="destination">Common options instance to update.</param>
        /// <param name="configuration">Full configuration root.</param>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        /// <remarks>
        /// A non-empty <see cref="AcmeCloudflareOptions.CloudFlareOperationTimeout"/>
        /// value that <see cref="TimeSpan.TryParse(string, out TimeSpan)"/> accepts replaces the destination timeout.
        /// A blank or unparseable value leaves it unchanged.
        /// </remarks>
        private static void OverlayCloudFlareOperationTimeout(AcmeCloudflareOptions destination, IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentNullException.ThrowIfNull(configuration);

            var timeout = configuration[nameof(AcmeCloudflareOptions.CloudFlareOperationTimeout)];
            if (!string.IsNullOrWhiteSpace(timeout) && TimeSpan.TryParse(timeout, out var parsed))
            {
                destination.CloudFlareOperationTimeout = parsed;
            }
        }

        /// <summary>
        /// Chooses the ACME state directory copied onto Common options.
        /// </summary>
        /// <param name="source">BackFiller options. The caller has already rejected null.</param>
        /// <returns>
        /// Trimmed <see cref="BackFillerOptions.AcmeStateDir"/> when it is not white space;
        /// otherwise trimmed <see cref="BackFillerOptions.CertificateDirectory"/> when that is not white space;
        /// otherwise <see cref="AcmeCloudflareOptions.DefaultAcmeStateDir"/>.
        /// </returns>
        private static string ResolveAcmeStateDir(BackFillerOptions source)
        {
            if (!string.IsNullOrWhiteSpace(source.AcmeStateDir))
            {
                return source.AcmeStateDir.Trim();
            }

            return string.IsNullOrWhiteSpace(source.CertificateDirectory)
                ? AcmeCloudflareOptions.DefaultAcmeStateDir
                : source.CertificateDirectory.Trim();
        }
    }
}
