namespace VectorNNTPD.BackFillerService.Configuration
{
    /// <summary>
    /// Immutable validated runtime snapshot consumed by application services.
    /// </summary>
    /// <remarks>
    /// Produced once after successful validation. Services must not re-read
    /// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> for these values.
    /// Never log a complete instance: <see cref="NntpDbRuntimeOptions.ConnectionString"/> is secret.
    /// RabbitMQ broker connectivity settings live on Common <c>RabbitMqOptions</c> /
    /// <c>RabbitMqService</c>; this snapshot only carries Article Work application knobs.
    /// </remarks>
    /// <param name="ServerId">Validated BackFiller server id.</param>
    /// <param name="DnsSuffix">Canonical DNS suffix.</param>
    /// <param name="Fqdn">Generated FQDN taken from the bindable options.</param>
    /// <param name="BindAddressTokens">Trimmed non-empty bind tokens, including wildcards.</param>
    /// <param name="BindPortTls">TLS listener port.</param>
    /// <param name="LogDirectory">Resolved file-log directory, or empty when the file target directory is blank.</param>
    /// <param name="CertificateDirectory">Resolved ACME state directory.</param>
    /// <param name="Shutdown">Validated shutdown policy.</param>
    /// <param name="Listener">Validated listener bounds.</param>
    /// <param name="ArticleRetention">Validated retention policy.</param>
    /// <param name="RabbitMq">Article Work RabbitMQ knobs projected from Common options.</param>
    /// <param name="CertificateDomainNames">Certificate DNS identities for <paramref name="Fqdn"/>.</param>
    /// <param name="NntpDb">NntpDB connection projection. <see cref="NntpDbRuntimeOptions.ConnectionString"/> is secret.</param>
    /// <param name="AccountRefreshInterval">MySQL account-refresh poll interval.</param>
    /// <param name="Nntp">Validated outbound NNTP provider session bounds.</param>
    internal sealed record BackFillerRuntimeOptions(
        int ServerId,
        string DnsSuffix,
        string Fqdn,
        IReadOnlyList<string> BindAddressTokens,
        int BindPortTls,
        string LogDirectory,
        string CertificateDirectory,
        BackFillerShutdownRuntimeOptions Shutdown,
        BackFillerListenerRuntimeOptions Listener,
        BackFillerArticleRetentionRuntimeOptions ArticleRetention,
        BackFillerRabbitMqRuntimeOptions RabbitMq,
        IReadOnlyList<string> CertificateDomainNames,
        NntpDbRuntimeOptions NntpDb,
        TimeSpan AccountRefreshInterval,
        BackFillerNntpRuntimeOptions Nntp);

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
    internal sealed record BackFillerListenerRuntimeOptions(
        TimeSpan TlsHandshakeTimeout,
        TimeSpan IoProgressTimeout,
        int MaxQueuedFoundPayloadBytes,
        int MaxActiveConnections);

    /// <summary>Validated outbound NNTP provider session bounds.</summary>
    /// <param name="MaxConcurrentSessionEstablishments">
    /// Application-wide limit on concurrent provider-session establishment (TCP through Ready).
    /// Does not cap per-provider steady-state <c>MaxSessions</c>.
    /// </param>
    internal sealed record BackFillerNntpRuntimeOptions(int MaxConcurrentSessionEstablishments);

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
