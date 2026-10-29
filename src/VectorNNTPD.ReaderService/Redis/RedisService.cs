using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.ReaderService.Redis
{
    /// <summary>
    /// Process-wide Redis infrastructure: one long-lived connection and generic key operations.
    /// </summary>
    public sealed class RedisService : IRedisService, IApplicationService, IAsyncDisposable
    {
        private readonly IRedisConnectionFactory _connectionFactory;
        private readonly IOptions<RedisOptions> _options;
        private readonly ILogger<RedisService> _logger;
        private readonly RedisAvailability _availability;
        private IRedisConnection? _connection;
        private IRedisDatabase? _database;
        private int _started;
        private int _disposed;

        /// <summary>Initializes a new instance of the <see cref="RedisService"/> class.</summary>
        public RedisService(
            IRedisConnectionFactory connectionFactory,
            IOptions<RedisOptions> options,
            ILogger<RedisService> logger)
            : this(connectionFactory, options, logger, TimeProvider.System, RedisAvailability.DefaultCooldown)
        {
        }

        /// <summary>Initializes a new instance with an explicit circuit cooldown (tests).</summary>
        internal RedisService(
            IRedisConnectionFactory connectionFactory,
            IOptions<RedisOptions> options,
            ILogger<RedisService> logger,
            TimeProvider timeProvider,
            TimeSpan circuitCooldown)
        {
            ArgumentNullException.ThrowIfNull(connectionFactory);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(timeProvider);
            _connectionFactory = connectionFactory;
            _options = options;
            _logger = logger;
            _availability = new RedisAvailability(timeProvider, circuitCooldown);
        }

        /// <inheritdoc />
        public string Name => "Redis";

        /// <inheritdoc />
        public Task? Execution => null;

        /// <summary>Gets the number of times a connection was established (tests).</summary>
        internal int ConnectionCount { get; private set; }

        /// <inheritdoc />
        public IRedisDatabase Database =>
            _database ?? throw new InvalidOperationException("Redis has not been started.");

        /// <inheritdoc />
        public bool IsUnavailable => _availability.IsUnavailable;

        /// <inheritdoc />
        public bool TryBeginOperation(out bool isRecoveryProbe) => _availability.TryBegin(out isRecoveryProbe);

        /// <inheritdoc />
        public void CompleteOperation(bool isRecoveryProbe, bool succeeded, Exception? exception = null)
        {
            _availability.Complete(isRecoveryProbe, succeeded, out var becameUnavailable, out var recovered);
            if (becameUnavailable)
            {
                RedisLogMessages.BecameUnavailable(_logger, exception);
            }

            if (recovered)
            {
                RedisLogMessages.Recovered(_logger);
            }
        }

        /// <inheritdoc />
        public void AbandonOperation(bool isRecoveryProbe) => _availability.Abandon(isRecoveryProbe);

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            var options = _options.Value;
            RedisLogMessages.Connecting(_logger, options.Host.Length, options.Port);
            try
            {
                _connection = await _connectionFactory.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
                _database = _connection.GetDatabase();
                var ping = await _database.PingAsync(cancellationToken).ConfigureAwait(false);
                ConnectionCount++;
                RedisLogMessages.Connected(_logger, (long)ping.TotalMilliseconds);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RedisLogMessages.StartupFailed(_logger, ex);
                if (_connection is not null)
                {
                    await _connection.DisposeAsync().ConfigureAwait(false);
                    _connection = null;
                    _database = null;
                }

                Interlocked.Exchange(ref _started, 0);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await DisposeAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public ValueTask<bool> KeyExistsAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
            Database.KeyExistsAsync(key, cancellationToken);

        /// <inheritdoc />
        public ValueTask<byte[]?> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
            Database.GetAsync(key, cancellationToken);

        /// <inheritdoc />
        public ValueTask SetAsync(
            ReadOnlyMemory<byte> key,
            ReadOnlyMemory<byte> value,
            TimeSpan expiry,
            CancellationToken cancellationToken = default) =>
            Database.SetAsync(key, value, expiry, cancellationToken);

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                RedisLogMessages.Disposed(_logger);
            }

            _connection = null;
            _database = null;
        }
    }
}
