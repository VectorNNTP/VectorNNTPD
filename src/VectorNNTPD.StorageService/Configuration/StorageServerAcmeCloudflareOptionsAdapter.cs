using Microsoft.Extensions.Configuration;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.StorageService.Configuration
{

    /// <summary>
    /// Copies StorageServer-owned bind settings and the ACME state directory onto Common
    /// <see cref="AcmeCloudflareOptions"/>.
    /// </summary>
    /// <remarks>
    /// Directory URL, renewal days, zone id, DNS suffix, account email, certificate password,
    /// and the Cloudflare API key are published from <c>nntpsharedconfig</c> and are not copied
    /// here. <see cref="AcmeCloudflareOptions.IncludeNewsHostnameInCertificate"/> is always
    /// <see langword="false"/>. Cleartext <see cref="AcmeCloudflareOptions.BindPort"/>
    /// is copied from <see cref="StorageServerOptions.BindPort"/> (default 0) and is
    /// never listened on by StorageServer.
    /// </remarks>
    public static class StorageServerAcmeCloudflareOptionsAdapter
    {
        /// <summary>
        /// Applies bind settings, the ACME state directory, and a root Cloudflare operation timeout onto
        /// <paramref name="destination"/>. Shared database fields are left for <c>nntpsharedconfig</c>.
        /// </summary>
        public static void Apply(
            AcmeCloudflareOptions destination,
            StorageServerOptions source,
            IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(configuration);

            destination.BindAddress = source.BindAddress is { Length: > 0 } ? source.BindAddress : [];
            destination.BindPort = source.BindPort;
            destination.BindPortTls = source.BindPortTls ?? 0;
            destination.AcmeStateDir = ResolveAcmeStateDir(source);
            destination.IncludeNewsHostnameInCertificate = false;

            var timeout = configuration[nameof(AcmeCloudflareOptions.CloudFlareOperationTimeout)];
            if (!string.IsNullOrWhiteSpace(timeout) && TimeSpan.TryParse(timeout, out var parsed))
            {
                destination.CloudFlareOperationTimeout = parsed;
            }
        }

        private static string ResolveAcmeStateDir(StorageServerOptions source)
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
