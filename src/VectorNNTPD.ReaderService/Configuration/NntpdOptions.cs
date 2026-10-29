using System.ComponentModel.DataAnnotations;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.ReaderService.Configuration
{
    /// <summary>
    /// Strongly typed lifecycle, hosting, and NNTPD listener options for VectorNNTPD.ReaderService.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NNTPD-specific settings bind from section <see cref="SectionName"/> and
    /// <c>NNTPD__*</c> environment variables. Bind addresses and ports and the
    /// ACME state directory bind from <c>Nntpd</c> only. ACME directory URL,
    /// renewal threshold, account email, certificate password, Cloudflare API key,
    /// zone id, and DNS suffix are published from <c>nntpsharedconfig</c>.
    /// RabbitMQ hosts and Management settings bind from the top-level
    /// <c>RabbitMQ</c> section. RabbitMQ username and password are published from
    /// <c>nntpsharedconfig</c> and are not read from <c>VECTOR__RABBITMQ__USERNAME</c>
    /// or <c>VECTOR__RABBITMQ__PASSWORD</c>.
    /// </para>
    /// <para>
    /// <see cref="Fqdn"/> is generated from <see cref="ServerId"/> and <see cref="AcmeCloudflareOptions.DnsSuffix"/> and cannot
    /// be bound or overridden from configuration or environment variables.
    /// </para>
    /// <para>
    /// Never log complete <see cref="NntpdOptions"/> instances:
    /// <see cref="AcmeCloudflareOptions.AcmeEmail"/>,
    /// <see cref="AcmeCloudflareOptions.CloudFlareApiKey"/>, <see cref="AcmeCloudflareOptions.AcmeCertificatePassword"/>,
    /// <see cref="XTraceKey"/> / <see cref="XTracePreviousKey"/>, and
    /// <see cref="NewsmasterPassword"/> are secrets.
    /// </para>
    /// </remarks>
    public sealed class NntpdOptions : AcmeCloudflareOptions, IApplicationLifecycleOptions
    {
        /// <summary>NNTPD-specific configuration section name.</summary>
        public new const string SectionName = "Nntpd";

        /// <summary>
        /// Legacy combined-host FQDN prefix retained for documentation and older fixtures.
        /// Live Reader/Transit hosts use <see cref="ApplicationPrefixFor"/>.
        /// </summary>
        public const string ApplicationPrefix = "nntpd";

        /// <summary>FQDN host-label prefix for the Reader service.</summary>
        public const string ReaderApplicationPrefix = "reader";

        /// <summary>FQDN host-label prefix for the Transit service.</summary>
        public const string TransitApplicationPrefix = "transit";

        /// <summary>
        /// Returns the FQDN host-label prefix for <paramref name="role"/>.
        /// </summary>
        /// <param name="role">Reader or Transit host role.</param>
        /// <returns>The role-specific prefix.</returns>
        public static string ApplicationPrefixFor(Hosting.NntpServerRole role) =>
            role switch
            {
                Hosting.NntpServerRole.Transit => TransitApplicationPrefix,
                _ => ReaderApplicationPrefix,
            };

        /// <summary>
        /// Gets or sets the FQDN host-label prefix for this process.
        /// Set by hosting registration; not bound from configuration.
        /// </summary>
        public string HostApplicationPrefix { get; set; } = ApplicationPrefix;

        /// <summary>
        /// Environment variable that supplies <see cref="ServerId"/>
        /// (<c>NNTPD__SERVERID</c>).
        /// </summary>
        public const string ServerIdEnvironmentVariable = "NNTPD__SERVERID";

        /// <summary>Configuration key for the POST <c>X-Trace</c> AES-256 key secret.</summary>
        public const string XTraceKeyConfigurationKey = "XTraceKey";

        /// <summary>Configuration key for the optional previous POST <c>X-Trace</c> AES-256 key.</summary>
        public const string XTracePreviousKeyConfigurationKey = "XTracePreviousKey";

        /// <summary>
        /// Environment variable that supplies <see cref="XTraceKey"/>
        /// (<c>NNTPD__XTRACEKEY</c>).
        /// </summary>
        public const string XTraceKeyEnvironmentVariable = "NNTPD__XTRACEKEY";

        /// <summary>
        /// Environment variable that supplies <see cref="XTracePreviousKey"/>
        /// (<c>NNTPD__XTRACEPREVIOUSKEY</c>).
        /// </summary>
        public const string XTracePreviousKeyEnvironmentVariable = "NNTPD__XTRACEPREVIOUSKEY";

        /// <summary>Configuration key for the optional newsmaster AUTHINFO username.</summary>
        public const string NewsmasterUserConfigurationKey = "NewsmasterUser";

        /// <summary>Configuration key for the optional newsmaster AUTHINFO password secret.</summary>
        public const string NewsmasterPasswordConfigurationKey = "NewsmasterPassword";

        /// <summary>
        /// Environment variable that supplies <see cref="NewsmasterUser"/>
        /// (<c>NNTPD__NEWSMASTERUSER</c>).
        /// </summary>
        public const string NewsmasterUserEnvironmentVariable = "NNTPD__NEWSMASTERUSER";

        /// <summary>
        /// Environment variable that supplies <see cref="NewsmasterPassword"/>
        /// (<c>NNTPD__NEWSMASTERPASSWORD</c>).
        /// </summary>
        public const string NewsmasterPasswordEnvironmentVariable = "NNTPD__NEWSMASTERPASSWORD";

        /// <summary>
        /// Gets or sets the application display name used by lifecycle messages.
        /// </summary>
        /// <remarks>
        /// Defaults to <see cref="ApplicationJsonConfiguration.EntryAssemblyName"/>.
        /// Log file names, the Serilog application property, the Windows service name,
        /// and the RabbitMQ connection prefix use the entry assembly name directly.
        /// </remarks>
        [Required(AllowEmptyStrings = false)]
        [MaxLength(128)]
        public string ApplicationName { get; set; } = ApplicationJsonConfiguration.EntryAssemblyName;

        /// <summary>
        /// Gets or sets the maximum time allowed for the overall graceful shutdown of application services.
        /// </summary>
        /// <remarks>
        /// This is a single wall-clock budget for the entire reverse-order stop sequence, not a fresh
        /// timeout granted independently to each service. <see cref="ApplicationServiceManager"/>
        /// awaits each service stop (no abandon). Services that ignore cancellation can block that await
        /// until they return; the host <c>ShutdownTimeout</c> (configured from this value) and external
        /// supervisors remain the process-level backstop.
        /// </remarks>
        public TimeSpan GracefulShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the maximum time allowed for application startup before cancellation is considered a failure.
        /// </summary>
        /// <remarks>
        /// When <see langword="null"/>, startup is bounded only by the host cancellation token.
        /// </remarks>
        public TimeSpan? StartupTimeout { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the process should exit if an application service
        /// terminates unexpectedly while running.
        /// </summary>
        public bool StopHostOnUnexpectedServiceTermination { get; set; } = true;

        /// <summary>
        /// Gets or sets systemd-specific notification and watchdog options.
        /// </summary>
        [Required]
        public SystemdOptions Systemd { get; set; } = new();

        /// <summary>
        /// Gets or sets trusted HAProxy PROXY-protocol peer addresses.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Optional. Each entry must be a literal IPv4 or IPv6 address (not a DNS name, not a CIDR).
        /// When empty or omitted, PROXY protocol processing is disabled and every connection's
        /// effective client endpoint is the TCP peer.
        /// </para>
        /// <para>
        /// When non-empty, a TCP peer whose address matches an entry is treated as a trusted proxy
        /// and must present a valid PROXY v1/v2 header before TLS/NNTP. Untrusted peers are not
        /// permitted to supply PROXY metadata that replaces their TCP identity.
        /// </para>
        /// </remarks>
        public string[] ProxyHosts { get; set; } = [];

        /// <summary>
        /// Gets or sets whether <c>AUTHINFO USER/PASS</c> is permitted when the connection is not TLS-protected.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Default is <see langword="true"/> so cleartext NNTP can authenticate. <c>AUTHINFO PASS</c>
        /// transmits the password in clear text at the NNTP protocol layer; prefer TLS in production.
        /// </para>
        /// <para>
        /// When <see langword="false"/> and the connection is not TLS-protected, AUTHINFO USER/PASS
        /// return <c>483</c> and <c>AUTHINFO USER</c> is not advertised in CAPABILITIES.
        /// TLS connections always permit AUTHINFO USER/PASS regardless of this setting.
        /// </para>
        /// </remarks>
        public bool AllowCleartextAuth { get; set; } = true;

        /// <summary>Default relative Serilog file-log and INN <c>news</c> directory.</summary>
        public const string DefaultLogDir = "logs/";

        /// <summary>
        /// Gets or sets the filesystem directory for Serilog daily rolling application logs
        /// and the INN-compatible <c>news</c> file.
        /// </summary>
        public string LogDir { get; set; } = DefaultLogDir;

        /// <summary>
        /// Gets or sets the numeric server identity used when generating <see cref="Fqdn"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Required. No default. Must be explicitly configured as an integer in <c>1–255</c>.
        /// </para>
        /// <para>
        /// Typed as <see cref="Nullable{T}"/> so a missing configuration value remains distinguishable
        /// from an explicitly configured <c>0</c> (both fail validation).
        /// </para>
        /// </remarks>
        [Required]
        [Range(ServerIdRules.MinimumInclusive, ServerIdRules.MaximumInclusive)]
        public int? ServerId { get; set; }

        /// <summary>
        /// Gets the generated server FQDN <c>nntpd{ServerId:00}.{DnsSuffix}</c>.
        /// </summary>
        /// <remarks>
        /// Not independently configurable. There is no dot between <c>nntpd</c> and the two-digit id.
        /// Example: <c>ServerId</c> 1 → <c>nntpd01.usenet.ninja</c>.
        /// Returns an empty string when <see cref="ServerId"/> is unset so data-annotation validation
        /// can inspect the object; dependent services must use this only after validation succeeds.
        /// </remarks>
        public override string Fqdn =>
            ServerId is { } serverId
            && ServerIdRules.IsInRange(serverId)
            && !string.IsNullOrWhiteSpace(DnsSuffix)
                ? FormatFqdn(
                    string.IsNullOrWhiteSpace(HostApplicationPrefix) ? ApplicationPrefix : HostApplicationPrefix,
                    serverId,
                    DnsSuffix)
                : string.Empty;

        /// <summary>
        /// Formats the generated FQDN for a validated server id and DNS suffix.
        /// </summary>
        /// <param name="serverId">Server id in 1–255.</param>
        /// <param name="dnsSuffix">DNS suffix without a trailing dot.</param>
        /// <returns>The FQDN string.</returns>
        public static string FormatFqdn(int serverId, string dnsSuffix) =>
            ApplicationFqdn.Build(ApplicationPrefix, serverId, dnsSuffix);

        /// <summary>
        /// Formats the generated FQDN for a role-specific host-label prefix.
        /// </summary>
        /// <param name="applicationPrefix">Host-label prefix (<c>reader</c> or <c>transit</c>).</param>
        /// <param name="serverId">Server id in 1–255.</param>
        /// <param name="dnsSuffix">DNS suffix without a trailing dot.</param>
        /// <returns>The FQDN string.</returns>
        public static string FormatFqdn(string applicationPrefix, int serverId, string dnsSuffix) =>
            ApplicationFqdn.Build(applicationPrefix, serverId, dnsSuffix);

        /// <summary>
        /// Gets or sets NNTPD-local transit runtime options (STREAM TX depth and
        /// WantTrash/LogTrash).
        /// </summary>
        /// <remarks>
        /// Named Transit peers bind from the top-level <c>Transit</c> section
        /// (<see cref="TransitPeersOptions"/>), not from this property.
        /// </remarks>
        [Required]
        public TransitOptions Transit { get; set; } = new();

        /// <summary>
        /// Gets or sets SPEEDTEST diagnostic limits.
        /// </summary>
        /// <remarks>
        /// Optional. Defaults cap duration, bytes, and concurrency for the VectorNNTP
        /// <c>SPEEDTEST</c> extension. Not part of the article data plane.
        /// </remarks>
        [Required]
        public SpeedTestOptions SpeedTest { get; set; } = new();

        /// <summary>
        /// Gets or sets temporary real-feed observability.
        /// </summary>
        /// <remarks>
        /// Off by default. Periodic snapshots only; not per-article logging.
        /// Also enabled by environment variable <c>VECTORNNTP_FEED_DIAGNOSTICS</c>.
        /// </remarks>
        [Required]
        public FeedDiagnosticsOptions FeedDiagnostics { get; set; } = new();

        /// <summary>
        /// Gets or sets how long a HistoryDB marker is retained in local memory and Redis.
        /// </summary>
        /// <remarks>
        /// Default is two hours. Redis uses native key TTL. The in-memory store expires the same
        /// digest after this period. HistoryDB policy lives here, not on <see cref="RedisOptions"/>.
        /// </remarks>
        public TimeSpan HistoryTime { get; set; } = TimeSpan.FromHours(2);

        /// <summary>Default NNTP command idle timeout in seconds.</summary>
        public const int DefaultIdleTime = 300;

        /// <summary>Minimum accepted <see cref="IdleTime"/> in seconds.</summary>
        public const int MinIdleTime = 1;

        /// <summary>Maximum accepted <see cref="IdleTime"/> in seconds (24 hours).</summary>
        public const int MaxIdleTime = 86_400;

        /// <summary>
        /// Gets or sets how many seconds an established NNTP session may remain without
        /// executing an NNTP command before the session is disconnected.
        /// </summary>
        /// <remarks>
        /// Unit is seconds. Default is <see cref="DefaultIdleTime"/>. Valid range is
        /// <see cref="MinIdleTime"/>–<see cref="MaxIdleTime"/>. <c>0</c> is invalid
        /// (not “disabled”); it follows the same minimum-1-second rule as other
        /// required timeouts such as <see cref="HistoryTime"/>.
        /// The timer starts after session setup (greeting) and resets when a command
        /// is accepted for processing. In-flight commands (including pipelined CHECK
        /// and TAKETHIS) keep the session non-idle.
        /// </remarks>
        public int IdleTime { get; set; } = DefaultIdleTime;

        /// <summary>Default POST article size limit: 5 MiB.</summary>
        public const int DefaultMaxArticleSize = 5 * 1024 * 1024;

        /// <summary>Minimum accepted <see cref="MaxArticleSize"/> in bytes.</summary>
        public const int MinMaxArticleSize = 1;

        /// <summary>Maximum accepted <see cref="MaxArticleSize"/> in bytes (100 MiB).</summary>
        public const int MaxMaxArticleSize = 100 * 1024 * 1024;

        /// <summary>
        /// Gets or sets the maximum destuffed POST article size in bytes.
        /// </summary>
        /// <remarks>
        /// Default is <see cref="DefaultMaxArticleSize"/> (5,242,880). Valid range is
        /// <see cref="MinMaxArticleSize"/>–<see cref="MaxMaxArticleSize"/>. The limit is
        /// enforced while the multiline article is streamed (destuffed headers + blank
        /// separator + body; NNTP terminator excluded; stuffing dots are not counted).
        /// Distinct from <c>ArticleIngestion:MaxArticleBytes</c>, which bounds
        /// IHAVE/TAKETHIS ingest and IHAVE worker destuff only.
        /// </remarks>
        public int MaxArticleSize { get; set; } = DefaultMaxArticleSize;

        /// <summary>Default POST <c>Injection-Info</c> complaint mailbox.</summary>
        public const string DefaultMailComplaintsTo = "abuse@usenet.ninja";

        /// <summary>
        /// Gets or sets the mailbox emitted as <c>mail-complaints-to</c> on server-owned
        /// POST <c>Injection-Info</c>.
        /// </summary>
        /// <remarks>
        /// Default is <see cref="DefaultMailComplaintsTo"/>. Required; must be a plausible
        /// mailbox. Client-supplied <c>Injection-Info</c> is never used.
        /// </remarks>
        public string MailComplaintsTo { get; set; } = DefaultMailComplaintsTo;

        /// <summary>
        /// Gets or sets Path-survey (ninpaths) report recipient mailboxes.
        /// </summary>
        /// <remarks>
        /// Missing, null, or empty disables internal ninpaths reporting. Whitespace-only
        /// entries are ignored. There is no separate enable flag and no environment-variable
        /// overlay. Reports are emailed through the existing <c>Email</c> subsystem.
        /// </remarks>
        public string[] Top1000 { get; set; } = [];

        /// <summary>
        /// Gets or sets the persisted AES-256 key used to protect POST <c>X-Trace</c> values.
        /// </summary>
        /// <remarks>
        /// Required secret. Supply 32 bytes as 64 hex characters or Base64 via
        /// <see cref="XTraceKeyEnvironmentVariable"/> or deployment secrets — never commit
        /// this value. The process does not generate a key at startup; restarting with the
        /// same key keeps previously issued tokens decryptable.
        /// </remarks>
        public string XTraceKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the previous AES-256 key retained for one-generation <c>X-Trace</c> rotation.
        /// </summary>
        /// <remarks>
        /// Optional. When set, trusted decrypt accepts tokens produced with this key.
        /// New tokens always use <see cref="XTraceKey"/>. After this value is removed,
        /// tokens produced only with the retired key cannot be decrypted.
        /// </remarks>
        public string XTracePreviousKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the AUTHINFO username that may POST a well-formed cancel control article.
        /// </summary>
        /// <remarks>
        /// Optional. When omitted with <see cref="NewsmasterPassword"/>, AUTHINFO remains deny-all
        /// (unless another provider is registered). When set, <see cref="NewsmasterPassword"/> is
        /// required. Ordinary POST clients never receive this identity.
        /// </remarks>
        public string NewsmasterUser { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the AUTHINFO password for <see cref="NewsmasterUser"/>.
        /// </summary>
        /// <remarks>
        /// Optional secret. Required when <see cref="NewsmasterUser"/> is set. Supply via
        /// <see cref="NewsmasterPasswordEnvironmentVariable"/> or deployment secrets — never commit
        /// this value. Do not log it or include it in exception messages.
        /// </remarks>
        public string NewsmasterPassword { get; set; } = string.Empty;

        /// <summary>
        /// Default Transit article-queue memory budget: 1 GiB (1,073,741,824 bytes).
        /// </summary>
        public const long DefaultTransitQueueMemoryLimit = 1_073_741_824L;

        /// <summary>
        /// Gets or sets the Transit article-queue memory budget in bytes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Bounds the sum of owned queued article payload lengths
        /// (<c>InboundArticle.Payload.Length</c>): complete NNTP article bytes as
        /// queued (IHAVE: stuffed wire, terminating <c>CRLF . CRLF</c> excluded).
        /// It does not include object overhead and is not a process-wide memory cap.
        /// Memory is released when queued articles are consumed.
        /// </para>
        /// <para>
        /// Default is 1 GiB. Valid range is <c>1</c> through <see cref="long.MaxValue"/>
        /// (signed 64-bit accounting). An individual article larger than this budget
        /// is rejected rather than waited for, so admission cannot deadlock.
        /// </para>
        /// </remarks>
        public long TransitQueueMemoryLimit { get; set; } = DefaultTransitQueueMemoryLimit;

        /// <summary>
        /// Gets or sets article ingestion worker options.
        /// </summary>
        /// <remarks>
        /// Defaults: max article 4 MiB. The ingestion worker does not write article bodies to disk.
        /// Queue admission is bounded by <see cref="TransitQueueMemoryLimit"/>, not
        /// an article-count cap. Used by <c>TAKETHIS</c> and <c>IHAVE</c>.
        /// </remarks>
        [Required]
        public ArticleIngestionOptions ArticleIngestion { get; set; } = new();
    }
}
