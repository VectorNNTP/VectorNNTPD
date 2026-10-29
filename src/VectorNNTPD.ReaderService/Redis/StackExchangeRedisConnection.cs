using StackExchange.Redis;

namespace VectorNNTPD.ReaderService.Redis
{
    /// <summary>Owns one <see cref="ConnectionMultiplexer"/> for the process.</summary>
    internal sealed class StackExchangeRedisConnection : IRedisConnection
    {
        private readonly ConnectionMultiplexer _multiplexer;
        private readonly StackExchangeRedisDatabase _database;
        private int _disposed;

        /// <summary>Initializes a new instance of the <see cref="StackExchangeRedisConnection"/> class.</summary>
        public StackExchangeRedisConnection(ConnectionMultiplexer multiplexer)
        {
            ArgumentNullException.ThrowIfNull(multiplexer);
            _multiplexer = multiplexer;
            _database = new StackExchangeRedisDatabase(multiplexer.GetDatabase());
        }

        /// <summary>Gets the shared multiplexer (tests).</summary>
        internal ConnectionMultiplexer Multiplexer => _multiplexer;

        /// <inheritdoc />
        public IRedisDatabase GetDatabase() => _database;

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await _multiplexer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
