using VectorNNTPD.Common.Articles;
using VectorNNTPD.TransitService.Authentication;

namespace VectorNNTPD.TransitService.Tests.TestDoubles;

/// <summary>In-memory <see cref="INntpUserRecordStore"/> for authentication tests.</summary>
internal sealed class MemoryNntpUserRecordStore : INntpUserRecordStore
{
    private readonly Dictionary<string, NntpUserRecord> _users = new(StringComparer.Ordinal);

    public int LookupCount { get; private set; }

    public Exception? Exception { get; set; }

    public void Add(NntpUserRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _users[record.AccountName] = record;
    }

    public ValueTask<NntpUserRecord?> TryGetUserAsync(string accountName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        cancellationToken.ThrowIfCancellationRequested();
        LookupCount++;
        if (Exception is not null)
        {
            throw Exception;
        }

        return _users.TryGetValue(accountName, out var record)
            ? ValueTask.FromResult<NntpUserRecord?>(record)
            : ValueTask.FromResult<NntpUserRecord?>(null);
    }

    public static NntpUserRecord Create(
        string name,
        string password,
        bool enabled = true,
        bool allowPlain = true,
        bool allowScram = true,
        int sessionLimit = 0,
        int srcIpLimit = 0,
        int rateLimitBps = 0,
        long byteLimit = 1_000_000_000,
        ReadOnlyMemory<byte> scramSalt = default,
        int scramIterations = 0,
        ReadOnlyMemory<byte> scramStoredKey = default,
        ReadOnlyMemory<byte> scramServerKey = default,
        string customerId = "11111111-1111-1111-1111-111111111111",
        ArticleType allowedArtTypes = ArticleTypeCapabilities.All) =>
        new(
            name,
            password,
            allowPlain,
            allowScram,
            scramSalt,
            scramIterations,
            scramStoredKey,
            scramServerKey,
            rateLimitBps,
            byteLimit,
            sessionLimit,
            srcIpLimit,
            enabled,
            customerId,
            allowedArtTypes);
}
