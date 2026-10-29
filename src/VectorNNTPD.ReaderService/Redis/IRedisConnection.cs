namespace VectorNNTPD.ReaderService.Redis
{
    /// <summary>Long-lived Redis connection owned by <see cref="IRedisService"/>.</summary>
    public interface IRedisConnection : IAsyncDisposable
    {
        /// <summary>Gets the shared logical database for this connection.</summary>
        IRedisDatabase GetDatabase();
    }
}
