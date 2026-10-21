
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Networking;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.BackFillerService.Configuration
{
    /// <summary>
    /// Validates <see cref="BackFillerOptions"/> at bind / startup time.
    /// </summary>
    /// <remarks>
    /// Does not bind sockets, create directories, or connect to MySQL, RabbitMQ, or Cloudflare.
    /// Failure messages never include secret values. RabbitMQ connectivity is validated by
    /// Common <see cref="RabbitMqOptionsValidator"/>; this type only cross-checks shutdown
    /// grace against <see cref="RabbitMqOptions.MaximumShutdownDrainTimeoutSeconds"/>.
    /// </remarks>
    internal sealed class BackFillerOptionsValidator : IValidateOptions<BackFillerOptions>
    {
        /// <summary>Operating-system physical-memory source used by retention ceiling validation.</summary>
        private readonly IPhysicalMemoryProvider _physicalMemoryProvider;

        /// <summary>Bound RabbitMQ options. Used only to compare shutdown grace with the drain timeout.</summary>
        private readonly IOptions<RabbitMqOptions> _rabbitMqOptions;

        /// <summary>
        /// Initializes a new validator.
        /// </summary>
        /// <param name="physicalMemoryProvider">Physical-memory probe for retention capacity.</param>
        /// <param name="rabbitMqOptions">Top-level RabbitMQ options used for grace-period cross-check.</param>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
#pragma warning disable IDE0290 // Use primary constructor
        public BackFillerOptionsValidator(
#pragma warning restore IDE0290 // Use primary constructor
            IPhysicalMemoryProvider physicalMemoryProvider,
            IOptions<RabbitMqOptions> rabbitMqOptions)
        {
            _physicalMemoryProvider = physicalMemoryProvider ?? throw new ArgumentNullException(nameof(physicalMemoryProvider));
            _rabbitMqOptions = rabbitMqOptions ?? throw new ArgumentNullException(nameof(rabbitMqOptions));
        }

        /// <summary>
        /// Validates identity, TLS port, ACME, logging, shutdown, listener, NNTP establishment concurrency, account refresh, host physical memory, retention, systemd, and the RabbitMQ drain cross-check.
        /// </summary>
        /// <param name="name">Named-options name. Not consulted; every instance is validated the same way.</param>
        /// <param name="options">Bound BackFiller options.</param>
        /// <returns><see cref="ValidateOptionsResult.Success"/> when no failures were collected; otherwise <see cref="ValidateOptionsResult.Fail(IEnumerable{string})"/> with those messages.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is null, or the RabbitMQ options value passed to the drain cross-check is null.</exception>
        /// <remarks>
        /// Invalid configuration is returned as failure messages and is not thrown.
        /// A physical-memory probe failure or a host below
        /// <see cref="BackFillerArticleRetentionOptions.MinimumPhysicalMemoryBytes"/> fails startup.
        /// This method does not bind sockets, create directories, or connect to MySQL, RabbitMQ, or Cloudflare.
        /// </remarks>
        public ValidateOptionsResult Validate(string? name, BackFillerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var failures = new List<string>();
            ValidateIdentity(options, failures);
            ValidateBindPortTls(options, failures);
            ValidateAcme(options, failures);
            ValidateLogging(options, failures);
            ValidateShutdown(options, failures);
            ValidateListener(options, failures);
            ValidateNntp(options, failures);
            ValidateTcpKeepAlive(options, failures);
            ValidateAccountRefresh(options, failures);
            ValidateArticleRetention(options, failures);
            ValidateSystemd(options, failures);
            ValidateRabbitMqDrainAgainstGrace(options, _rabbitMqOptions.Value, failures);

            return failures.Count > 0
                ? ValidateOptionsResult.Fail(failures)
                : ValidateOptionsResult.Success;
        }

        /// <summary>Appends a failure when the watchdog fraction is outside 0.05–0.9.</summary>
        /// <param name="options">Options whose <see cref="BackFillerOptions.Systemd"/> is checked.</param>
        /// <param name="failures">Failure list to append. Not cleared.</param>
        /// <remarks>
        /// NaN, values at or outside (0, 1), and values inside that interval but outside 0.05–0.9 each add one message.
        /// Other systemd flags are not checked.
        /// </remarks>
        private static void ValidateSystemd(BackFillerOptions options, List<string> failures)
        {
            var fraction = options.Systemd.WatchdogIntervalFraction;
            if (double.IsNaN(fraction) || fraction is <= 0 or >= 1)
            {
                failures.Add(
                    $"{nameof(BackFillerOptions.Systemd)}.{nameof(BackFillerSystemdOptions.WatchdogIntervalFraction)} must be in the open interval (0, 1).");
            }
            else if (fraction is < 0.05 or > 0.9)
            {
                failures.Add(
                    $"{nameof(BackFillerOptions.Systemd)}.{nameof(BackFillerSystemdOptions.WatchdogIntervalFraction)} must be between 0.05 and 0.9 inclusive.");
            }
        }

        /// <summary>Appends the first identity failure and then returns.</summary>
        /// <param name="options">Options whose server id and generated host label are checked.</param>
        /// <param name="failures">Failure list to append. Not cleared.</param>
        /// <remarks>
        /// DNS suffix, Cloudflare zone id, and the generated FQDN are validated from <c>nntpsharedconfig</c>
        /// when that snapshot is published. A missing or rejected server id returns immediately.
        /// </remarks>
        private static void ValidateIdentity(BackFillerOptions options, List<string> failures)
        {
            if (options.ServerId is not { } serverId)
            {
                failures.Add(ServerIdRules.Validate(null, "BackFiller:ServerId")!);
                return;
            }

            var serverIdFailure = ServerIdRules.Validate(serverId, "BackFiller:ServerId");
            if (serverIdFailure is not null)
            {
                failures.Add(serverIdFailure);
                return;
            }

            var hostLabel = ApplicationFqdn.FormatHostLabel(BackFillerOptions.ApplicationPrefix, serverId);
            if (!BackFillerIdentity.IsValidDnsLabel(hostLabel))
            {
                failures.Add("BackFiller generated host label is not a valid DNS label.");
            }
        }

        /// <summary>Appends one failure when the TLS port is missing or outside 1–65535.</summary>
        /// <param name="options">Options whose <see cref="BackFillerOptions.BindPortTls"/> is checked.</param>
        /// <param name="failures">Failure list to append. Not cleared.</param>
        private static void ValidateBindPortTls(BackFillerOptions options, List<string> failures)
        {
            if (options.BindPortTls is null or < 1 or > 65535)
            {
                failures.Add(
                    "BackFiller:BindPortTls is required and must be an integer in the range 1–65535 because BackFiller is TLS-only. There is no cleartext listener fallback.");
            }
        }

        /// <summary>Appends a failure when the ACME state directory is missing. Directory URL and renewal days come from <c>nntpsharedconfig</c>.</summary>
        /// <param name="options">Options whose ACME fields are checked.</param>
        /// <param name="failures">Failure list to append. Not cleared.</param>
        /// <remarks>
        /// State fails only when both <see cref="BackFillerOptions.AcmeStateDir"/> and
        /// <see cref="BackFillerOptions.CertificateDirectory"/> are empty or white space.
        /// </remarks>
        private static void ValidateAcme(BackFillerOptions options, List<string> failures)
        {
            if (string.IsNullOrWhiteSpace(options.AcmeStateDir) && string.IsNullOrWhiteSpace(options.CertificateDirectory))
            {
                failures.Add("BackFiller:AcmeStateDir must be a non-empty filesystem path.");
            }
        }

        /// <summary>Appends failures for log level, retention, and each enabled logging target.</summary>
        /// <param name="options">Options whose <see cref="BackFillerOptions.Logging"/> section is checked.</param>
        /// <param name="failures">Failure list to append. Not cleared.</param>
        /// <remarks>
        /// Level must be a Serilog name accepted by <see cref="BackFillerLogLevelParser.TryParse"/>.
        /// Retention must be inside <see cref="BackFillerLoggingOptions.MinimumLogRetentionDays"/>–<see cref="BackFillerLoggingOptions.MaximumLogRetentionDays"/>.
        /// An enabled file target requires a non-white-space directory. An enabled RabbitMQ target requires exchange and routing key.
        /// A disabled syslog target skips host, port, and protocol checks. An enabled one requires a host, a port in 1–65535, and protocol <c>Udp</c> or <c>Tcp</c> compared ordinal-ignore-case.
        /// </remarks>
        private static void ValidateLogging(BackFillerOptions options, List<string> failures)
        {
            var logging = options.Logging;
            if (!BackFillerLogLevelParser.TryParse(logging.LogLevel, out _))
            {
                failures.Add(
                    "BackFiller:Logging:LogLevel must be one of Verbose, Debug, Information, Warning, Error, Fatal.");
            }

            if (logging.LogRetentionDays is < BackFillerLoggingOptions.MinimumLogRetentionDays
                or > BackFillerLoggingOptions.MaximumLogRetentionDays)
            {
                failures.Add(
                    $"BackFiller:Logging:LogRetentionDays must be an integer in the range {BackFillerLoggingOptions.MinimumLogRetentionDays}–{BackFillerLoggingOptions.MaximumLogRetentionDays}.");
            }

            var file = logging.File;
            if (file.Enabled && string.IsNullOrWhiteSpace(file.LogDir))
            {
                failures.Add("BackFiller:Logging:File:LogDir is required when file logging is enabled (old key: DirLogs).");
            }

            var rabbit = logging.RabbitMq;
            if (rabbit.Enabled)
            {
                if (string.IsNullOrWhiteSpace(rabbit.Exchange))
                {
                    failures.Add("BackFiller:Logging:RabbitMQ:Exchange is required when RabbitMQ logging is enabled.");
                }

                if (string.IsNullOrWhiteSpace(rabbit.RoutingKey))
                {
                    failures.Add("BackFiller:Logging:RabbitMQ:RoutingKey is required when RabbitMQ logging is enabled.");
                }
            }

            var syslog = logging.Syslog;
            if (!syslog.Enabled)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(syslog.Host))
            {
                failures.Add("BackFiller:Logging:Syslog:Host is required when syslog logging is enabled.");
            }

            if (syslog.Port is < 1 or > 65535)
            {
                failures.Add("BackFiller:Logging:Syslog:Port must be an integer in the range 1–65535.");
            }

            if (!string.Equals(syslog.Protocol?.Trim(), BackFillerSyslogLoggingTargetOptions.UdpProtocol, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(syslog.Protocol?.Trim(), BackFillerSyslogLoggingTargetOptions.TcpProtocol, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("BackFiller:Logging:Syslog:Protocol must be Udp or Tcp.");
            }
        }

        /// <summary>Appends a failure when the shutdown grace period is outside 5–600 seconds.</summary>
        /// <param name="options">Options whose <see cref="BackFillerOptions.Shutdown"/> section is checked.</param>
        /// <param name="failures">Failure list to append. Not cleared.</param>
        /// <remarks>
        /// The grace period must be inside 5–600 seconds.
        /// </remarks>
        private static void ValidateShutdown(BackFillerOptions options, List<string> failures)
        {
            var shutdown = options.Shutdown;
            if (shutdown.GracePeriodSeconds is < BackFillerShutdownOptions.MinimumGracePeriodSeconds
                or > BackFillerShutdownOptions.MaximumGracePeriodSeconds)
            {
                failures.Add(
                    $"BackFiller:Shutdown:GracePeriodSeconds must be between {BackFillerShutdownOptions.MinimumGracePeriodSeconds} and {BackFillerShutdownOptions.MaximumGracePeriodSeconds}.");
            }
        }

        /// <summary>Appends failures for listener accumulation, timeouts, queued Found bytes, and connection capacity.</summary>
        /// <param name="options">Options whose <see cref="BackFillerOptions.Listener"/> section is checked.</param>
        /// <param name="failures">Failure list to append. Not cleared.</param>
        /// <remarks>
        /// Accumulation must be at least 32,768. TLS handshake and receipt-ack timeouts must be 1–300 seconds.
        /// I/O progress timeout must be 1–600 seconds. Queued Found bytes and active connections must be at least 1.
        /// The failure text also states an upper bound of <see cref="int.MaxValue"/>; values above that cannot be stored in these <see cref="int"/> properties.
        /// </remarks>
        private static void ValidateListener(BackFillerOptions options, List<string> failures)
        {
            var listener = options.Listener;
            if (listener.ParserAccumulationMaxBytes < 32_768)
            {
                failures.Add("BackFiller:Listener:ParserAccumulationMaxBytes must be between 32,768 and 2,147,483,647.");
            }

            if (listener.TlsHandshakeTimeoutSeconds is < 1 or > 300)
            {
                failures.Add("BackFiller:Listener:TlsHandshakeTimeoutSeconds must be between 1 and 300.");
            }

            if (listener.IoProgressTimeoutSeconds is < 1 or > 600)
            {
                failures.Add("BackFiller:Listener:IoProgressTimeoutSeconds must be between 1 and 600.");
            }

            if (listener.AwaitingReceiptAckTimeoutSeconds is < 1 or > 300)
            {
                failures.Add("BackFiller:Listener:AwaitingReceiptAckTimeoutSeconds must be between 1 and 300.");
            }

            if (listener.MaxQueuedFoundPayloadBytes < 1)
            {
                failures.Add("BackFiller:Listener:MaxQueuedFoundPayloadBytes must be between 1 and 2,147,483,647.");
            }

            if (listener.MaxActiveConnections < 1)
            {
                failures.Add("BackFiller:Listener:MaxActiveConnections must be between 1 and 2,147,483,647.");
            }

            ValidateBufferSize("BackFiller:Listener:SendBufferBytes", listener.SendBufferBytes, failures);
            ValidateBufferSize("BackFiller:Listener:ReceiveBufferBytes", listener.ReceiveBufferBytes, failures);
        }

        /// <summary>
        /// Appends a failure when concurrent provider-session establishment is outside 1–1024.
        /// </summary>
        /// <param name="options">Options whose <see cref="BackFillerOptions.Nntp"/> section is checked.</param>
        /// <param name="failures">Failure list to append. Not cleared.</param>
        /// <remarks>
        /// Zero and negative values are rejected. Zero does not mean unlimited.
        /// Values above <see cref="BackFillerNntpOptions.MaximumMaxConcurrentSessionEstablishments"/> fail.
        /// </remarks>
        private static void ValidateNntp(BackFillerOptions options, List<string> failures)
        {
            var nntp = options.Nntp;
            if (nntp.MaxConcurrentSessionEstablishments
                is < BackFillerNntpOptions.MinimumMaxConcurrentSessionEstablishments
                or > BackFillerNntpOptions.MaximumMaxConcurrentSessionEstablishments)
            {
                failures.Add(
                    "BackFiller:Nntp:MaxConcurrentSessionEstablishments must be between "
                    + $"{BackFillerNntpOptions.MinimumMaxConcurrentSessionEstablishments} and "
                    + $"{BackFillerNntpOptions.MaximumMaxConcurrentSessionEstablishments}.");
            }

            ValidateBufferSize("BackFiller:Nntp:SendBufferBytes", nntp.SendBufferBytes, failures);
            ValidateBufferSize("BackFiller:Nntp:ReceiveBufferBytes", nntp.ReceiveBufferBytes, failures);
        }

        /// <summary>
        /// Appends failures when TCP keepalive timings are outside the ranges shared with
        /// <see cref="TcpKeepAliveSettings"/>.
        /// </summary>
        /// <param name="options">Options whose <see cref="BackFillerOptions.TcpKeepAlive"/> section is checked.</param>
        /// <param name="failures">Failure list to append. Not cleared.</param>
        private static void ValidateTcpKeepAlive(BackFillerOptions options, List<string> failures)
        {
            var keepAlive = options.TcpKeepAlive;
            if (keepAlive.TimeSeconds
                is < TcpKeepAliveSettings.MinimumPositive
                or > TcpKeepAliveSettings.MaximumTimeSeconds)
            {
                failures.Add(
                    "BackFiller:TcpKeepAlive:TimeSeconds must be between "
                    + $"{TcpKeepAliveSettings.MinimumPositive} and {TcpKeepAliveSettings.MaximumTimeSeconds}.");
            }

            if (keepAlive.IntervalSeconds
                is < TcpKeepAliveSettings.MinimumPositive
                or > TcpKeepAliveSettings.MaximumIntervalSeconds)
            {
                failures.Add(
                    "BackFiller:TcpKeepAlive:IntervalSeconds must be between "
                    + $"{TcpKeepAliveSettings.MinimumPositive} and {TcpKeepAliveSettings.MaximumIntervalSeconds}.");
            }

            if (keepAlive.RetryCount
                is < TcpKeepAliveSettings.MinimumPositive
                or > TcpKeepAliveSettings.MaximumRetryCount)
            {
                failures.Add(
                    "BackFiller:TcpKeepAlive:RetryCount must be between "
                    + $"{TcpKeepAliveSettings.MinimumPositive} and {TcpKeepAliveSettings.MaximumRetryCount}.");
            }
        }

        /// <summary>Appends a failure when a TCP buffer limit is outside 16 KiB through 16 MiB.</summary>
        /// <param name="key">Configuration key included in the failure text.</param>
        /// <param name="bytes">Requested buffer size.</param>
        /// <param name="failures">Failure list to append. Not cleared.</param>
        private static void ValidateBufferSize(string key, int bytes, List<string> failures)
        {
            if (bytes is < TcpSocketOptions.MinimumBufferBytes or > TcpSocketOptions.MaximumBufferBytes)
            {
                failures.Add(FormattableString.Invariant(
                    $"{key} must be between {TcpSocketOptions.MinimumBufferBytes:N0} and {TcpSocketOptions.MaximumBufferBytes:N0}."));
            }
        }

        /// <summary>Appends a failure when the account poll interval is outside 5–3600 seconds.</summary>
        /// <param name="options">Options whose <see cref="BackFillerOptions.BackFillerAccountRefreshIntervalSeconds"/> is checked.</param>
        /// <param name="failures">Failure list to append. Not cleared.</param>
        private static void ValidateAccountRefresh(BackFillerOptions options, List<string> failures)
        {
            if (options.BackFillerAccountRefreshIntervalSeconds
                is < BackFillerOptions.MinimumAccountRefreshIntervalSeconds
                or > BackFillerOptions.MaximumAccountRefreshIntervalSeconds)
            {
                failures.Add(
                    "BackFiller:BackFillerAccountRefreshIntervalSeconds must be between 5 and 3600.");
            }
        }

        /// <summary>Appends retention range failures, the host physical-memory prerequisite, and the retention ceiling failure.</summary>
        /// <param name="options">Options whose <see cref="BackFillerOptions.ArticleRetention"/> section is checked.</param>
        /// <param name="failures">Failure list to append. Not cleared.</param>
        /// <remarks>
        /// A maximum below 1 returns before the other checks. TTL and sweep must be 1–60 seconds.
        /// Openable request ids must be 1–256. Those range checks run before the memory probe.
        /// Any exception from <see cref="IPhysicalMemoryProvider.GetTotalPhysicalMemoryBytes"/> adds one host-memory
        /// failure and returns without assuming the minimum is met.
        /// Detected RAM below <see cref="BackFillerArticleRetentionOptions.MinimumPhysicalMemoryBytes"/> fails startup.
        /// Otherwise, the configured gibibytes must not exceed 80 per cent of total physical memory, truncated down to
        /// whole gibibytes with no artificial floor, and must not exceed the largest <see cref="int"/> whose product with
        /// <see cref="BackFillerArticleRetentionOptions.BytesPerGibibyte"/> fits in <see cref="long"/>.
        /// </remarks>
        private void ValidateArticleRetention(BackFillerOptions options, List<string> failures)
        {
            var retention = options.ArticleRetention;
            if (retention.MaximumRetainedPayloadGigabytes < 1)
            {
                failures.Add("BackFiller:ArticleRetention:MaximumRetainedPayloadGigabytes must be greater than zero.");
                return;
            }

            if (retention.RetentionTtlSeconds is < 1 or > 60)
            {
                failures.Add("BackFiller:ArticleRetention:RetentionTtlSeconds must be between 1 and 60.");
            }

            if (retention.MaxOpenableRequestIdsPerArticle is < 1 or > 256)
            {
                failures.Add("BackFiller:ArticleRetention:MaxOpenableRequestIdsPerArticle must be between 1 and 256.");
            }

            if (retention.SweepIntervalSeconds is < 1 or > 60)
            {
                failures.Add("BackFiller:ArticleRetention:SweepIntervalSeconds must be between 1 and 60.");
            }

            long totalPhysicalMemoryBytes;
            try
            {
                totalPhysicalMemoryBytes = _physicalMemoryProvider.GetTotalPhysicalMemoryBytes();
            }
            // codeql[cs/catch-of-all-exceptions] Validation must report every rejected value and continue checking the remaining options.
            catch (Exception)
            {
                failures.Add(
                    $"The host must have at least {BackFillerArticleRetentionOptions.MinimumPhysicalMemoryGibibytes} GiB ({BackFillerArticleRetentionOptions.MinimumPhysicalMemoryBytes} bytes) of physical RAM, but total physical memory could not be determined.");
                return;
            }

            if (totalPhysicalMemoryBytes < BackFillerArticleRetentionOptions.MinimumPhysicalMemoryBytes)
            {
                failures.Add(
                    $"The host must have at least {BackFillerArticleRetentionOptions.MinimumPhysicalMemoryGibibytes} GiB ({BackFillerArticleRetentionOptions.MinimumPhysicalMemoryBytes} bytes) of physical RAM; detected {totalPhysicalMemoryBytes} bytes.");
                return;
            }

            var maximumPolicyGigabytes = ComputePhysicalMemoryRetentionCeilingGigabytes(totalPhysicalMemoryBytes);
            if (retention.MaximumRetainedPayloadGigabytes > maximumPolicyGigabytes)
            {
                failures.Add(
                    $"BackFiller:ArticleRetention:MaximumRetainedPayloadGigabytes exceeds the allowed 80% physical-memory ceiling ({maximumPolicyGigabytes} GiB).");
            }

            var maxSupportedGigabytes = (int)Math.Min(int.MaxValue, long.MaxValue / BackFillerArticleRetentionOptions.BytesPerGibibyte);
            if (retention.MaximumRetainedPayloadGigabytes > maxSupportedGigabytes)
            {
                failures.Add($"BackFiller:ArticleRetention:MaximumRetainedPayloadGigabytes must be less than or equal to {maxSupportedGigabytes}.");
            }
        }

        /// <summary>
        /// Truncates 80% of <paramref name="totalPhysicalMemoryBytes"/> down to whole gibibytes with no floor.
        /// </summary>
        /// <param name="totalPhysicalMemoryBytes">Detected total physical RAM in bytes.</param>
        /// <returns>Maximum allowed <see cref="BackFillerArticleRetentionOptions.MaximumRetainedPayloadGigabytes"/>.</returns>
        internal static int ComputePhysicalMemoryRetentionCeilingGigabytes(long totalPhysicalMemoryBytes) =>
            (int)(totalPhysicalMemoryBytes * BackFillerArticleRetentionOptions.PhysicalMemoryCeilingRatio
                  / BackFillerArticleRetentionOptions.BytesPerGibibyte);

        /// <summary>
        /// Appends a failure when the RabbitMQ shutdown drain is longer than the BackFiller grace period.
        /// </summary>
        /// <param name="options">Options whose grace period is read from <see cref="BackFillerOptions.Shutdown"/>.</param>
        /// <param name="rabbitMq">Top-level RabbitMQ options.</param>
        /// <param name="failures">Failure list to append. Not cleared.</param>
        /// <exception cref="ArgumentNullException"><paramref name="rabbitMq"/> is null.</exception>
        /// <remarks>
        /// No failure is added when grace is not positive, or when
        /// <see cref="RabbitMqOptions.MaximumShutdownDrainTimeoutSeconds"/> has no value.
        /// </remarks>
        private static void ValidateRabbitMqDrainAgainstGrace(
            BackFillerOptions options,
            RabbitMqOptions rabbitMq,
            List<string> failures)
        {
            ArgumentNullException.ThrowIfNull(rabbitMq);

            var grace = options.Shutdown.GracePeriodSeconds;
            if (grace > 0
                && rabbitMq.MaximumShutdownDrainTimeoutSeconds is { } drain
                && drain > grace)
            {
                failures.Add(
                    "RabbitMQ:MaximumShutdownDrainTimeoutSeconds must be less than or equal to BackFiller:Shutdown:GracePeriodSeconds.");
            }
        }

    }
}
