using System.Collections.Concurrent;
using System.Text;
using StackExchange.Redis;
using VectorNNTPD.TransitService.Redis;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Tests.Fixtures;

/// <summary>
/// Opt-in live TransitPeerState Redis seam. Reuses
/// <c>VECTORNNTP_REDIS_INTEGRATION</c> and never reads <c>Redis:Host</c>.
/// </summary>
internal static class TransitPeerStateRedisIntegration
{
    /// <summary>Dedicated Redis endpoint for live Transit Lua tests (<c>host:port</c>).</summary>
    public const string EndpointEnvironmentVariable = SessionStateRedisIntegration.EndpointEnvironmentVariable;

    /// <summary>Authorized TransitPeerState integration-test Redis.</summary>
    public const string AuthorizedEndpoint = SessionStateRedisIntegration.AuthorizedEndpoint;

    /// <summary>Reason used when the live Redis tests are not configured.</summary>
    public const string SkipReason =
        "Set VECTORNNTP_REDIS_INTEGRATION=198.18.0.70:6379 to run TransitPeerState Lua "
        + "against the dedicated test Redis. Ordinary tests do not open Redis and do "
        + "not use Redis:Host.";

    /// <summary>Parses the opt-in endpoint, or <see langword="null"/> when unset.</summary>
    public static VectorNNTPD.TransitService.Configuration.RedisOptions? TryGetOptions() =>
        SessionStateRedisIntegration.TryGetOptions();
}

/// <summary>
/// Shared live Redis connection for TransitPeerState Lua EVAL tests. Deletes only
/// keys created for uniquely named test peer identifiers.
/// </summary>
public sealed class TransitPeerStateRedisIntegrationFixture : IAsyncLifetime
{
    private readonly ConcurrentBag<string> _identifiers = [];
    private IRedisConnection? _connection;
    private IDatabase? _database;

    public bool IsConfigured { get; private set; }

    public string Endpoint { get; private set; } = string.Empty;

    public string ServerVersion { get; private set; } = string.Empty;

    internal RedisTransitPeerStateStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var options = TransitPeerStateRedisIntegration.TryGetOptions();
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
        Store = new RedisTransitPeerStateStore(new LiveRedisService(_connection.GetDatabase()));
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            foreach (var identifier in _identifiers)
            {
                await DeleteKeyAsync(identifier);
            }

            var remaining = await RemainingTestKeysAsync();
            if (remaining.Count > 0)
            {
                throw new InvalidOperationException(
                    "TransitPeerState live Redis cleanup left keys: " + string.Join(", ", remaining));
            }
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    public async Task<string> CreateIdentifierAsync()
    {
        EnsureConfigured();
        var identifier = "vnntp.tconn.lua." + Guid.NewGuid().ToString("N");
        _identifiers.Add(identifier);
        await DeleteKeyAsync(identifier);
        return identifier;
    }

    public async Task DeleteKeyAsync(string identifier)
    {
        EnsureConfigured();
        _ = await Database.KeyDeleteAsync((RedisKey)TransitPeerStateKeys.Create(identifier));
    }

    public async Task<IReadOnlyList<string>> RemainingTestKeysAsync()
    {
        EnsureConfigured();
        var remaining = new List<string>();
        // codeql[cs/linq/missed-where] Async KeyExists filter; Where cannot await.
        foreach (var identifier in _identifiers.Distinct(StringComparer.Ordinal))
        {
            if (await Database.KeyExistsAsync((RedisKey)TransitPeerStateKeys.Create(identifier)))
            {
                remaining.Add(Encoding.UTF8.GetString(TransitPeerStateKeys.Create(identifier)));
            }
        }

        return remaining;
    }

    public ValueTask<TransitPeerStateAdmitResult> TryAdmitAsync(
        string identifier,
        string ownerId,
        int maxIncoming,
        long generation,
        long nowUnixMs,
        long leaseMs) =>
        Store.TryAdmitAsync(
            identifier,
            ownerId,
            maxIncoming,
            generation,
            DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs),
            TimeSpan.FromMilliseconds(leaseMs));

    public ValueTask ReleaseAsync(string identifier, string ownerId, long generation) =>
        Store.ReleaseAsync(identifier, ownerId, generation);

    public ValueTask<TransitPeerStateRenewStatus> RenewAsync(
        string identifier,
        string ownerId,
        long generation,
        long nowUnixMs,
        long leaseMs) =>
        Store.RenewAsync(
            identifier,
            ownerId,
            generation,
            DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs),
            TimeSpan.FromMilliseconds(leaseMs));

    public ValueTask ReleaseOwnerAsync(string identifier, string ownerId) =>
        Store.ReleaseOwnerAsync(identifier, ownerId);

    public async Task SeedAsync(string identifier, string ownerId, long expiryUnixMs, long generation, int count)
    {
        EnsureConfigured();
        _ = await Database.HashSetAsync(
            (RedisKey)TransitPeerStateKeys.Create(identifier),
            ownerId,
            TransitPeerStateKeys.Value(expiryUnixMs, generation, count));
    }

    public async Task<Ownership?> ReadAsync(string identifier, string ownerId)
    {
        EnsureConfigured();
        var value = await Database.HashGetAsync((RedisKey)TransitPeerStateKeys.Create(identifier), ownerId);
        return Parse(value);
    }

    public async Task<int> CountAsync(string identifier)
    {
        EnsureConfigured();
        var entries = await Database.HashGetAllAsync((RedisKey)TransitPeerStateKeys.Create(identifier));
        var total = 0;
        // codeql[cs/linq/missed-where] Parse pattern-match; avoid double Parse from Where+body.
        foreach (var entry in entries)
        {
            if (Parse(entry.Value) is { } ownership)
            {
                total += ownership.Count;
            }
        }

        return total;
    }

    public async Task<TimeSpan?> KeyTimeToLiveAsync(string identifier)
    {
        EnsureConfigured();
        return await Database.KeyTimeToLiveAsync((RedisKey)TransitPeerStateKeys.Create(identifier));
    }

    public async Task<int> FieldCountAsync(string identifier)
    {
        EnsureConfigured();
        return (int)await Database.HashLengthAsync((RedisKey)TransitPeerStateKeys.Create(identifier));
    }

    private IDatabase Database =>
        _database ?? throw new InvalidOperationException("Live Redis fixture is not connected.");

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(TransitPeerStateRedisIntegration.SkipReason);
        }
    }

    private static Ownership? Parse(RedisValue value)
    {
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        return TransitPeerStateKeys.TrySplitValue((string)value!, out var expiry, out var generation, out var count)
            ? new Ownership(expiry, generation, count)
            : null;
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

/// <summary>
/// xUnit 2 discovery-time skip unless <c>VECTORNNTP_REDIS_INTEGRATION</c> is set.
/// </summary>
public sealed class TransitPeerStateRedisIntegrationFactAttribute : FactAttribute
{
    /// <summary>Initializes the attribute and skips when the opt-in endpoint is absent.</summary>
    public TransitPeerStateRedisIntegrationFactAttribute()
    {
        if (TransitPeerStateRedisIntegration.TryGetOptions() is null)
        {
            Skip = TransitPeerStateRedisIntegration.SkipReason;
        }
    }
}
