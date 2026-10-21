namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>
    /// Top-level RabbitMQ connection and lifecycle options.
    /// </summary>
    /// <remarks>
    /// Property names, types, and defaults follow the established <c>RabbitMQ</c> section.
    /// Recovery after a successful start is indefinite; there is no consecutive-failure
    /// abandon threshold. Reserved properties are still bound and validated for later
    /// topology, channel, or consumer work. Nested <see cref="Management"/> is optional for
    /// connectivity-only hosts; applications that use the Management HTTP API validate it
    /// separately. Never log <see cref="Password"/> or complete option instances that contain
    /// credentials.
    /// </remarks>
    public sealed class RabbitMqOptions
    {
        /// <summary>Configuration section name.</summary>
        internal const string SectionName = "RabbitMQ";

        /// <summary>Configuration key for the broker username.</summary>
        internal const string UsernameConfigurationKey = "Username";

        /// <summary>Configuration key for the broker password secret.</summary>
        internal const string PasswordConfigurationKey = "Password";

        /// <summary>
        /// Environment variable that supplies <see cref="Username"/>
        /// (<c>VECTOR__RABBITMQ__USERNAME</c>).
        /// </summary>
        internal const string UsernameEnvironmentVariable = "VECTOR__RABBITMQ__USERNAME";

        /// <summary>
        /// Environment variable that supplies <see cref="Password"/>
        /// (<c>VECTOR__RABBITMQ__PASSWORD</c>).
        /// </summary>
        internal const string PasswordEnvironmentVariable = "VECTOR__RABBITMQ__PASSWORD";

        /// <summary>
        /// Maximum RabbitMQ work-request envelope size, in bytes, admitted before a future
        /// consumer copies the borrowed broker body.
        /// </summary>
        /// <remarks>
        /// Validated and projected. Not consumed by the connection-lifecycle service in this phase.
        /// </remarks>
        public int? WorkRequestMaxPayloadBytes { get; set; } = 1024;

        /// <summary>
        /// Maximum allowed duration, in seconds, for RabbitMQ operation-timeout coherence validation.
        /// </summary>
        /// <remarks>
        /// Validated and projected. Channel lease revocation is not implemented in this phase.
        /// </remarks>
        public int? ChannelLeaseTimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// RabbitMQ RPC operation timeout, in seconds, used for lease/operation coherence checks
        /// and as the client continuation/handshake timeout.
        /// </summary>
        public int? RpcTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Maximum continuous duration, in seconds, that a broker-blocked RabbitMQ connection may
        /// remain blocked before <see cref="RabbitMqService"/> retires that generation and reconnects.
        /// Not the AMQP handshake timeout; handshake uses <see cref="SocketTimeoutSeconds"/>.
        /// </summary>
        public int? ConnectionBlockedTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// RabbitMQ broker host endpoints used for connection establishment.
        /// </summary>
        public string[]? Hosts { get; set; } = [];

        /// <summary>
        /// RabbitMQ username used for credential-based authentication.
        /// </summary>
        /// <remarks>
        /// NNTPD, BackFiller, and StorageServer publish this from <c>nntpsharedconfig.rabbitmqusername</c>
        /// and discard any bound value. Never commit real values.
        /// </remarks>
        public string? Username { get; set; }

        /// <summary>
        /// RabbitMQ password used for credential-based authentication.
        /// </summary>
        /// <remarks>
        /// Secret. NNTPD, BackFiller, and StorageServer publish this from <c>nntpsharedconfig.rabbitmqpassword</c>
        /// and discard any bound value. Never commit or log.
        /// </remarks>
        public string? Password { get; set; }

        /// <summary>
        /// RabbitMQ virtual host used for namespace isolation.
        /// </summary>
        public string? VirtualHost { get; set; } = "/";

        /// <summary>
        /// Whether RabbitMQ connections use TLS/SSL encryption.
        /// </summary>
        public bool? EnableSsl { get; set; } = true;

        /// <summary>
        /// RabbitMQ AMQP TCP port used when connecting to configured hosts.
        /// </summary>
        public int? Port { get; set; } = 5672;

        /// <summary>
        /// Bounded in-memory delivery buffer capacity used by future RabbitMQ consumer infrastructure.
        /// </summary>
        /// <remarks>
        /// Validated and projected. Not a RabbitMQ channel-object pool in this phase.
        /// </remarks>
        public int? ChannelPoolSize { get; set; } = 512;

        /// <summary>
        /// Configured minimum RabbitMQ connection count target for future pool-scaling policy.
        /// </summary>
        /// <remarks>
        /// Validated and projected. The current service owns a single connection.
        /// </remarks>
        public int? MinConnections { get; set; } = 4;

        /// <summary>
        /// Configured maximum RabbitMQ connection count limit for future pool-scaling policy.
        /// </summary>
        /// <remarks>
        /// Validated and projected. The current service owns a single connection.
        /// </remarks>
        public int? MaxConnections { get; set; } = 16;

        /// <summary>
        /// Maximum pending channel-lease waiter target for future RabbitMQ channel-pool policy.
        /// </summary>
        /// <remarks>Validated and projected. Not enforced in this phase.</remarks>
        public int? MaxPendingLeaseWaiters { get; set; } = 1024;

        /// <summary>
        /// Minimum idle duration, in seconds, for future RabbitMQ connection scale-down policy.
        /// </summary>
        /// <remarks>Validated and projected. Not enforced in this phase.</remarks>
        public int? ConnectionScaleDownIdleSeconds { get; set; } = 300;

        /// <summary>
        /// Cooldown, in seconds, for future RabbitMQ connection scale-down policy.
        /// </summary>
        /// <remarks>Validated and projected. Not enforced in this phase.</remarks>
        public int? ScaleDownCooldownSeconds { get; set; } = 30;

        /// <summary>
        /// Minimum base interval, in seconds, between automatic RabbitMQ network recovery attempts.
        /// </summary>
        public int? NetworkRecoveryIntervalSeconds { get; set; } = 5;

        /// <summary>
        /// Base delay, in milliseconds, used before application-level reconnect attempts.
        /// </summary>
        public int? PoolReconnectBaseDelayMs { get; set; } = 250;

        /// <summary>
        /// Maximum delay, in milliseconds, allowed for application-level reconnect backoff.
        /// </summary>
        public int? PoolReconnectMaxDelayMs { get; set; } = 30000;

        /// <summary>
        /// Minimum healthy RabbitMQ connection lifetime policy, in seconds, for future idle-retirement logic.
        /// </summary>
        /// <remarks>Validated and projected. Not enforced in this phase.</remarks>
        public int? MinimumConnectionLifetimeSeconds { get; set; } = 300;

        /// <summary>
        /// Maximum wait time, in seconds, for RabbitMQ publisher confirmations.
        /// </summary>
        /// <remarks>Used by the OverviewDB ingest handoff publisher.</remarks>
        public int? PublishConfirmTimeoutSeconds { get; set; } = 10;

        /// <summary>
        /// Maximum RabbitMQ shutdown-drain budget, in seconds.
        /// </summary>
        /// <remarks>
        /// Validated and projected. Shutdown is cancellation-driven and does not use this as an internal timer.
        /// </remarks>
        public int? MaximumShutdownDrainTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Degraded-capacity threshold policy for future RabbitMQ health evaluation logic.
        /// </summary>
        /// <remarks>Validated and projected. Not consumed in this phase.</remarks>
        public double? DegradedThreshold { get; set; } = 0.75;

        /// <summary>
        /// Consecutive-unhealthy threshold policy for future RabbitMQ health evaluation logic.
        /// </summary>
        /// <remarks>Validated and projected. Not consumed in this phase.</remarks>
        public int? UnhealthyThreshold { get; set; } = 5;

        /// <summary>
        /// Requested RabbitMQ heartbeat timeout, in seconds, for AMQP connection negotiation.
        /// </summary>
        public int? RequestedHeartbeatSeconds { get; set; } = 60;

        /// <summary>
        /// RabbitMQ socket read/write timeout and AMQP connection-establishment timeout
        /// (<c>RequestedConnectionTimeout</c>), in seconds.
        /// </summary>
        public int? SocketTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Requested RabbitMQ channel limit per connection.
        /// </summary>
        public int? RequestedChannelMax { get; set; } = 2047;

        /// <summary>
        /// Optional RabbitMQ consumer prefetch count used for future Basic.Qos control.
        /// </summary>
        public ushort? ConsumerPrefetchCount { get; set; }

        /// <summary>
        /// Optional AMQP CorrelationId used to gate temporary payload diagnostics.
        /// </summary>
        public string? DiagnosticPayloadCorrelationId { get; set; }

        /// <summary>
        /// RabbitMQ Management HTTP API settings for BackFiller ArticleWork availability discovery.
        /// </summary>
        public RabbitMqManagementOptions Management { get; set; } = new();

        /// <summary>
        /// Projects a validated options snapshot into immutable runtime settings.
        /// </summary>
        /// <returns>The runtime snapshot consumed by the RabbitMQ connection service.</returns>
        /// <exception cref="InvalidOperationException">Thrown when required settings are missing after validation.</exception>
        internal RabbitMqRuntimeOptions ToRuntimeOptions()
        {
            if (Hosts is null || Hosts.Length == 0)
            {
                throw new InvalidOperationException("RabbitMQ Hosts must contain at least one entry.");
            }

            string[] hosts = [.. (Hosts ?? [])
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Select(static x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)];

            var username = !string.IsNullOrWhiteSpace(Username)
                ? Username.Trim()
                : null;

            var virtualHost = !string.IsNullOrWhiteSpace(VirtualHost)
                ? VirtualHost.Trim()
                : "/";

            return new RabbitMqRuntimeOptions(
                Hosts: hosts,
                Port: Port ?? throw Missing(nameof(Port)),
                Username: username,
                Password: Password,
                VirtualHost: virtualHost,
                EnableSsl: EnableSsl ?? throw Missing(nameof(EnableSsl)),
                ChannelLeaseTimeoutSeconds: ChannelLeaseTimeoutSeconds ?? throw Missing(nameof(ChannelLeaseTimeoutSeconds)),
                RpcTimeoutSeconds: RpcTimeoutSeconds ?? throw Missing(nameof(RpcTimeoutSeconds)),
                ConnectionBlockedTimeoutSeconds: ConnectionBlockedTimeoutSeconds ?? throw Missing(nameof(ConnectionBlockedTimeoutSeconds)),
                ChannelPoolSize: ChannelPoolSize ?? throw Missing(nameof(ChannelPoolSize)),
                MinConnections: MinConnections ?? throw Missing(nameof(MinConnections)),
                MaxConnections: MaxConnections ?? throw Missing(nameof(MaxConnections)),
                MaxPendingLeaseWaiters: MaxPendingLeaseWaiters ?? throw Missing(nameof(MaxPendingLeaseWaiters)),
                ConnectionScaleDownIdleSeconds: ConnectionScaleDownIdleSeconds ?? throw Missing(nameof(ConnectionScaleDownIdleSeconds)),
                ScaleDownCooldownSeconds: ScaleDownCooldownSeconds ?? throw Missing(nameof(ScaleDownCooldownSeconds)),
                NetworkRecoveryIntervalSeconds: NetworkRecoveryIntervalSeconds ?? throw Missing(nameof(NetworkRecoveryIntervalSeconds)),
                PoolReconnectBaseDelayMs: PoolReconnectBaseDelayMs ?? throw Missing(nameof(PoolReconnectBaseDelayMs)),
                PoolReconnectMaxDelayMs: PoolReconnectMaxDelayMs ?? throw Missing(nameof(PoolReconnectMaxDelayMs)),
                MinimumConnectionLifetimeSeconds: MinimumConnectionLifetimeSeconds ?? throw Missing(nameof(MinimumConnectionLifetimeSeconds)),
                PublishConfirmTimeoutSeconds: PublishConfirmTimeoutSeconds ?? throw Missing(nameof(PublishConfirmTimeoutSeconds)),
                MaximumShutdownDrainTimeoutSeconds: MaximumShutdownDrainTimeoutSeconds ?? throw Missing(nameof(MaximumShutdownDrainTimeoutSeconds)),
                DegradedThreshold: DegradedThreshold ?? throw Missing(nameof(DegradedThreshold)),
                UnhealthyThreshold: UnhealthyThreshold ?? throw Missing(nameof(UnhealthyThreshold)),
                RequestedHeartbeatSeconds: RequestedHeartbeatSeconds ?? throw Missing(nameof(RequestedHeartbeatSeconds)),
                SocketTimeoutSeconds: SocketTimeoutSeconds ?? throw Missing(nameof(SocketTimeoutSeconds)),
                RequestedChannelMax: RequestedChannelMax ?? throw Missing(nameof(RequestedChannelMax)),
                ConsumerPrefetchCount: ConsumerPrefetchCount,
                DiagnosticPayloadCorrelationId: DiagnosticPayloadCorrelationId,
                WorkRequestMaxPayloadBytes: WorkRequestMaxPayloadBytes ?? 1024);
        }

        /// <summary>Creates the exception thrown when a required option is still null during runtime projection.</summary>
        /// <param name="name">Option property name included in the message. Not a secret value.</param>
        /// <returns>An <see cref="InvalidOperationException"/> stating that the named RabbitMQ option is required.</returns>
        private static InvalidOperationException Missing(string name) =>
            new($"RabbitMQ {name} is required.");
    }
}
