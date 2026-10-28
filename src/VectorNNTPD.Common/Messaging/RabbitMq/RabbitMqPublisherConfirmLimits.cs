namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>
    /// Shared outstanding publisher-confirm bound for confirm-tracked publish channels.
    /// </summary>
    /// <remarks>
    /// RabbitMQ.Client 7.2.2 documents a default <c>ThrottlingRateLimiter</c> limit of 128 for
    /// outstanding publisher confirmations, but the two-argument <c>CreateChannelOptions</c>
    /// constructor leaves the limiter <see langword="null"/> (disabling client-side outstanding
    /// throttling). Production publish channels set an explicit limiter to this value so app-level
    /// and client-level bounds stay aligned.
    /// </remarks>
    internal static class RabbitMqPublisherConfirmLimits
    {
        /// <summary>
        /// Maximum outstanding confirmation-tracked publications on one confirm-enabled channel.
        /// </summary>
        internal const int MaxOutstandingConfirmations = 128;
    }
}
