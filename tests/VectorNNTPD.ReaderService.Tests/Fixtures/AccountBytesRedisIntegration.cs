using System.Collections.Concurrent;
using System.Text;
using StackExchange.Redis;
using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.ReaderService.Redis;
using VectorNNTPD.ReaderService.Tests.SessionState;

namespace VectorNNTPD.ReaderService.Tests.Fixtures;

/// <summary>
/// Shared live Redis connection for AccountBytes Lua EVAL tests. Deletes only
/// <c>nntpd:bytes:</c> keys created for uniquely named test accounts.
/// </summary>
public sealed class AccountBytesRedisIntegrationFixture : IAsyncLifetime
{
    private readonly ConcurrentBag<string> _accounts = [];
    private IRedisConnection? _connection;
    private IDatabase? _database;

    public bool IsConfigured { get; private set; }

    public string Endpoint { get; private set; } = string.Empty;

    public string ServerVersion { get; private set; } = string.Empty;

    internal RedisAccountByteStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var options = SessionStateRedisIntegration.TryGetOptions();
        if (options is null)
        {
            return;
        }

        IsConfigured = true;
        Endpoint = SessionStateRedisIntegration.DescribeTarget(options);
        _connection = await new StackExchangeRedisConnectionFactory()
            .ConnectAsync(options, CancellationToken.None);
        var live = (StackExchangeRedisConnection)_connection;
        _database = live.Multiplexer.GetDatabase();
        var endpoint = live.Multiplexer.GetEndPoints()[0];
        ServerVersion = live.Multiplexer.GetServer(endpoint).Version.ToString();
        Store = new RedisAccountByteStore(new LiveRedisService(_connection.GetDatabase()));
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            foreach (var account in _accounts)
            {
                await DeleteKeyAsync(account);
            }

            var remaining = await RemainingTestKeysAsync();
            if (remaining.Count > 0)
            {
                throw new InvalidOperationException(
                    "AccountBytes live Redis cleanup left keys: " + string.Join(", ", remaining));
            }
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    public async Task<string> CreateAccountAsync()
    {
        EnsureConfigured();
        var account = "vnntp.bytes.lua." + Guid.NewGuid().ToString("N");
        _accounts.Add(account);
        await DeleteKeyAsync(account);
        return account;
    }

    public async Task DeleteKeyAsync(string accountName)
    {
        EnsureConfigured();
        _ = await Database.KeyDeleteAsync((RedisKey)AccountByteKeys.Create(accountName));
    }

    public async Task<IReadOnlyList<string>> RemainingTestKeysAsync()
    {
        EnsureConfigured();
        var remaining = new List<string>();
        // codeql[cs/linq/missed-where] Async KeyExists filter; Where cannot await.
        foreach (var account in _accounts.Distinct(StringComparer.Ordinal))
        {
            if (await Database.KeyExistsAsync((RedisKey)AccountByteKeys.Create(account)))
            {
                remaining.Add(Encoding.UTF8.GetString(AccountByteKeys.Create(account)));
            }
        }

        return remaining;
    }

    public ValueTask<long?> ApplyAsync(string accountName, string batchId, long consumed, long mysqlRemainingAfter) =>
        Store.ApplyAsync(accountName, batchId, consumed, mysqlRemainingAfter);

    public ValueTask<long?> ApplyAsync(string accountName, long consumed, long mysqlRemainingAfter) =>
        Store.ApplyAsync(accountName, AccountByteBatchId.Create(), consumed, mysqlRemainingAfter);

    public ValueTask<long?> ObserveAsync(string accountName) => Store.ObserveAsync(accountName);

    public ValueTask<long?> DeleteAsync(string accountName) => Store.DeleteAsync(accountName);

    public async Task SeedAsync(string accountName, long remaining)
    {
        EnsureConfigured();
        _ = await Database.HashSetAsync(
            (RedisKey)AccountByteKeys.Create(accountName),
            "remaining",
            remaining.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<bool> BatchMarkExistsAsync(string accountName, string batchId)
    {
        EnsureConfigured();
        return await Database.HashExistsAsync(
            (RedisKey)AccountByteKeys.Create(accountName),
            AccountByteBatchId.FieldName(batchId));
    }

    public async Task<RedisType> KeyTypeAsync(string accountName)
    {
        EnsureConfigured();
        return await Database.KeyTypeAsync((RedisKey)AccountByteKeys.Create(accountName));
    }

    public async Task<TimeSpan?> KeyTimeToLiveAsync(string accountName)
    {
        EnsureConfigured();
        return await Database.KeyTimeToLiveAsync((RedisKey)AccountByteKeys.Create(accountName));
    }

    public async Task DeleteWithoutFixtureTrackingAsync(string accountName)
    {
        EnsureConfigured();
        _ = await Database.KeyDeleteAsync((RedisKey)AccountByteKeys.Create(accountName));
    }

    private IDatabase Database =>
        _database ?? throw new InvalidOperationException("Live Redis fixture is not connected.");

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(SessionStateRedisIntegration.SkipReason);
        }
    }

    private sealed class LiveRedisService : IRedisService
    {
        private readonly IRedisDatabase _database;

        public LiveRedisService(IRedisDatabase database)
        {
            _database = database;
        }

        public IRedisDatabase Database => _database;

        public bool IsUnavailable => false;

        public bool TryBeginOperation(out bool isRecoveryProbe)
        {
            isRecoveryProbe = false;
            return true;
        }

        public void CompleteOperation(bool isRecoveryProbe, bool succeeded, Exception? exception = null)
        {
        }

        public void AbandonOperation(bool isRecoveryProbe)
        {
        }

        public ValueTask<bool> KeyExistsAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
            _database.KeyExistsAsync(key, cancellationToken);

        public ValueTask<byte[]?> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
            _database.GetAsync(key, cancellationToken);

        public ValueTask SetAsync(
            ReadOnlyMemory<byte> key,
            ReadOnlyMemory<byte> value,
            TimeSpan expiry,
            CancellationToken cancellationToken = default) =>
            _database.SetAsync(key, value, expiry, cancellationToken);
    }
}
