using VectorNNTPD.ReaderService.Moderation;

namespace VectorNNTPD.ReaderService.Tests.TestDoubles;

/// <summary>In-memory <see cref="INntpModeratorRepository"/> for catalogue tests.</summary>
internal sealed class MemoryNntpModeratorRepository : INntpModeratorRepository
{
    private readonly List<MemoryModeratorRecord> _rows = [];

    public int QueryCount { get; private set; }

    public Exception? Exception { get; set; }

    public void Add(MemoryModeratorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _rows.Add(record);
    }

    public ValueTask<IReadOnlyList<NntpModeratorRow>> GetEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QueryCount++;
        if (Exception is not null)
        {
            throw Exception;
        }

        var enabled = _rows
            .Where(static row => row.Enabled)
            .OrderBy(static row => row.ModeratorId)
            .Select(static row => new NntpModeratorRow(
                row.ModeratorId,
                row.GroupPattern,
                row.ModeratorAddress,
                row.AccountName))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<NntpModeratorRow>>(enabled);
    }
}

/// <summary>One in-memory moderator row, including disabled rows for filter tests.</summary>
internal sealed class MemoryModeratorRecord
{
    public long ModeratorId { get; init; }

    public string GroupPattern { get; init; } = string.Empty;

    public string ModeratorAddress { get; init; } = string.Empty;

    public string AccountName { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;
}

/// <summary>Builds a <see cref="ModeratorSnapshot"/> from test mappings.</summary>
internal static class ModeratorTestSnapshot
{
    public static ModeratorSnapshot Create(params (string Pattern, string Address, string Username)[] mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var rows = new NntpModeratorRow[mappings.Length];
        for (var i = 0; i < mappings.Length; i++)
        {
            rows[i] = new NntpModeratorRow(
                i + 1,
                mappings[i].Pattern,
                mappings[i].Address,
                mappings[i].Username);
        }

        return ModeratorSnapshot.Create(rows);
    }
}
