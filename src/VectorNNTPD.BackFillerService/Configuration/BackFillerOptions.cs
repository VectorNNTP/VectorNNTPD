using System.ComponentModel.DataAnnotations;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.BackFillerService.Configuration
{
    /// <summary>
    /// Bindable BackFiller configuration under section <see cref="SectionName"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Property names are PascalCase. Generated <see cref="Fqdn"/> cannot be bound.
    /// Never log a complete instance: ACME/NntpDB secrets may appear on related options.
    /// The Cloudflare API key, ACME account email, and ACME PKCS#12 password are
    /// published from <c>nntpsharedconfig</c> onto <see cref="AcmeCloudflareOptions"/>.
    /// </para>
    /// <para>
    /// Bind, ACME directory, and DNS-suffix values are BackFiller-owned and bind
    /// only from section <see cref="SectionName"/>. Root-level
    /// <c>BindAddress</c>, <c>BindPort</c>, <c>BindPortTls</c>, <c>DnsSuffix</c>,
    /// and ACME directory keys are not used. There is no
    /// <c>BackFiller:BindPort</c>; the listener port is
    /// <see cref="BindPortTls"/> only. <see cref="ServerId"/> binds from
    /// <c>BackFiller:ServerId</c> only. The FQDN is
    /// <c>backfiller{ServerId:00}.{DnsSuffix}</c>; there is no configurable Name.
    /// RabbitMQ connectivity other than username and password binds from the
    /// top-level <c>RabbitMQ</c> section via Common <c>RabbitMqOptions</c> (not nested under
    /// <c>BackFiller</c>). Username and password are published from
    /// <c>nntpsharedconfig</c>. NntpDB uses the existing
    /// <c>ConnectionStrings__NntpDB</c> Generic Host mapping (same key as NNTPD).
    /// </para>
    /// <para>
    /// Intentional key rename from the old worker: <c>BackFiller:Id</c> is now
    /// <c>BackFiller:ServerId</c>. <c>DirLogs</c> is
    /// <c>BackFiller:Logging:File:LogDir</c>;
    /// <c>DirCerts</c> is <see cref="CertificateDirectory"/> /
    /// <see cref="AcmeStateDir"/>.
    /// </para>
    /// </remarks>
    internal sealed class BackFillerOptions
    {
        /// <summary>Configuration section name.</summary>
        internal const string SectionName = "BackFiller";

        /// <summary>Canonical VectorNNTP environment-variable prefix.</summary>
        internal const string EnvironmentVariablePrefix = VectorEnvironment.Prefix;

        /// <summary>Fixed FQDN host-label prefix. Not configuration.</summary>
        internal const string ApplicationPrefix = "backfiller";

        /// <summary>Canonical environment variable that supplies RabbitMQ username.</summary>
        internal const string RabbitMqUsernameEnvironmentVariable = "VECTOR__RABBITMQ__USERNAME";

        /// <summary>Canonical environment variable that supplies RabbitMQ password.</summary>
        internal const string RabbitMqPasswordEnvironmentVariable = "VECTOR__RABBITMQ__PASSWORD";

        /// <summary>Default DNS suffix when the key is omitted.</summary>
        private const string DefaultDnsSuffix = "usenet.ninja";

        /// <summary>Default relative certificate directory.</summary>
        private const string DefaultCertificateDirectory = "certs";

        /// <summary>Default Let's Encrypt staging ACME directory URL.</summary>
        internal const string DefaultAcmeDirectoryUrl = AcmeCloudflareOptions.DefaultAcmeDirectoryUrl;

        /// <summary>Default relative ACME state directory.</summary>
        private const string DefaultAcmeStateDir = AcmeCloudflareOptions.DefaultAcmeStateDir;

        /// <summary>Default certificate renewal lead time in days.</summary>
        internal const int DefaultAcmeRenewalThresholdDays = AcmeCloudflareOptions.DefaultAcmeRenewalThresholdDays;

        /// <summary>Default MySQL account-refresh poll interval in seconds.</summary>
        private const int DefaultAccountRefreshIntervalSeconds = 60;

        /// <summary>Minimum MySQL account-refresh poll interval in seconds.</summary>
        internal const int MinimumAccountRefreshIntervalSeconds = 5;

        /// <summary>Maximum MySQL account-refresh poll interval in seconds.</summary>
        internal const int MaximumAccountRefreshIntervalSeconds = 3600;

        /// <summary>
        /// Gets or sets the BackFiller server identifier.
        /// </summary>
        /// <remarks>
        /// Required. No default. Must be explicitly configured as an integer in
        /// <c>1–255</c> via <c>BackFiller:ServerId</c> (same bounds as NNTPD).
        /// Typed as <see cref="Nullable{T}"/> so a missing value remains distinguishable
        /// from an explicit <c>0</c> (both fail validation).
        /// </remarks>
        [Required]
        [Range(ServerIdRules.MinimumInclusive, ServerIdRules.MaximumInclusive)]
        public int? ServerId { get; set; }

        /// <summary>
        /// Gets or sets the DNS suffix used to generate <see cref="Fqdn"/>.
        /// </summary>
        /// <remarks>
        /// Omitted configuration uses <c>usenet.ninja</c>. A successful
        /// <c>nntpsharedconfig</c> projection replaces this value. Whitespace fails when the
        /// runtime snapshot canonicalizes the suffix.
        /// </remarks>
        public string DnsSuffix { get; set; } = DefaultDnsSuffix;

        /// <summary>
        /// Gets or sets the Cloudflare zone identifier.
        /// </summary>
        /// <remarks>
        /// Required at bind time from <c>BackFiller:CloudFlareZoneId</c> only.
        /// A successful <c>nntpsharedconfig</c> projection replaces the bound value.
        /// </remarks>
        public string CloudFlareZoneId { get; set; } = string.Empty;

        /// <summary>
        /// Gets the generated FQDN <c>backfiller{ServerId:00}.{DnsSuffix}</c>.
        /// </summary>
        /// <remarks>
        /// Not independently configurable. The <c>backfiller</c> prefix is fixed.
        /// Returns an empty string when <see cref="ServerId"/> is missing or outside the accepted range,
        /// or when <see cref="DnsSuffix"/> is empty or white space.
        /// </remarks>
        internal string Fqdn =>
            ServerId is { } serverId
            && ServerIdRules.IsInRange(serverId)
            && !string.IsNullOrWhiteSpace(DnsSuffix)
                ? ApplicationFqdn.Build(ApplicationPrefix, serverId, DnsSuffix)
                : string.Empty;

        /// <summary>
        /// Gets or sets listener bind addresses.
        /// </summary>
        /// <remarks>
        /// Optional. Omitted or empty means all interfaces (IPv4 and IPv6 any-address).
        /// Explicit addresses must be locally assigned. Wildcards: <c>*</c>, <c>0.0.0.0</c>, <c>::</c>.
        /// </remarks>
        public string[]? BindAddress { get; set; }

        /// <summary>
        /// Gets or sets the TLS TCP port used by the Cache Listener.
        /// </summary>
        /// <remarks>
        /// Required. Range 1–65535. BackFiller is TLS-only; there is no cleartext fallback.
        /// Nullable so missing is distinct from 0.
        /// </remarks>
        public int? BindPortTls { get; set; }

        /// <summary>
        /// Gets or sets the ACME directory URL.
        /// </summary>
        /// <remarks>
        /// Binder default is the Let's Encrypt staging directory. The value must be an absolute HTTPS URL
        /// when ACME options are validated. A successful <c>nntpsharedconfig</c> projection replaces it.
        /// </remarks>
        public string AcmeDirectoryUrl { get; set; } = DefaultAcmeDirectoryUrl;

        /// <summary>
        /// Gets or sets how many days before expiry a certificate is due for renewal.
        /// </summary>
        /// <remarks>
        /// Binder default is 30. ACME validation accepts 1–90. A successful <c>nntpsharedconfig</c>
        /// projection replaces the bound value before renewal decisions use it.
        /// </remarks>
        public int AcmeRenewalThresholdDays { get; set; } = DefaultAcmeRenewalThresholdDays;

        /// <summary>
        /// Gets or sets the ACME account and certificate state directory.
        /// </summary>
        /// <remarks>
        /// Relative paths resolve against the host content root.
        /// When empty, <see cref="CertificateDirectory"/> is used.
        /// </remarks>
        public string AcmeStateDir { get; set; } = DefaultAcmeStateDir;

        /// <summary>Gets or sets application-owned logging targets.</summary>
        /// <remarks>A null section is replaced by <see cref="BackFillerLoggingOptions"/> defaults when the runtime snapshot resolves the file-log directory.</remarks>
        public BackFillerLoggingOptions Logging { get; set; } = new();

        /// <summary>
        /// Gets or sets the directory used for ACME and TLS certificate artefacts.
        /// </summary>
        /// <remarks>
        /// Old key: <c>DirCerts</c>. Relative paths resolve against the host content root.
        /// When <see cref="AcmeStateDir"/> is empty, this value is used as the ACME state
        /// directory. Live TLS material is published through Common ACME /
        /// <c>ITlsCertificateContextProvider</c>; the Cache Listener does not load a
        /// fixed PFX filename from this directory.
        /// </remarks>
        public string CertificateDirectory { get; set; } = DefaultCertificateDirectory;

        /// <summary>
        /// Gets or sets in-memory article retention settings.
        /// </summary>
        /// <remarks>Required. The runtime snapshot rejects a null section.</remarks>
        public BackFillerArticleRetentionOptions ArticleRetention { get; set; } = new();

        /// <summary>
        /// Gets or sets a graceful shutdown policy.
        /// </summary>
        /// <remarks>Required. The runtime snapshot rejects a null section. Defaults live on <see cref="BackFillerShutdownOptions"/>.</remarks>
        public BackFillerShutdownOptions Shutdown { get; set; } = new();

        /// <summary>
        /// Gets or sets Listener resource-safety bounds.
        /// </summary>
        /// <remarks>Required. The runtime snapshot rejects a null section. Defaults and ranges live on <see cref="BackFillerListenerOptions"/>.</remarks>
        public BackFillerListenerOptions Listener { get; set; } = new();

        /// <summary>
        /// Gets or sets outbound NNTP provider session resource bounds.
        /// </summary>
        /// <remarks>Required. The runtime snapshot rejects a null section.</remarks>
        public BackFillerNntpOptions Nntp { get; set; } = new();

        /// <summary>
        /// Gets or sets Linux systemd notify/watchdog options.
        /// </summary>
        /// <remarks>Defaults live on <see cref="BackFillerSystemdOptions"/>. Windows hosts still bind the section.</remarks>
        public BackFillerSystemdOptions Systemd { get; set; } = new();

        /// <summary>
        /// Gets or sets how often BackFiller polls NntpDB for
        /// <c>nntpbackfilleraccounts</c> changes.
        /// </summary>
        /// <remarks>
        /// Default 60. Range 5–3600. This is not the per-account NNTP DATE
        /// keepalive; that value comes from each MySQL <c>keepalive</c> column.
        /// There is no dedicated environment-variable mapping for this key.
        /// </remarks>
        public int BackFillerAccountRefreshIntervalSeconds { get; set; } = DefaultAccountRefreshIntervalSeconds;

        /// <summary>
        /// Returns whether a bind-address token is a wildcard.
        /// </summary>
        /// <param name="entry">Configured token.</param>
        /// <returns><see langword="true"/> for all-interface wildcards.</returns>
        internal static bool IsBindAddressWildcard(string entry) =>
            AcmeCloudflareOptions.IsBindAddressWildcard(entry);
    }

    /// <summary>Graceful shutdown policy.</summary>
    internal sealed class BackFillerShutdownOptions
    {
        /// <summary>Minimum grace period in seconds.</summary>
        internal const int MinimumGracePeriodSeconds = 5;

        /// <summary>Maximum grace period in seconds.</summary>
        internal const int MaximumGracePeriodSeconds = 600;

        /// <summary>
        /// Gets or sets the complete application shutdown budget in seconds.
        /// </summary>
        /// <remarks>Used for worker drain and Generic Host <c>ShutdownTimeout</c>. Default 30. Range 5–600.</remarks>
        public int GracePeriodSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets whether already-admitted queued (not-yet-started) Article Work may
        /// proceed to start during shutdown.
        /// </summary>
        /// <remarks>
        /// Independent of <see cref="FinishActiveArticles"/>. When <see langword="false"/>,
        /// admitted work that has not entered <c>ProcessAsync</c> is settled as cancelled
        /// (NACK requeue) and does not start. This setting
        ///  does not drain broker-prefetched deliveries that were never admitted.
        /// </remarks>
        public bool DrainQueuedWork { get; set; } = true;

        /// <summary>
        /// Gets or sets whether Article Work that has already started processing may finish
        /// during shutdown.
        /// </summary>
        /// <remarks>
        /// Independent of <see cref="DrainQueuedWork"/>. When <see langword="false"/>, active
        /// work is cooperatively cancelled through the existing pipeline. Cancellation never
        /// ACKs the RabbitMQ delivery.
        /// </remarks>
        public bool FinishActiveArticles { get; set; } = true;
    }

    /// <summary>Listener resource-safety bounds.</summary>
    internal sealed class BackFillerListenerOptions
    {
        /// <summary>Maximum incomplete inbound protocol bytes per connection.</summary>
        /// <remarks>
        /// Default 262144. Validation requires 32768 through <see cref="int.MaxValue"/>.
        /// The runtime listener snapshot does not copy this value.
        /// </remarks>
        public int ParserAccumulationMaxBytes { get; set; } = 262144;

        /// <summary>TLS handshake timeout in seconds.</summary>
        /// <remarks>
        /// Default 30. Validation accepts 1–300. Copied into the runtime snapshot as a <see cref="TimeSpan"/>.
        /// </remarks>
        public int TlsHandshakeTimeoutSeconds { get; set; } = 30;

        /// <summary>No-progress deadline, in seconds, for one listener read or one non-empty listener write.</summary>
        /// <remarks>
        /// Default 60. Validation accepts 1–600. Copied into the runtime snapshot as a <see cref="TimeSpan"/>.
        /// A completed call is progress and the next call receives a new deadline. One buffer is not given a
        /// fresh deadline for every 32 KiB slice.
        /// </remarks>
        public int IoProgressTimeoutSeconds { get; set; } = 60;

        /// <summary>ReceiptAck wait after a completed Found transfer, in seconds.</summary>
        /// <remarks>
        /// Default 30. Validation accepts 1–300. The runtime listener snapshot does not copy this value.
        /// </remarks>
        public int AwaitingReceiptAckTimeoutSeconds { get; set; } = 30;

        /// <summary>Maximum queued Found payload bytes per connection.</summary>
        /// <remarks>
        /// Default 67108864. Validation requires 1 through <see cref="int.MaxValue"/>.
        /// Copied into the runtime snapshot. The cache listener does not read it.
        /// </remarks>
        public int MaxQueuedFoundPayloadBytes { get; set; } = 67108864;

        /// <summary>Maximum concurrently active accepted connections.</summary>
        /// <remarks>
        /// Default 1024. Validation requires 1 through <see cref="int.MaxValue"/>.
        /// The cache listener rejects further accepts at this count.
        /// Larger TCP buffers do not raise this cap.
        /// </remarks>
        public int MaxActiveConnections { get; set; } = 1024;

        /// <summary>Default TCP send-buffer limit for one accepted cache-listener socket, in bytes. Two MiB.</summary>
        /// <remarks>
        /// Article egress is the large direction on this socket. Two MiB is about 1.7 ms of 10 Gbps.
        /// It is a kernel buffer limit, not a reserved managed allocation and not the congestion window.
        /// </remarks>
        internal const int DefaultSendBufferBytes = 2 * 1024 * 1024;

        /// <summary>Default TCP receive-buffer limit for one accepted cache-listener socket, in bytes. One MiB.</summary>
        /// <remarks>
        /// Inbound listener traffic is short NNTP commands. One MiB is independent of <see cref="DefaultSendBufferBytes"/>.
        /// </remarks>
        internal const int DefaultReceiveBufferBytes = 1 * 1024 * 1024;

        /// <summary>Requested TCP send-buffer limit, in bytes, applied to each accepted socket before TLS.</summary>
        /// <remarks>
        /// Default <see cref="DefaultSendBufferBytes"/>. Validation accepts
        /// <see cref="Networking.TcpSocketOptions.MinimumBufferBytes"/> through
        /// <see cref="Networking.TcpSocketOptions.MaximumBufferBytes"/>.
        /// At the default <see cref="MaxActiveConnections"/> of 1024, the default send plus receive limits
        /// are a 3 GiB kernel-buffer ceiling (1024 × 3 MiB), not memory reserved when the listener binds.
        /// The operating system may round or double the stored limit.
        /// </remarks>
        public int SendBufferBytes { get; set; } = DefaultSendBufferBytes;

        /// <summary>Requested TCP receive-buffer limit, in bytes, applied to each accepted socket before TLS.</summary>
        /// <remarks>
        /// Default <see cref="DefaultReceiveBufferBytes"/>. Same validated range as <see cref="SendBufferBytes"/>.
        /// Not inherited from the listening socket.
        /// </remarks>
        public int ReceiveBufferBytes { get; set; } = DefaultReceiveBufferBytes;
    }

    /// <summary>Outbound NNTP provider session resource bounds.</summary>
    /// <remarks>
    /// Per-provider steady-state capacity remains MySQL <c>maxconnections</c>
    /// (<see cref="VectorNNTPD.BackFillerService.Nntp.BackFillerProviderDefinition.MaxSessions"/>).
    /// <see cref="MaxConcurrentSessionEstablishments"/> only bounds how many provider
    /// sessions may be in the process of connecting across the entire application.
    /// </remarks>
    internal sealed class BackFillerNntpOptions
    {
        /// <summary>Default application-wide concurrent provider-session establishment limit.</summary>
        /// <remarks>
        /// Chosen to allow meaningful startup parallelism while preventing aggregate
        /// establishment activity from scaling as providers × MaxSessions. Eight concurrent
        /// TCP/TLS/capability/auth handshakes is protective under multi-provider warm-up and
        /// mass replacement without serializing pool fill.
        /// </remarks>
        internal const int DefaultMaxConcurrentSessionEstablishments = 8;

        /// <summary>Minimum accepted concurrent establishment limit.</summary>
        internal const int MinimumMaxConcurrentSessionEstablishments = 1;

        /// <summary>Maximum accepted concurrent establishment limit.</summary>
        internal const int MaximumMaxConcurrentSessionEstablishments = 1024;

        /// <summary>Default TCP send-buffer limit for one upstream provider socket, in bytes. One MiB.</summary>
        /// <remarks>
        /// Upstream sends are short commands. One MiB is independent of <see cref="DefaultReceiveBufferBytes"/>.
        /// Nagle is disabled in code and is not a configuration setting.
        /// </remarks>
        internal const int DefaultSendBufferBytes = 1 * 1024 * 1024;

        /// <summary>Default TCP receive-buffer limit for one upstream provider socket, in bytes. Two MiB.</summary>
        /// <remarks>
        /// Article bodies arrive on this socket. Two MiB is about 1.7 ms of 10 Gbps.
        /// Steady-state session count remains per-provider <c>MaxSessions</c>. Each session requests
        /// 3 MiB of kernel buffering at these defaults. That is not a managed allocation, and
        /// <see cref="MaxConcurrentSessionEstablishments"/> still limits how many sessions may connect at once.
        /// </remarks>
        internal const int DefaultReceiveBufferBytes = 2 * 1024 * 1024;

        /// <summary>
        /// Gets or sets how many provider NNTP sessions may establish concurrently across all pools.
        /// </summary>
        /// <remarks>
        /// Default <see cref="DefaultMaxConcurrentSessionEstablishments"/>. Range
        /// <see cref="MinimumMaxConcurrentSessionEstablishments"/>–
        /// <see cref="MaximumMaxConcurrentSessionEstablishments"/>.
        /// Zero is invalid and does not mean unlimited. This does not cap steady-state
        /// <c>MaxSessions</c> per provider.
        /// </remarks>
        public int MaxConcurrentSessionEstablishments { get; set; } = DefaultMaxConcurrentSessionEstablishments;

        /// <summary>Requested TCP send-buffer limit, in bytes, for each upstream provider socket.</summary>
        /// <remarks>
        /// Default <see cref="DefaultSendBufferBytes"/>. Validation accepts
        /// <see cref="Networking.TcpSocketOptions.MinimumBufferBytes"/> through
        /// <see cref="Networking.TcpSocketOptions.MaximumBufferBytes"/>.
        /// Applied before connect and again on the connected socket, before TLS or NNTP bytes.
        /// </remarks>
        public int SendBufferBytes { get; set; } = DefaultSendBufferBytes;

        /// <summary>Requested TCP receive-buffer limit, in bytes, for each upstream provider socket.</summary>
        /// <remarks>
        /// Default <see cref="DefaultReceiveBufferBytes"/>. Independent of <see cref="SendBufferBytes"/>
        /// and of the 64 KiB managed NNTP reader buffer.
        /// </remarks>
        public int ReceiveBufferBytes { get; set; } = DefaultReceiveBufferBytes;
    }

    /// <summary>In-memory article retention policy.</summary>
    internal sealed class BackFillerArticleRetentionOptions
    {
        /// <summary>Bytes in one gibibyte.</summary>
        internal const long BytesPerGibibyte = 1024L * 1024L * 1024L;

        /// <summary>Fraction of physical memory allowed for retained payloads.</summary>
        internal const double PhysicalMemoryCeilingRatio = 0.80;

        /// <summary>
        /// Maximum retained payload capacity in GiB.
        /// Under pressure, FIFO reclaim skips entries that still have openable Success RequestIds;
        /// new admissions may then fail with capacity unavailable instead of invalidating those capabilities.
        /// </summary>
        /// <remarks>
        /// Default 4. Validation requires a positive value that does not exceed 80 percent of physical memory
        /// or the largest GiB count that fits in a 64-bit byte length. The snapshot stores the value in bytes.
        /// </remarks>
        public int MaximumRetainedPayloadGigabytes { get; set; } = 4;

        /// <summary>
        /// Maximum retention lifetime in seconds from insertion.
        /// TTL expiry removes the entry and clears remaining openable RequestIds.
        /// </summary>
        /// <remarks>Default 60. Validation accepts 1–60. The snapshot stores a <see cref="TimeSpan"/>.</remarks>
        public int RetentionTtlSeconds { get; set; } = 60;

        /// <summary>
        /// Maximum concurrently openable Article Work RequestIds attached to one retained Message-ID.
        /// AlreadyPresent attachments beyond this limit are rejected without revoking existing RequestIds.
        /// </summary>
        /// <remarks>Default 16. Validation accepts 1–256.</remarks>
        public int MaxOpenableRequestIdsPerArticle { get; set; } = 16;

        /// <summary>Sweep cadence in seconds.</summary>
        /// <remarks>Default 1. Validation accepts 1–60. The snapshot stores a <see cref="TimeSpan"/>.</remarks>
        public int SweepIntervalSeconds { get; set; } = 1;
    }
}
