using System.Net;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.StorageService.Configuration
{

    /// <summary>
    /// Builds <see cref="StorageServerRuntimeOptions"/> from already-validated bindable options.
    /// </summary>
    public static class StorageServerRuntimeOptionsFactory
    {
        /// <summary>
        /// Projects validated options into the immutable runtime snapshot.
        /// </summary>
        public static StorageServerRuntimeOptions Create(
            StorageServerOptions options,
            string? contentRootPath = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            var acme = new AcmeCloudflareOptions
            {
                BindAddress = options.BindAddress is { Length: > 0 } ? options.BindAddress : ["*"],
                BindPort = options.BindPort,
                BindPortTls = options.BindPortTls ?? 0,
                Fqdn = options.Fqdn,
                IncludeNewsHostnameInCertificate = false,
                AcmeDirectoryUrl = options.AcmeDirectoryUrl,
                AcmeRenewalThresholdDays = options.AcmeRenewalThresholdDays,
                AcmeCertificatePassword = string.Empty,
                AcmeStateDir = string.IsNullOrWhiteSpace(options.AcmeStateDir)
                    ? options.CertificateDirectory
                    : options.AcmeStateDir,
                CloudFlareApiKey = string.Empty,
                CloudFlareZoneId = string.Empty,
                DnsSuffix = options.DnsSuffix,
            };
            return Create(options, acme, contentRootPath);
        }

        /// <inheritdoc cref="Create(StorageServerOptions,string?)"/>
        public static StorageServerRuntimeOptions Create(
            StorageServerOptions options,
            AcmeCloudflareOptions acme,
            string? contentRootPath = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(acme);

            if (options.ServerId is not { } serverId || string.IsNullOrWhiteSpace(options.Fqdn))
            {
                throw new InvalidOperationException("Validated StorageServer identity is required to build runtime options.");
            }

            var dnsSuffix = ApplicationFqdn.CanonicalizeDnsSuffix(options.DnsSuffix);
            var fqdn = options.Fqdn;
            if (acme.BindPortTls is < 1 or > 65535)
            {
                throw new InvalidOperationException(
                    "BindPortTls is required and must be 1–65535 because StorageServer is TLS-only. There is no cleartext fallback.");
            }

            var bindPortTls = acme.BindPortTls;
            var tokens = (acme.BindAddress is { Length: > 0 } ? acme.BindAddress : ["*"])
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Select(static x => x.Trim())
                .ToArray();

            var addresses = new List<IPAddress>();
            foreach (var token in tokens)
            {
                if (AcmeCloudflareOptions.IsBindAddressWildcard(token))
                {
                    continue;
                }

                if (IPAddress.TryParse(token, out var address))
                {
                    addresses.Add(address);
                }
            }

            var listener = options.Listener ?? throw new InvalidOperationException("StorageServer:Listener is required.");

            return new StorageServerRuntimeOptions(
                ServerId: serverId,
                DnsSuffix: dnsSuffix,
                Fqdn: fqdn,
                BindAddressTokens: tokens,
                CanonicalBindAddresses: addresses,
                BindPort: options.BindPort,
                BindPortTls: bindPortTls,
                LogDir: ApplicationLocalPath.ResolveApplicationLocalPath(options.LogDir, contentRootPath),
                CertificateDirectory: ApplicationLocalPath.ResolveApplicationLocalPath(acme.AcmeStateDir, contentRootPath),
                GracefulShutdownTimeout: options.GracefulShutdownTimeout,
                StartupTimeout: options.StartupTimeout,
                StopHostOnUnexpectedServiceTermination: options.StopHostOnUnexpectedServiceTermination,
                Listener: new StorageServerListenerRuntimeOptions(
                    listener.ParserAccumulationMaxBytes,
                    TimeSpan.FromSeconds(listener.TlsHandshakeTimeoutSeconds),
                    TimeSpan.FromSeconds(listener.IoProgressTimeoutSeconds),
                    listener.MaxActiveConnections),
                CertificateDomainNames: CertificateIdentities.ForFqdn(fqdn, includeNewsHostname: false));
        }
    }
}
