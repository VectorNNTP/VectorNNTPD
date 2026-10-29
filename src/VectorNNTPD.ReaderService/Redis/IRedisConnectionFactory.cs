using VectorNNTPD.ReaderService.Configuration;

namespace VectorNNTPD.ReaderService.Redis
{
    /// <summary>Creates the process-wide Redis connection from configuration.</summary>
    public interface IRedisConnectionFactory
    {
        /// <summary>Establishes one long-lived Redis connection for the configured topology.</summary>
        Task<IRedisConnection> ConnectAsync(RedisOptions options, CancellationToken cancellationToken);
    }
}
