using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Moderation;
using VectorNNTPD.ReaderService.NntpDb;

namespace VectorNNTPD.ReaderService.Tests.Fixtures;

/// <summary>
/// Seeds uniquely prefixed <c>nntpmoderators</c> rows on a live MySQL target and
/// deletes only those rows. Unconfigured runs do not open a connection.
/// </summary>
public sealed class MySqlModeratorIntegrationFixture : IAsyncLifetime
{
    private readonly List<long> _insertedIds = [];
    private NntpDbService? _nntpDb;

    public bool IsConfigured { get; private set; }

    public string RunId { get; } = Guid.NewGuid().ToString("N");

    public string PatternPrefix => "test.vnntp.modcat." + RunId;

    public MySqlNntpModeratorRepository? Repository { get; private set; }

    public SeededModeratorRow Static { get; private set; } = null!;

    public SeededModeratorRow Template { get; private set; } = null!;

    public SeededModeratorRow Disabled { get; private set; } = null!;

    public SeededModeratorRow SharedFirst { get; private set; } = null!;

    public SeededModeratorRow SharedSecond { get; private set; } = null!;

    public SeededModeratorRow Boundary { get; private set; } = null!;

    public string SharedPattern => PatternPrefix + ".shared.*";

    public string LongAccountName { get; } = "ABCDEFGHIJKLMNOPQRSTUVWXYZ012345";

    public string LongModeratorAddress { get; } = new string('a', 308) + "@example.org";

    public async Task InitializeAsync()
    {
        var connectionString = NntpDbIntegration.TryGetConnectionString();
        if (connectionString is null)
        {
            return;
        }

        IsConfigured = true;
        var options = Options.Create(NntpDbIntegration.CreateOptions(connectionString));
        _nntpDb = new NntpDbService(
            new MySqlNntpDbConnectionFactory(),
            options,
            NullLogger<NntpDbService>.Instance);
        await _nntpDb.StartAsync(CancellationToken.None);
        Repository = new MySqlNntpModeratorRepository(
            _nntpDb,
            NullLogger<MySqlNntpModeratorRepository>.Instance);

        Static = await InsertAsync(
            connectionString,
            PatternPrefix + ".static.*",
            "moderator@example.org",
            "TESTMOD01",
            enabled: true);
        Template = await InsertAsync(
            connectionString,
            PatternPrefix + ".template.*",
            "moderator-%s@example.org",
            "TESTMOD02",
            enabled: true);
        Disabled = await InsertAsync(
            connectionString,
            PatternPrefix + ".disabled.*",
            "disabled@example.org",
            "TESTMOD03",
            enabled: false);
        SharedFirst = await InsertAsync(
            connectionString,
            SharedPattern,
            "shared-first@example.org",
            "TESTMOD04",
            enabled: true);
        SharedSecond = await InsertAsync(
            connectionString,
            SharedPattern,
            "shared-second@example.org",
            "TESTMOD05",
            enabled: true);
        Boundary = await InsertAsync(
            connectionString,
            CreateBoundaryPattern(),
            LongModeratorAddress,
            LongAccountName,
            enabled: true);
    }

    public async Task DisposeAsync()
    {
        var connectionString = NntpDbIntegration.TryGetConnectionString();
        if (connectionString is not null && _insertedIds.Count > 0)
        {
            await CleanupAsync(connectionString).ConfigureAwait(false);
        }

        if (_nntpDb is not null)
        {
            await _nntpDb.DisposeAsync().ConfigureAwait(false);
        }
    }

    public IReadOnlyList<NntpModeratorRow> Ours(IReadOnlyList<NntpModeratorRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Where(row => row.GroupPattern.StartsWith(PatternPrefix, StringComparison.Ordinal)).ToArray();
    }

    private string CreateBoundaryPattern()
    {
        var prefix = PatternPrefix + ".long.";
        var remaining = 255 - prefix.Length - 1;
        return prefix + new string('x', remaining) + "*";
    }

    private async Task<SeededModeratorRow> InsertAsync(
        string connectionString,
        string pattern,
        string address,
        string account,
        bool enabled)
    {
        await using var connection = MySqlNntpDbConnectionFactory.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO nntpmoderators "
            + "(group_pattern, moderator_address, account_name, is_enabled) "
            + "VALUES (@pattern, @address, @account, @enabled); "
            + "SELECT LAST_INSERT_ID();";
        command.Parameters.AddWithValue("@pattern", pattern);
        command.Parameters.AddWithValue("@address", address);
        command.Parameters.AddWithValue("@account", account);
        command.Parameters.AddWithValue("@enabled", enabled ? "Y" : "N");
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        _insertedIds.Add(id);
        return new SeededModeratorRow(id, pattern, address, account, enabled);
    }

    private async Task CleanupAsync(string connectionString)
    {
        await using var connection = MySqlNntpDbConnectionFactory.CreateConnection(connectionString);
        await connection.OpenAsync();
        if (_insertedIds.Count > 0)
        {
            await using var byId = connection.CreateCommand();
            var names = new string[_insertedIds.Count];
            for (var i = 0; i < _insertedIds.Count; i++)
            {
                names[i] = "@id" + i.ToString(CultureInfo.InvariantCulture);
                byId.Parameters.AddWithValue(names[i], _insertedIds[i]);
            }

            byId.CommandText = "DELETE FROM nntpmoderators WHERE moderator_id IN (" + string.Join(", ", names) + ")";
            await byId.ExecuteNonQueryAsync();
        }

        await using var byPrefix = connection.CreateCommand();
        byPrefix.CommandText = "DELETE FROM nntpmoderators WHERE group_pattern LIKE @prefix";
        byPrefix.Parameters.AddWithValue("@prefix", PatternPrefix + "%");
        await byPrefix.ExecuteNonQueryAsync();
    }
}

/// <summary>One row inserted by <see cref="MySqlModeratorIntegrationFixture"/>.</summary>
public sealed record SeededModeratorRow(
    long ModeratorId,
    string GroupPattern,
    string ModeratorAddress,
    string AccountName,
    bool Enabled);
