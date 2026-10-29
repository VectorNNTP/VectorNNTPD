using StackExchange.Redis;
using VectorNNTPD.TransitService.Configuration;

namespace VectorNNTPD.TransitService.Redis
{
    /// <summary>Creates one <see cref="ConnectionMultiplexer"/> from <see cref="RedisOptions"/>.</summary>
    public sealed class StackExchangeRedisConnectionFactory : IRedisConnectionFactory
    {
        /// <inheritdoc />
        public async Task<IRedisConnection> ConnectAsync(RedisOptions options, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            cancellationToken.ThrowIfCancellationRequested();

            var configuration = new ConfigurationOptions
            {
                AbortOnConnectFail = true,
                ConnectRetry = 2,
                ConnectTimeout = 5000,
            };

            foreach (var host in options.Host)
            {
                configuration.EndPoints.Add(host.Trim(), options.Port);
            }

            ConnectionMultiplexer multiplexer;
            try
            {
                multiplexer = await ConnectionMultiplexer.ConnectAsync(configuration).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is RedisConnectionException or RedisException or TimeoutException)
            {
                throw new RedisUnavailableException("Redis ConnectAsync failed.", ex);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await multiplexer.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new StackExchangeRedisConnection(multiplexer);
        }
    }
}
