using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using StackExchange.Redis;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Redis;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.SessionState.BytesAccounting;

namespace VectorNNTPD.ReaderService.Tests.Fixtures;

/// <summary>
/// Opt-in live SessionState Redis seam. Ordinary tests never read <c>Redis:Host</c>
/// or open the test Redis.
/// </summary>
internal static class SessionStateRedisIntegration
{
    /// <summary>Dedicated Redis endpoint for live SessionState Lua tests (<c>host:port</c>).</summary>
    public const string EndpointEnvironmentVariable = "VECTORNNTP_REDIS_INTEGRATION";

    /// <summary>Authorized SessionState integration-test Redis.</summary>
    public const string AuthorizedEndpoint = "198.18.0.70:6379";

    /// <summary>Reason used when the live Redis tests are not configured.</summary>
    public const string SkipReason =
        "Set VECTORNNTP_REDIS_INTEGRATION=198.18.0.70:6379 to run SessionState Lua "
        + "against the dedicated test Redis. Ordinary tests do not open Redis and do "
        + "not use Redis:Host.";

    /// <summary>Parses the opt-in endpoint, or <see langword="null"/> when unset.</summary>
    public static RedisOptions? TryGetOptions()
    {
        var value = Environment.GetEnvironmentVariable(EndpointEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var endpoint = value.Trim();
        var port = RedisOptions.DefaultPort;
        var host = endpoint;
        var separator = endpoint.LastIndexOf(':');
        if (separator > 0
            && separator < endpoint.Length - 1
            && int.TryParse(endpoint.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed is > 0 and <= 65535)
        {
            host = endpoint[..separator];
            port = parsed;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        return new RedisOptions { Host = [host], Port = port };
    }

    /// <summary>Describes the target without inventing extra connection material.</summary>
    public static string DescribeTarget(RedisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Host[0] + ":" + options.Port.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Shared live Redis connection for SessionState Lua EVAL tests. Deletes only
/// keys created for uniquely named test accounts.
/// </summary>
public sealed class SessionStateRedisIntegrationFixture : IAsyncLifetime
{
    private readonly ConcurrentBag<string> _accounts = [];
    private IRedisConnection? _connection;
    private IDatabase? _database;

    public bool IsConfigured { get; private set; }

    public string Endpoint { get; private set; } = string.Empty;

    public string ServerVersion { get; private set; } = string.Empty;

    internal RedisSessionStateStore Store { get; private set; } = null!;

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
        Store = new RedisSessionStateStore(new LiveRedisService(_connection.GetDatabase()));
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
                    "SessionState live Redis cleanup left keys: " + string.Join(", ", remaining));
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
        var account = "vnntp.sess.lua." + Guid.NewGuid().ToString("N");
        _accounts.Add(account);
        await DeleteKeysAsync(account);
        return account;
    }

    public async Task DeleteKeysAsync(string accountName)
    {
        EnsureConfigured();
        _ = await Database.KeyDeleteAsync((RedisKey)SessionStateKeys.CreateSession(accountName));
        _ = await Database.KeyDeleteAsync((RedisKey)SessionStateKeys.CreateSource(accountName));
        _ = await Database.KeyDeleteAsync((RedisKey)AccountByteKeys.Create(accountName));
    }

    public async Task<IReadOnlyList<string>> RemainingTestKeysAsync()
    {
        EnsureConfigured();
        var remaining = new List<string>();
        foreach (var account in _accounts.Distinct(StringComparer.Ordinal))
        {
            if (await Database.KeyExistsAsync((RedisKey)SessionStateKeys.CreateSession(account)))
            {
                remaining.Add(Encoding.UTF8.GetString(SessionStateKeys.CreateSession(account)));
            }

            if (await Database.KeyExistsAsync((RedisKey)SessionStateKeys.CreateSource(account)))
            {
                remaining.Add(Encoding.UTF8.GetString(SessionStateKeys.CreateSource(account)));
            }

            if (await Database.KeyExistsAsync((RedisKey)AccountByteKeys.Create(account)))
            {
                remaining.Add(Encoding.UTF8.GetString(AccountByteKeys.Create(account)));
            }
        }

        return remaining;
    }

    public ValueTask<SessionStateAdmitResult> TryAdmitAsync(
        string accountName,
        string ip,
        string ownerId,
        int sessionLimit,
        int srcIpLimit,
        long sessionGeneration,
        long sourceGeneration,
        long nowUnixMs,
        long leaseMs,
        bool trackSessions = false) =>
        Store.TryAdmitAsync(
            accountName,
            ip,
            ownerId,
            sessionLimit,
            srcIpLimit,
            sessionGeneration,
            sourceGeneration,
            DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs),
            TimeSpan.FromMilliseconds(leaseMs),
            trackSessions: trackSessions);

    public ValueTask<int> ReleaseAsync(
        string accountName,
        string ip,
        string ownerId,
        long sessionGeneration,
        long sourceGeneration) =>
        Store.ReleaseAsync(accountName, ip, ownerId, sessionGeneration, sourceGeneration);

    public ValueTask<SessionStateRenewResult> RenewAsync(
        string accountName,
        string ownerId,
        long sessionGeneration,
        IReadOnlyList<(string Ip, long Generation)> sources,
        long nowUnixMs,
        long leaseMs) =>
        Store.RenewAsync(
            accountName,
            ownerId,
            sessionGeneration,
            sources,
            DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs),
            TimeSpan.FromMilliseconds(leaseMs));

    public ValueTask ReleaseOwnerAsync(string accountName, string ownerId) =>
        Store.ReleaseOwnerAsync(accountName, ownerId);

    public async Task SeedAsync(string accountName, string field, long expiryUnixMs, long generation, int count, bool session)
    {
        EnsureConfigured();
        var key = session ? SessionStateKeys.CreateSession(accountName) : SessionStateKeys.CreateSource(accountName);
        _ = await Database.HashSetAsync(
            (RedisKey)key,
            field,
            SessionStateKeys.Value(expiryUnixMs, generation, count));
    }

    public Task SeedSessionAsync(string accountName, string ownerId, long expiryUnixMs, long generation, int count) =>
        SeedAsync(accountName, ownerId, expiryUnixMs, generation, count, session: true);

    public Task SeedSourceAsync(string accountName, string ip, string ownerId, long expiryUnixMs, long generation, int count) =>
        SeedAsync(accountName, SessionStateKeys.SourceField(ip, ownerId), expiryUnixMs, generation, count, session: false);

    public async Task<Ownership?> ReadSessionAsync(string accountName, string ownerId)
    {
        EnsureConfigured();
        var value = await Database.HashGetAsync((RedisKey)SessionStateKeys.CreateSession(accountName), ownerId);
        return Parse(value);
    }

    public async Task<Ownership?> ReadSourceAsync(string accountName, string ip, string ownerId)
    {
        EnsureConfigured();
        var value = await Database.HashGetAsync(
            (RedisKey)SessionStateKeys.CreateSource(accountName),
            SessionStateKeys.SourceField(ip, ownerId));
        return Parse(value);
    }

    public async Task<int> SessionCountAsync(string accountName)
    {
        var fields = await HashValuesAsync(SessionStateKeys.CreateSession(accountName));
        var total = 0;
        // codeql[cs/linq/missed-where] Parse pattern-match; avoid double Parse from Where+body.
        foreach (var value in fields)
        {
            if (Parse(value) is { } ownership)
            {
                total += ownership.Count;
            }
        }

        return total;
    }

    public async Task<int> DistinctSourceCountAsync(string accountName)
    {
        var names = await HashFieldNamesAsync(SessionStateKeys.CreateSource(accountName));
        var ips = new HashSet<string>(StringComparer.Ordinal);
        // codeql[cs/linq/missed-where] TrySplitField out-params; foreach preserves parse-and-collect.
        foreach (var field in names)
        {
            if (SessionStateKeys.TrySplitField(field, out var ip, out _))
            {
                ips.Add(ip);
            }
        }

        return ips.Count;
    }

    public async Task<TimeSpan?> KeyTimeToLiveAsync(string accountName, bool session)
    {
        EnsureConfigured();
        var key = session ? SessionStateKeys.CreateSession(accountName) : SessionStateKeys.CreateSource(accountName);
        return await Database.KeyTimeToLiveAsync((RedisKey)key);
    }

    public static string Format(string ip) => SourceAddressIdentity.Format(IPAddress.Parse(ip));

    private IDatabase Database =>
        _database ?? throw new InvalidOperationException("Live Redis fixture is not connected.");

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(SessionStateRedisIntegration.SkipReason);
        }
    }

    private async Task<RedisValue[]> HashValuesAsync(byte[] key)
    {
        EnsureConfigured();
        var entries = await Database.HashGetAllAsync((RedisKey)key);
        return [.. entries.Select(static entry => entry.Value)];
    }

    private async Task<string[]> HashFieldNamesAsync(byte[] key)
    {
        EnsureConfigured();
        var entries = await Database.HashGetAllAsync((RedisKey)key);
        return [.. entries.Select(static entry => (string)entry.Name!)];
    }

    private static Ownership? Parse(RedisValue value)
    {
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        return SessionStateKeys.TrySplitValue((string)value!, out var expiry, out var generation, out var count)
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

/// <summary>Parsed SessionState HASH value.</summary>
public readonly record struct Ownership(long ExpiryUnixMs, long Generation, int Count);
