namespace VectorNNTPD.ReaderService.Redis
{
    /// <summary>
    /// Redis infrastructure failure (unavailable, timeout, or protocol error).
    /// Distinct from a successful reply that the key does not exist.
    /// </summary>
    public sealed class RedisUnavailableException : Exception
    {
        /// <summary>Initializes a new instance of the <see cref="RedisUnavailableException"/> class.</summary>
        public RedisUnavailableException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
