namespace VectorNNTPD.ReaderService.Redis
{
    /// <summary>
    /// Application Redis infrastructure: one shared connection and generic key operations.
    /// </summary>
    /// <remarks>
    /// Does not contain HistoryDB or CHECK policy. Consumers compose keys and TTLs themselves.
    /// </remarks>
    public interface IRedisService
    {
        /// <summary>Gets the shared Redis database after a successful start.</summary>
        IRedisDatabase Database { get; }

        /// <summary>
        /// Gets whether Redis is in the cooldown window after a failure. CHECK uses
        /// <see cref="TryBeginOperation"/> rather than this property so a recovery probe can proceed.
        /// </summary>
        bool IsUnavailable { get; }

        /// <summary>
        /// Attempts to start a Redis operation. Returns <see langword="false"/> during cooldown
        /// or when a recovery probe is already in flight.
        /// </summary>
        /// <param name="isRecoveryProbe">
        /// Set when this caller is the single recovery probe after cooldown.
        /// Only that probe may mark the circuit healthy.
        /// </param>
        bool TryBeginOperation(out bool isRecoveryProbe);

        /// <summary>Completes an operation started by <see cref="TryBeginOperation"/>.</summary>
        void CompleteOperation(bool isRecoveryProbe, bool succeeded, Exception? exception = null);

        /// <summary>
        /// Releases a <see cref="TryBeginOperation"/> grant without treating the attempt as a Redis failure.
        /// Used for caller/session cancellation.
        /// </summary>
        void AbandonOperation(bool isRecoveryProbe);

        /// <summary>Returns whether <paramref name="key"/> exists.</summary>
        ValueTask<bool> KeyExistsAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

        /// <summary>Returns the value of <paramref name="key"/>, or <see langword="null"/> when missing.</summary>
        ValueTask<byte[]?> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

        /// <summary>Sets <paramref name="key"/> to <paramref name="value"/> with a Redis-native TTL.</summary>
        ValueTask SetAsync(
            ReadOnlyMemory<byte> key,
            ReadOnlyMemory<byte> value,
            TimeSpan expiry,
            CancellationToken cancellationToken = default);
    }
}
