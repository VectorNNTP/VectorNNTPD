namespace VectorNNTPD.TransitService.SessionState.RateLimiting
{
    /// <summary>
    /// Process-local account rate allocation. SessionState observes the cluster
    /// session count; this type updates every registered local session cap.
    /// </summary>
    public interface IAccountRateAllocator
    {
        /// <summary>
        /// Registers a connected session. <paramref name="rateBps"/> is
        /// <c>account_rate_limit</c> in bits per second, not Mbps. Applies the last
        /// observed cluster count, or one session when none has been observed yet.
        /// </summary>
        void Register(string accountName, string sessionId, IOutboundRateCap cap, int rateBps);

        /// <summary>Drops a session. Remaining local sessions keep the last observed count.</summary>
        void Unregister(string accountName, string sessionId);

        /// <summary>
        /// Applies the cluster session total to local caps. Existing local sessions
        /// that already hold a positive equal-share cap may only drop to a share
        /// that fits <c>accountBytes − remotes × floor(accountBytes / remotes)</c>.
        /// A newly admitted session on a node that does not own every session is
        /// blocked. Equal shares resume when this node owns every remaining session.
        /// </summary>
        void ObserveClusterSessionCount(string accountName, int clusterSessionCount);

        /// <summary>Returns the current cap for a registered session (tests).</summary>
        long? GetSessionCap(string accountName, string sessionId);
    }

    /// <summary>No-op allocator for hosts and tests that do not attach rate limiters.</summary>
    public sealed class NullAccountRateAllocator : IAccountRateAllocator
    {
        /// <summary>Shared no-op instance.</summary>
        public static NullAccountRateAllocator Instance { get; } = new();

        private NullAccountRateAllocator()
        {
        }

        /// <inheritdoc />
        public void Register(string accountName, string sessionId, IOutboundRateCap cap, int rateBps)
        {
        }

        /// <inheritdoc />
        public void Unregister(string accountName, string sessionId)
        {
        }

        /// <inheritdoc />
        public void ObserveClusterSessionCount(string accountName, int clusterSessionCount)
        {
        }

        /// <inheritdoc />
        public long? GetSessionCap(string accountName, string sessionId) => null;
    }
}
