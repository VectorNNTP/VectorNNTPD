namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>
    /// Process-wide RabbitMQ connection surface. Does not own topology, publishers, or consumers.
    /// </summary>
    /// <remarks>
    /// After a successful start, a lost connection is a degraded runtime state. The service
    /// reconnects indefinitely until a usable connection is restored or NNTPD shuts down.
    /// Callers must use <see cref="TryGetCurrent"/> for each use; they must not retain
    /// ownership or dispose the connection.
    /// </remarks>
    public interface IRabbitMqService
    {
        /// <summary>Gets a value indicating whether the current broker connection is open and usable.</summary>
        bool IsReady { get; }

        /// <summary>
        /// Gets the monotonic generation of the current connection, or zero before the first successful connect.
        /// </summary>
        long ConnectionGeneration { get; }

        /// <summary>
        /// Raised after a connection generation becomes current.
        /// </summary>
        /// <remarks>
        /// The first successful connect has <see cref="RabbitMqConnectionReplacedEventArgs.IsReplacement"/>
        /// set to <see langword="false"/>. Later generations set it to <see langword="true"/> so callers
        /// can drop work tied to the previous connection.
        /// A subscriber exception is logged and isolated: it does not fail connect or reconnect, does not
        /// retire the published generation, and does not prevent later subscribers from receiving the event.
        /// </remarks>
        event EventHandler<RabbitMqConnectionReplacedEventArgs>? ConnectionReplaced;

        /// <summary>
        /// Attempts to capture a non-owning snapshot of the current authoritative connection.
        /// </summary>
        /// <param name="handle">
        /// When the method returns <see langword="true"/>, a generation snapshot. The handle does
        /// not pin the connection. After loss, replacement, or shutdown,
        /// <see cref="RabbitMqConnectionHandle.IsCurrent"/> is <see langword="false"/> and the
        /// captured instance may already be disposed or about to be disposed.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when an open connection is published and the service is not
        /// stopping; otherwise <see langword="false"/>.
        /// </returns>
        bool TryGetCurrent(out RabbitMqConnectionHandle handle);
    }
}
