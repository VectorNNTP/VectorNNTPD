using System.Collections.Concurrent;
using System.Text;
using StackExchange.Redis;
using VectorNNTPD.TransitService.PostFilter.Quota;
using VectorNNTPD.TransitService.Redis;

namespace VectorNNTPD.TransitService.Tests.Fixtures;

/// <summary>
/// Shared live Redis connection for PostFilter quota Lua EVAL tests. Deletes only
/// keys created for uniquely named test accounts. Reuses
/// <see cref="SessionStateRedisIntegration"/> opt-in.
/// </summary>
public sealed class PostFilterQuotaRedisIntegrationFixture : IAsyncLifetime
{
    private readonly ConcurrentBag<string> _accounts = [];
    private IRedisConnection? _connection;
    private IDatabase? _database;

    public bool IsConfigured { get; private set; }

    internal RedisPostFilterQuotaStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var options = SessionStateRedisIntegration.TryGetOptions();
        if (options is null)
        {
            return;
        }

        IsConfigured = true;
        _connection = await new StackExchangeRedisConnectionFactory()
            .ConnectAsync(options, CancellationToken.None);
        var live = (StackExchangeRedisConnection)_connection;
        _database = live.Multiplexer.GetDatabase();
        Store = new RedisPostFilterQuotaStore(new LiveRedisService(_connection.GetDatabase()));
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            foreach (var account in _accounts)
            {
                await DeleteKeysAsync(account);
            }

            var remaining = await RemainingTestKeysAsync();
            if (remaining.Count > 0)
            {
                throw new InvalidOperationException(
                    "PostFilter quota live Redis cleanup left keys: " + string.Join(", ", remaining));
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
        var account = "vnntp.pf.q.lua." + Guid.NewGuid().ToString("N");
        _accounts.Add(account);
        await DeleteKeysAsync(account);
        return account;
    }

    public async Task DeleteKeysAsync(string accountName)
    {
        EnsureConfigured();
        _ = await Database.KeyDeleteAsync((RedisKey)PostFilterQuotaKeys.CreateQuota(accountName));
        _ = await Database.KeyDeleteAsync((RedisKey)PostFilterQuotaKeys.CreateMultipost(accountName));
    }

    public async Task<IReadOnlyList<string>> RemainingTestKeysAsync()
    {
        EnsureConfigured();
        var remaining = new List<string>();
        foreach (var account in _accounts.Distinct(StringComparer.Ordinal))
        {
            if (await Database.KeyExistsAsync((RedisKey)PostFilterQuotaKeys.CreateQuota(account)))
            {
                remaining.Add(Encoding.UTF8.GetString(PostFilterQuotaKeys.CreateQuota(account)));
            }

            if (await Database.KeyExistsAsync((RedisKey)PostFilterQuotaKeys.CreateMultipost(account)))
            {
                remaining.Add(Encoding.UTF8.GetString(PostFilterQuotaKeys.CreateMultipost(account)));
            }
        }

        return remaining;
    }

    public async Task SeedQuotaAsync(string accountName, string field, string value)
    {
        EnsureConfigured();
        _ = await Database.HashSetAsync((RedisKey)PostFilterQuotaKeys.CreateQuota(accountName), field, value);
    }

    public async Task<string?> ReadQuotaFieldAsync(string accountName, string field)
    {
        EnsureConfigured();
        var value = await Database.HashGetAsync((RedisKey)PostFilterQuotaKeys.CreateQuota(accountName), field);
        return value.IsNullOrEmpty ? null : (string)value!;
    }

    public async Task<string?> ReadMultipostFieldAsync(string accountName, string field)
    {
        EnsureConfigured();
        var value = await Database.HashGetAsync((RedisKey)PostFilterQuotaKeys.CreateMultipost(accountName), field);
        return value.IsNullOrEmpty ? null : (string)value!;
    }

    public async Task<int> QuotaFieldCountAsync(string accountName)
    {
        EnsureConfigured();
        return (int)await Database.HashLengthAsync((RedisKey)PostFilterQuotaKeys.CreateQuota(accountName));
    }

    public async Task<TimeSpan?> QuotaKeyTtlAsync(string accountName)
    {
        EnsureConfigured();
        return await Database.KeyTimeToLiveAsync((RedisKey)PostFilterQuotaKeys.CreateQuota(accountName));
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
