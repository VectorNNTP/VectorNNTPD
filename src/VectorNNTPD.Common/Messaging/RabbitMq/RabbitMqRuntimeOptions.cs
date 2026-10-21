namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>
    /// Immutable RabbitMQ runtime options projected from validated startup configuration.
    /// </summary>
    /// <param name="Hosts">Canonical RabbitMQ broker hosts used for connection establishment.</param>
    /// <param name="Port">RabbitMQ broker port.</param>
    /// <param name="Username">RabbitMQ username used for credential-based authentication.</param>
    /// <param name="Password">RabbitMQ password used for credential-based authentication.</param>
    /// <param name="VirtualHost">RabbitMQ virtual host used for namespace isolation.</param>
    /// <param name="EnableSsl">Whether TLS is enabled for RabbitMQ connectivity.</param>
    /// <param name="ChannelLeaseTimeoutSeconds">Maximum channel lease timeout in seconds.</param>
    /// <param name="RpcTimeoutSeconds">Maximum RabbitMQ RPC timeout in seconds.</param>
    /// <param name="ConnectionBlockedTimeoutSeconds">Maximum continuous broker-blocked duration before generation retirement, in seconds.</param>
    /// <param name="ChannelPoolSize">Configured RabbitMQ channel pool size.</param>
    /// <param name="MinConnections">Configured minimum pooled RabbitMQ connections.</param>
    /// <param name="MaxConnections">Configured maximum pooled RabbitMQ connections.</param>
    /// <param name="MaxPendingLeaseWaiters">Maximum pending channel lease waiters.</param>
    /// <param name="ConnectionScaleDownIdleSeconds">Connection scale-down idle threshold in seconds.</param>
    /// <param name="ScaleDownCooldownSeconds">Connection scale-down cooldown in seconds.</param>
    /// <param name="NetworkRecoveryIntervalSeconds">Automatic network recovery interval in seconds.</param>
    /// <param name="PoolReconnectBaseDelayMs">Base reconnect delay for application-level reconnection in milliseconds.</param>
    /// <param name="PoolReconnectMaxDelayMs">Maximum reconnect delay for application-level reconnection in milliseconds.</param>
    /// <param name="MinimumConnectionLifetimeSeconds">Minimum healthy connection lifetime in seconds.</param>
    /// <param name="PublishConfirmTimeoutSeconds">Publisher confirm timeout in seconds.</param>
    /// <param name="MaximumShutdownDrainTimeoutSeconds">Maximum RabbitMQ shutdown drain timeout in seconds.</param>
    /// <param name="DegradedThreshold">Degraded health threshold ratio.</param>
    /// <param name="UnhealthyThreshold">Consecutive unhealthy evaluations threshold.</param>
    /// <param name="RequestedHeartbeatSeconds">Requested AMQP heartbeat in seconds.</param>
    /// <param name="SocketTimeoutSeconds">Socket read/write and AMQP connection-establishment timeout in seconds.</param>
    /// <param name="RequestedChannelMax">Requested channel max for AMQP negotiation.</param>
    /// <param name="ConsumerPrefetchCount">Optional RabbitMQ consumer prefetch count.</param>
    /// <param name="DiagnosticPayloadCorrelationId">Optional AMQP CorrelationId used to gate temporary payload diagnostics.</param>
    /// <param name="WorkRequestMaxPayloadBytes">Maximum RabbitMQ work-request envelope size, in bytes.</param>
    internal sealed record RabbitMqRuntimeOptions(
        IReadOnlyList<string> Hosts,
        int Port,
        string? Username,
        string? Password,
        string VirtualHost,
        bool EnableSsl,
        int ChannelLeaseTimeoutSeconds,
        int RpcTimeoutSeconds,
        int ConnectionBlockedTimeoutSeconds,
        int ChannelPoolSize,
        int MinConnections,
        int MaxConnections,
        int MaxPendingLeaseWaiters,
        int ConnectionScaleDownIdleSeconds,
        int ScaleDownCooldownSeconds,
        int NetworkRecoveryIntervalSeconds,
        int PoolReconnectBaseDelayMs,
        int PoolReconnectMaxDelayMs,
        int MinimumConnectionLifetimeSeconds,
        int PublishConfirmTimeoutSeconds,
        int MaximumShutdownDrainTimeoutSeconds,
        double DegradedThreshold,
        int UnhealthyThreshold,
        int RequestedHeartbeatSeconds,
        int SocketTimeoutSeconds,
        int RequestedChannelMax,
        ushort? ConsumerPrefetchCount,
        string? DiagnosticPayloadCorrelationId = null,
        int WorkRequestMaxPayloadBytes = 1024)
    {
        /// <summary>
        /// Builds a client-provided connection name from an application prefix and FQDN.
        /// </summary>
        /// <param name="applicationPrefix">Product/application prefix (for example <c>VectorNNTP.NNTPD</c>).</param>
        /// <param name="fqdn">Canonical host FQDN segment appended after a colon.</param>
        /// <returns>Deterministic connection name in the form <c>{applicationPrefix}:{fqdn}</c>.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="applicationPrefix"/> or <paramref name="fqdn"/> is <see langword="null"/>, empty, or whitespace.
        /// </exception>
        internal static string GetDefaultConnectionName(string applicationPrefix, string fqdn)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(applicationPrefix);
            ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
            return $"{applicationPrefix}:{fqdn}";
        }
    }
}
