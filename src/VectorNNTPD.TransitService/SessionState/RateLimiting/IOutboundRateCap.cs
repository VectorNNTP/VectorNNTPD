namespace VectorNNTPD.TransitService.SessionState.RateLimiting
{
    /// <summary>Dynamically adjustable outbound bytes-per-second cap.</summary>
    public interface IOutboundRateCap
    {
        /// <summary>
        /// Gets the current cap. <c>0</c> is unlimited. <c>&lt; 0</c> blocks writes
        /// until a later update (not unlimited).
        /// </summary>
        long MaxSendBytesPerSecond { get; }

        /// <summary>Replaces the cap. Existing and future writes observe the new value.</summary>
        void UpdateMaxSendBytesPerSecond(long bytesPerSecond);
    }
}
