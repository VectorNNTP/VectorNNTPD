namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>
    /// One AMQP connection owned by <see cref="RabbitMqService"/>.
    /// </summary>
    /// <remarks>
    /// This surface is connection lifecycle plus caller-owned channel factories.
    /// <see cref="RabbitMqService"/> remains the sole TCP connection owner.
    /// </remarks>
    internal interface IRabbitMqConnection : IAsyncDisposable
    {
        /// <summary>Gets a value indicating whether the broker connection is currently open.</summary>
        bool IsOpen { get; }

        /// <summary>Gets the broker host selected for this connection.</summary>
        string Host { get; }

        /// <summary>Gets the broker port selected for this connection.</summary>
        int Port { get; }

        /// <summary>Gets the virtual host used to establish this connection.</summary>
        string VirtualHost { get; }

        /// <summary>Gets the client-provided name visible in broker diagnostics.</summary>
        string ClientProvidedName { get; }

        /// <summary>
        /// Raised when the broker or peer closes the connection.
        /// </summary>
        /// <remarks>
        /// <see cref="RabbitMqService"/> is the only production subscriber. A lost connection
        /// does not dispose itself; the service decides when the instance is retired.
        /// </remarks>
        event EventHandler<RabbitMqConnectionLostEventArgs>? ConnectionLost;

        /// <summary>
        /// Raised when the broker blocks the connection (for example under resource alarm).
        /// </summary>
        /// <remarks>
        /// <see cref="RabbitMqService"/> is the only production subscriber. The connection remains
        /// open; the service owns the blocked-duration timeout and recovery decision.
        /// </remarks>
        event EventHandler<RabbitMqConnectionBlockedEventArgs>? ConnectionBlocked;

        /// <summary>
        /// Raised when the broker clears a prior block on this connection.
        /// </summary>
        /// <remarks>
        /// <see cref="RabbitMqService"/> is the only production subscriber. Cancels a pending
        /// blocked-duration timeout for the current generation when the sender is still current.
        /// </remarks>
        event EventHandler? ConnectionUnblocked;

        /// <summary>
        /// Opens a short-lived channel that can declare exchanges, queues, and bindings.
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel channel creation.</param>
        /// <returns>A declare-only channel owned by the caller.</returns>
        Task<IRabbitMqTopologyChannel> CreateTopologyChannelAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Opens a caller-owned channel for article-work RPC publish or consume.
        /// </summary>
        /// <param name="generation">Connection generation the channel belongs to.</param>
        /// <param name="cancellationToken">Token used to cancel channel creation.</param>
        /// <returns>An RPC channel owned by the caller. The caller must not dispose the connection.</returns>
        Task<IRabbitMqRpcChannel> CreateRpcChannelAsync(long generation, CancellationToken cancellationToken);

        /// <summary>
        /// Opens a caller-owned channel for topology declaration, manual-ack consume, and settlement.
        /// </summary>
        /// <param name="generation">Connection generation the channel belongs to.</param>
        /// <param name="cancellationToken">Token used to cancel channel creation.</param>
        /// <returns>
        /// A multipurpose channel with publisher confirms disabled. The caller must not dispose the connection.
        /// </returns>
        Task<IRabbitMqManualAckChannel> CreateManualAckChannelAsync(long generation, CancellationToken cancellationToken);

        /// <summary>
        /// Opens a caller-owned confirm-enabled channel for one-way publications.
        /// </summary>
        /// <param name="generation">Connection generation the channel belongs to.</param>
        /// <param name="cancellationToken">Token used to cancel channel creation.</param>
        /// <returns>A publish channel owned by the caller. The caller must not dispose the connection.</returns>
        /// <remarks>
        /// Uses library confirmation tracking so <c>BasicPublishAsync</c> awaits the confirm.
        /// Prefer <see cref="CreateAsyncConfirmPublishChannelAsync"/> for pipelined OverviewDB publishes.
        /// </remarks>
        Task<IRabbitMqPublishChannel> CreatePublishChannelAsync(long generation, CancellationToken cancellationToken);

        /// <summary>
        /// Opens a caller-owned confirm-enabled channel for asynchronous publisher confirms.
        /// </summary>
        /// <param name="generation">Connection generation the channel belongs to.</param>
        /// <param name="cancellationToken">Token used to cancel channel creation.</param>
        /// <returns>
        /// A channel with confirms enabled and library tracking disabled so publishes return
        /// after the write; callers correlate <c>BasicAcksAsync</c>/<c>BasicNacksAsync</c>/
        /// <c>BasicReturnAsync</c> by publish sequence number.
        /// </returns>
        Task<IRabbitMqAsyncConfirmPublishChannel> CreateAsyncConfirmPublishChannelAsync(
            long generation,
            CancellationToken cancellationToken);
    }
}
