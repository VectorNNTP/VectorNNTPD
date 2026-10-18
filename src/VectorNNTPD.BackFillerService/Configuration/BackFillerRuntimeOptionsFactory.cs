using MySqlConnector;
using VectorNNTPD.BackFillerService.Acme;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Acme;

namespace VectorNNTPD.BackFillerService.Configuration
{
    /// <summary>
    /// Builds <see cref="BackFillerRuntimeOptions"/> from already-validated bindable options.
    /// </summary>
    internal static class BackFillerRuntimeOptionsFactory
    {
        /// <summary>
        /// Projects validated options into the immutable runtime snapshot.
        /// </summary>
        /// <param name="options">Validated bindable options.</param>
        /// <param name="nntpDb">Validated shared NntpDB options.</param>
        /// <param name="contentRootPath">
        /// Application binary directory used to resolve relative
        /// <c>BackFiller:Logging:File:LogDir</c> and certificate paths.
        /// Production hosting passes <see cref="AppContext.BaseDirectory"/>. When omitted,
        /// relative paths resolve against <see cref="AppContext.BaseDirectory"/> rather than
        /// the process working directory or an IDE project content root.
        /// </param>
        /// <returns>Immutable snapshot.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is null. Other null arguments fail in the five-argument overload.</exception>
        /// <exception cref="InvalidOperationException">Thrown when a required value is missing after validation.</exception>
        /// <remarks>
        /// The three-argument overload maps leftover BackFiller bind/certificate fields only when
        /// a shared <see cref="AcmeCloudflareOptions"/> instance is not supplied (tests).
        /// RabbitMQ defaults are used when <see cref="RabbitMqOptions"/> is omitted.
        /// The synthesized ACME options leave the certificate password, Cloudflare API key, and zone id empty,
        /// and set <see cref="AcmeCloudflareOptions.IncludeNewsHostnameInCertificate"/> to <see langword="false"/>.
        /// An empty bind-address list becomes a single <c>*</c> token.
        /// </remarks>
        internal static BackFillerRuntimeOptions Create(
            BackFillerOptions options,
            NntpDbOptions nntpDb,
            string? contentRootPath = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            var acme = new AcmeCloudflareOptions
            {
                BindAddress = options.BindAddress is { Length: > 0 } ? options.BindAddress : ["*"],
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
            return Create(options, nntpDb, acme, new RabbitMqOptions(), contentRootPath);
        }

        /// <summary>
        /// Projects validated options and a caller-supplied ACME snapshot, using a new <see cref="RabbitMqOptions"/> for Article Work knobs.
        /// </summary>
        /// <param name="options">Validated bindable options.</param>
        /// <param name="nntpDb">Validated shared NntpDB options.</param>
        /// <param name="acme">Shared ACME and bind snapshot. Its bind port, addresses, state directory, and news-hostname flag are projected.</param>
        /// <param name="contentRootPath">
        /// Application base directory for relative log and certificate paths.
        /// Null or white space resolves against <see cref="AppContext.BaseDirectory"/>.
        /// </param>
        /// <returns>Immutable snapshot.</returns>
        /// <remarks>Null arguments fail in the five-argument overload. RabbitMQ null-coalescing is documented there.</remarks>
        internal static BackFillerRuntimeOptions Create(
            BackFillerOptions options,
            NntpDbOptions nntpDb,
            AcmeCloudflareOptions acme,
            string? contentRootPath = null) =>
            Create(options, nntpDb, acme, new RabbitMqOptions(), contentRootPath);

        /// <summary>
        /// Projects validated options, ACME settings, and RabbitMQ Article Work knobs into the immutable runtime snapshot.
        /// </summary>
        /// <param name="options">Validated bindable options. Server id, FQDN, DNS suffix, logging, retention, listener, shutdown, and account-refresh interval are read.</param>
        /// <param name="nntpDb">Validated shared NntpDB options.</param>
        /// <param name="acme">Bind addresses, TLS port, ACME state directory, and news-hostname flag.</param>
        /// <param name="rabbitMq">Article Work payload, confirm-timeout, and prefetch knobs.</param>
        /// <param name="contentRootPath">
        /// Application base directory for relative log and certificate paths.
        /// Null or white space resolves against <see cref="AppContext.BaseDirectory"/>.
        /// </param>
        /// <returns>Immutable snapshot.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="options"/>, <paramref name="nntpDb"/>, <paramref name="acme"/>, or <paramref name="rabbitMq"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <see cref="BackFillerOptions.DnsSuffix"/> or <see cref="AcmeCloudflareOptions.AcmeStateDir"/> is null or white space.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Server id or FQDN is missing, <see cref="AcmeCloudflareOptions.BindPortTls"/> is outside 1–65535,
        /// the NntpDB connection string is empty, or retention, listener, or shutdown options are null.
        /// </exception>
        /// <exception cref="OverflowException">
        /// <see cref="BackFillerArticleRetentionOptions.MaximumRetainedPayloadGigabytes"/> times
        /// <see cref="BackFillerArticleRetentionOptions.BytesPerGibibyte"/> overflows <see cref="long"/>.
        /// </exception>
        /// <remarks>
        /// The token list keeps every trimmed non-empty bind token, including wildcards and tokens that are not IP addresses.
        /// A null RabbitMQ payload limit becomes 1024 bytes and a null confirm timeout becomes 10 seconds.
        /// Prefetch is copied as supplied, including null. The NntpDB connection string is parsed with
        /// <see cref="MySqlConnectionStringBuilder"/>; a string it cannot parse fails this call.
        /// </remarks>
        internal static BackFillerRuntimeOptions Create(
            BackFillerOptions options,
            NntpDbOptions nntpDb,
            AcmeCloudflareOptions acme,
            RabbitMqOptions rabbitMq,
            string? contentRootPath = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(nntpDb);
            ArgumentNullException.ThrowIfNull(acme);
            ArgumentNullException.ThrowIfNull(rabbitMq);

            if (options.ServerId is not { } serverId || string.IsNullOrWhiteSpace(options.Fqdn))
            {
                throw new InvalidOperationException("Validated BackFiller identity is required to build runtime options.");
            }

            var dnsSuffix = ApplicationFqdn.CanonicalizeDnsSuffix(options.DnsSuffix);
            var fqdn = options.Fqdn;
            if (acme.BindPortTls is < 1 or > 65535)
            {
                throw new InvalidOperationException(
                    "BindPortTls is required and must be 1–65535 because BackFiller is TLS-only. There is no cleartext fallback.");
            }

            var bindPortTls = acme.BindPortTls;

            var tokens = (acme.BindAddress ?? [])
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Select(static x => x.Trim())
                .ToArray();

            if (string.IsNullOrWhiteSpace(nntpDb.ConnectionString))
            {
                throw new InvalidOperationException(
                    $"ConnectionStrings:{NntpDbOptions.ConnectionStringName} must be configured.");
            }

            var nntpDbBuilder = new MySqlConnectionStringBuilder(nntpDb.ConnectionString);

            var retention = options.ArticleRetention ?? throw new InvalidOperationException("BackFiller:ArticleRetention is required.");
            var listener = options.Listener ?? throw new InvalidOperationException("BackFiller:Listener is required.");
            var shutdown = options.Shutdown ?? throw new InvalidOperationException("BackFiller:Shutdown is required.");
            var nntp = options.Nntp ?? throw new InvalidOperationException("BackFiller:Nntp is required.");

            return new BackFillerRuntimeOptions(
                ServerId: serverId,
                DnsSuffix: dnsSuffix,
                Fqdn: fqdn,
                BindAddressTokens: tokens,
                BindPortTls: bindPortTls,
                LogDirectory: ResolveFileLogDirectory(options, contentRootPath),
                CertificateDirectory: ApplicationLocalPath.ResolveApplicationLocalPath(acme.AcmeStateDir, contentRootPath),
                Shutdown: new BackFillerShutdownRuntimeOptions(
                    TimeSpan.FromSeconds(shutdown.GracePeriodSeconds),
                    shutdown.DrainQueuedWork,
                    shutdown.FinishActiveArticles),
                Listener: new BackFillerListenerRuntimeOptions(
                    TimeSpan.FromSeconds(listener.TlsHandshakeTimeoutSeconds),
                    TimeSpan.FromSeconds(listener.IoProgressTimeoutSeconds),
                    listener.MaxQueuedFoundPayloadBytes,
                    listener.MaxActiveConnections,
                    listener.SendBufferBytes,
                    listener.ReceiveBufferBytes),
                ArticleRetention: new BackFillerArticleRetentionRuntimeOptions(
                    checked(retention.MaximumRetainedPayloadGigabytes * BackFillerArticleRetentionOptions.BytesPerGibibyte),
                    TimeSpan.FromSeconds(retention.RetentionTtlSeconds),
                    TimeSpan.FromSeconds(retention.SweepIntervalSeconds),
                    retention.MaxOpenableRequestIdsPerArticle),
                RabbitMq: new BackFillerRabbitMqRuntimeOptions(
                    WorkRequestMaxPayloadBytes: rabbitMq.WorkRequestMaxPayloadBytes ?? 1024,
                    PublishConfirmTimeoutSeconds: rabbitMq.PublishConfirmTimeoutSeconds ?? 10,
                    ConsumerPrefetchCount: rabbitMq.ConsumerPrefetchCount),
                CertificateDomainNames: CertificateIdentities.ForFqdn(fqdn, acme.IncludeNewsHostnameInCertificate),
                NntpDb: new NntpDbRuntimeOptions(
                    nntpDb.ConnectionString.Trim(),
                    nntpDbBuilder.Server ?? string.Empty,
                    nntpDbBuilder.Database ?? string.Empty),
                AccountRefreshInterval: TimeSpan.FromSeconds(options.BackFillerAccountRefreshIntervalSeconds),
                Nntp: new BackFillerNntpRuntimeOptions(
                    nntp.MaxConcurrentSessionEstablishments,
                    nntp.SendBufferBytes,
                    nntp.ReceiveBufferBytes));
        }

        /// <summary>Resolves the file-log directory, or returns empty when that directory is blank.</summary>
        /// <param name="options">Options whose logging file target is read. A null logging section or file target uses <see cref="BackFillerFileLoggingTargetOptions"/> defaults.</param>
        /// <param name="contentRootPath">Base directory forwarded to <see cref="ApplicationLocalPath.ResolveApplicationLocalPath(string, string?)"/>.</param>
        /// <returns>The resolved directory, or <see cref="string.Empty"/> when <see cref="BackFillerFileLoggingTargetOptions.LogDir"/> is white space.</returns>
        /// <remarks>The default file target directory is not treated as blank, so a missing logging section still resolves <c>logs</c>.</remarks>
        private static string ResolveFileLogDirectory(BackFillerOptions options, string? contentRootPath)
        {
            var file = options.Logging?.File ?? new BackFillerFileLoggingTargetOptions();
            if (string.IsNullOrWhiteSpace(file.LogDir))
            {
                return string.Empty;
            }

            return ApplicationLocalPath.ResolveApplicationLocalPath(file.LogDir, contentRootPath);
        }
    }
}
