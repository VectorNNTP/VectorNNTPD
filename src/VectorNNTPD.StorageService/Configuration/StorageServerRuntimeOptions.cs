using System.Net;

namespace VectorNNTPD.StorageService.Configuration
{

    /// <summary>
    /// Immutable validated runtime snapshot consumed by application services.
    /// </summary>
    /// <remarks>
    /// Produced once after successful validation. Services must not re-read
    /// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> for these values.
    /// Never log a complete instance: it may contain certificate-directory material paths
    /// used alongside ACME secrets held separately on Common options.
    /// </remarks>
    public sealed record StorageServerRuntimeOptions(
        int ServerId,
        string DnsSuffix,
        string Fqdn,
        IReadOnlyList<string> BindAddressTokens,
        IReadOnlyList<IPAddress> CanonicalBindAddresses,
        int BindPort,
        int BindPortTls,
        string LogDir,
        string CertificateDirectory,
        TimeSpan GracefulShutdownTimeout,
        TimeSpan? StartupTimeout,
        bool StopHostOnUnexpectedServiceTermination,
        StorageServerListenerRuntimeOptions Listener,
        IReadOnlyList<string> CertificateDomainNames);

    /// <summary>Validated listener bounds.</summary>
    public sealed record StorageServerListenerRuntimeOptions(
        int ParserAccumulationMaxBytes,
        TimeSpan TlsHandshakeTimeout,
        TimeSpan IoProgressTimeout,
        int MaxActiveConnections);
}
