using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.BackFillerService.Configuration
{
    /// <summary>
    /// Validated runtime snapshot consumed by application services.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Produced once after successful validation. Services must not re-read
    /// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> for these values.
    /// Never log a complete instance: <see cref="NntpDbRuntimeOptions.ConnectionString"/> is secret.
    /// RabbitMQ broker connectivity settings live on Common <c>RabbitMqOptions</c> /
    /// <c>RabbitMqService</c>; this snapshot only carries Article Work application knobs.
    /// </para>
    /// <para>
    /// Non-identity fields are immutable record state. <see cref="DnsSuffix"/>, <see cref="Fqdn"/>, and
    /// <see cref="CertificateDomainNames"/> live on a shared <see cref="DnsIdentity"/> box that starts as the
    /// config-file provisional identity. After <c>nntpsharedconfig</c> is published,
    /// <see cref="ApplySharedDnsIdentity"/> updates that box in place so every holder of this singleton
    /// (and any <c>with</c> copy that shares the box) observes the authoritative FQDN.
    /// </para>
    /// </remarks>
    /// <param name="ServerId">Validated BackFiller server id.</param>
    /// <param name="DnsIdentity">Mutable DNS identity box shared across <c>with</c> copies of this snapshot.</param>
    /// <param name="BindAddressTokens">Trimmed non-empty bind tokens, including wildcards.</param>
    /// <param name="BindPortTls">TLS listener port.</param>
    /// <param name="LogDirectory">Resolved file-log directory, or empty when the file target directory is blank.</param>
    /// <param name="CertificateDirectory">Resolved ACME state directory.</param>
    /// <param name="Shutdown">Validated shutdown policy.</param>
    /// <param name="Listener">Validated listener bounds.</param>
    /// <param name="ArticleRetention">Validated retention policy.</param>
    /// <param name="RabbitMq">Article Work RabbitMQ knobs projected from Common options.</param>
    /// <param name="NntpDb">NntpDB connection projection. <see cref="NntpDbRuntimeOptions.ConnectionString"/> is secret.</param>
    /// <param name="AccountRefreshInterval">MySQL account-refresh poll interval.</param>
    /// <param name="Nntp">Validated outbound NNTP provider session bounds.</param>
    /// <param name="TcpKeepAlive">Validated TCP keepalive timings for upstream and accepted listener sockets.</param>
    internal sealed record BackFillerRuntimeOptions(
        int ServerId,
        BackFillerRuntimeDnsIdentity DnsIdentity,
        IReadOnlyList<string> BindAddressTokens,
        int BindPortTls,
        string LogDirectory,
        string CertificateDirectory,
        BackFillerShutdownRuntimeOptions Shutdown,
        BackFillerListenerRuntimeOptions Listener,
        BackFillerArticleRetentionRuntimeOptions ArticleRetention,
        BackFillerRabbitMqRuntimeOptions RabbitMq,
        NntpDbRuntimeOptions NntpDb,
        TimeSpan AccountRefreshInterval,
        BackFillerNntpRuntimeOptions Nntp,
        Networking.TcpKeepAliveSettings TcpKeepAlive)
    {
        /// <summary>Canonical DNS suffix. Updated by <see cref="ApplySharedDnsIdentity"/>.</summary>
        internal string DnsSuffix => DnsIdentity.DnsSuffix;

        /// <summary>Generated FQDN. Updated by <see cref="ApplySharedDnsIdentity"/>.</summary>
        internal string Fqdn => DnsIdentity.Fqdn;

        /// <summary>Certificate DNS identities for <see cref="Fqdn"/>. Updated by <see cref="ApplySharedDnsIdentity"/>.</summary>
        internal IReadOnlyList<string> CertificateDomainNames => DnsIdentity.CertificateDomainNames;

        /// <summary>
        /// Replaces the provisional config-file DNS identity with the authoritative values from
        /// <c>nntpsharedconfig</c>. Mutates <see cref="DnsIdentity"/> in place.
        /// </summary>
        /// <param name="dnsSuffix">Shared-configuration DNS suffix (canonicalized before store).</param>
        /// <param name="fqdn">Already-validated application FQDN for this server id and suffix.</param>
        /// <param name="includeNewsHostnameInCertificate">
        /// When <see langword="true"/>, certificate identities include the news hostname convention.
        /// </param>
        internal void ApplySharedDnsIdentity(
            string dnsSuffix,
            string fqdn,
            bool includeNewsHostnameInCertificate) =>
            DnsIdentity.Apply(dnsSuffix, fqdn, includeNewsHostnameInCertificate);
    }

    /// <summary>
    /// Mutable DNS identity for <see cref="BackFillerRuntimeOptions"/>. Shared by <c>with</c> copies of the snapshot.
    /// </summary>
    internal sealed class BackFillerRuntimeDnsIdentity
    {
        /// <summary>Creates the provisional identity from validated config-file values.</summary>
        /// <param name="dnsSuffix">Canonical DNS suffix.</param>
        /// <param name="fqdn">Generated FQDN.</param>
        /// <param name="certificateDomainNames">Certificate DNS identities for <paramref name="fqdn"/>.</param>
        internal BackFillerRuntimeDnsIdentity(
            string dnsSuffix,
            string fqdn,
            IReadOnlyList<string> certificateDomainNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dnsSuffix);
            ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
            ArgumentNullException.ThrowIfNull(certificateDomainNames);
            DnsSuffix = dnsSuffix;
            Fqdn = fqdn;
            CertificateDomainNames = certificateDomainNames;
        }

        /// <summary>Canonical DNS suffix.</summary>
        internal string DnsSuffix { get; private set; }

        /// <summary>Generated FQDN.</summary>
        internal string Fqdn { get; private set; }

        /// <summary>Certificate DNS identities for <see cref="Fqdn"/>.</summary>
        internal IReadOnlyList<string> CertificateDomainNames { get; private set; }

        /// <summary>Replaces the provisional identity with shared-configuration values.</summary>
        /// <param name="dnsSuffix">Shared-configuration DNS suffix.</param>
        /// <param name="fqdn">Already-validated application FQDN.</param>
        /// <param name="includeNewsHostnameInCertificate">Whether certificate identities include the news hostname.</param>
        internal void Apply(
            string dnsSuffix,
            string fqdn,
            bool includeNewsHostnameInCertificate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dnsSuffix);
            ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
            DnsSuffix = ApplicationFqdn.CanonicalizeDnsSuffix(dnsSuffix);
            Fqdn = fqdn;
            CertificateDomainNames = CertificateIdentities.ForFqdn(fqdn, includeNewsHostnameInCertificate);
        }
    }

    /// <summary>Validated shutdown policy captured in the runtime snapshot.</summary>
    /// <param name="GracePeriod">Complete application shutdown budget. Also drives host <c>ShutdownTimeout</c>.</param>
    /// <param name="DrainQueuedWork">
    /// When <see langword="true"/>, admitted-but-not-yet-started Article Work may acquire
    /// the session dispatch lock and start. When <see langword="false"/>, that work is
    /// cancelled without starting and settled as <c>Cancelled</c> (NACK requeue).
    /// </param>
    /// <param name="FinishActiveArticles">
    /// When <see langword="true"/>, work that has entered <c>ProcessAsync</c> may finish
    /// inside <paramref name="GracePeriod"/>. When <see langword="false"/>, that work is
    /// cooperatively cancelled through the existing pipeline.
    /// </param>
    internal sealed record BackFillerShutdownRuntimeOptions(
        TimeSpan GracePeriod,
        bool DrainQueuedWork,
        bool FinishActiveArticles);

    /// <summary>Validated listener bounds.</summary>
    /// <param name="TlsHandshakeTimeout">TLS handshake timeout.</param>
    /// <param name="IoProgressTimeout">
    /// No-progress deadline for one listener read or one non-empty listener write.
    /// A completed call starts a new deadline. A stream write does not reset it mid-call.
    /// </param>
    /// <param name="MaxQueuedFoundPayloadBytes">Maximum queued Found payload bytes per connection.</param>
    /// <param name="MaxActiveConnections">Maximum concurrently active accepted connections.</param>
    /// <param name="SendBufferBytes">TCP send-buffer limit applied to each accepted socket before TLS.</param>
    /// <param name="ReceiveBufferBytes">TCP receive-buffer limit applied to each accepted socket before TLS.</param>
    internal sealed record BackFillerListenerRuntimeOptions(
        TimeSpan TlsHandshakeTimeout,
        TimeSpan IoProgressTimeout,
        int MaxQueuedFoundPayloadBytes,
        int MaxActiveConnections,
        int SendBufferBytes,
        int ReceiveBufferBytes);

    /// <summary>Validated outbound NNTP provider session bounds.</summary>
    /// <param name="MaxConcurrentSessionEstablishments">
    /// Application-wide limit on concurrent provider-session establishment (TCP through Ready).
    /// Does not cap per-provider steady-state <c>MaxSessions</c>.
    /// </param>
    /// <param name="SendBufferBytes">TCP send-buffer limit for each upstream provider socket.</param>
    /// <param name="ReceiveBufferBytes">TCP receive-buffer limit for each upstream provider socket.</param>
    internal sealed record BackFillerNntpRuntimeOptions(
        int MaxConcurrentSessionEstablishments,
        int SendBufferBytes,
        int ReceiveBufferBytes);

    /// <summary>Validated retention policy.</summary>
    /// <param name="MaximumRetainedPayloadBytes">Maximum retained payload capacity in bytes.</param>
    /// <param name="RetentionTtl">Maximum retention lifetime from insertion.</param>
    /// <param name="SweepInterval">Retention sweep cadence.</param>
    /// <param name="MaxOpenableRequestIdsPerArticle">Maximum concurrently openable Article Work RequestIds for one retained Message-ID.</param>
    internal sealed record BackFillerArticleRetentionRuntimeOptions(
        long MaximumRetainedPayloadBytes,
        TimeSpan RetentionTtl,
        TimeSpan SweepInterval,
        int MaxOpenableRequestIdsPerArticle);

    /// <summary>Validated NntpDB projection without exposing the raw password in property names used for logs.</summary>
    /// <param name="ConnectionString">Full connection string. Secret.</param>
    /// <param name="Server">Server host.</param>
    /// <param name="Database">Database name.</param>
    internal sealed record NntpDbRuntimeOptions(
        string ConnectionString,
        string Server,
        string Database);

    /// <summary>
    /// Slim Article Work RabbitMQ knobs projected from Common <c>RabbitMqOptions</c>.
    /// </summary>
    /// <param name="WorkRequestMaxPayloadBytes">Maximum admitted work-request envelope size.</param>
    /// <param name="PublishConfirmTimeoutSeconds">Publisher-confirm wait for response publications.</param>
    /// <param name="ConsumerPrefetchCount">Optional Basic.Qos prefetch for Article Work consumers.</param>
    internal sealed record BackFillerRabbitMqRuntimeOptions(
        int WorkRequestMaxPayloadBytes,
        int PublishConfirmTimeoutSeconds,
        ushort? ConsumerPrefetchCount);
}
