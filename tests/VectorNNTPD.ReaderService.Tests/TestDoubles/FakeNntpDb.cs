using MySqlConnector;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.ReaderService.Authentication;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Moderation;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.Transit;

namespace VectorNNTPD.ReaderService.Tests.TestDoubles;

/// <summary>In-memory MySQL connection factory for offline NntpDB tests.</summary>
internal sealed class FakeNntpDbConnectionFactory : INntpDbConnectionFactory
{
    private readonly object _sync = new();
    private readonly List<FakeNntpDbConnection> _connections = [];

    public int OpenCount { get; private set; }

    public int OpenAttemptCount { get; private set; }

    public Exception? OpenException { get; set; }

    public int RemainingOpenFailures { get; set; }

    public TaskCompletionSource? BlockOpen { get; set; }

    public TaskCompletionSource? OpenStarted { get; set; }

    public TaskCompletionSource? BlockSelectOne { get; set; }

    public TaskCompletionSource? SelectOneStarted { get; set; }

    public int NextHealthCheckResult { get; set; } = 1;

    public Exception? NextHealthCheckException { get; set; }

    public IReadOnlyList<NntpGroupRow> Newsgroups { get; set; } = [];

    public Exception? QueryNewsgroupsException { get; set; }

    public Dictionary<string, NntpUserRecord> Users { get; } = new(StringComparer.Ordinal);

    public Exception? QueryUserException { get; set; }

    public Exception? ConsumeAccountBytesException { get; set; }

    public IReadOnlyList<NntpModeratorRow> Moderators { get; set; } = [];

    public Exception? QueryModeratorsException { get; set; }

    public PostFilterPolicyRecord? PostFilterPolicy { get; set; } =
        new(1, DateTimeOffset.UtcNow, new PostFilterOptions());

    public Dictionary<long, PostFilterPolicyRecord> PostFilterRevisions { get; } = [];

    public long? PostFilterCurrentRevision { get; set; }

    public Exception? QueryPostFilterPolicyException { get; set; }

    public Exception? InsertPostFilterRejectionException { get; set; }

    public TaskCompletionSource? BlockQueryNewsgroups { get; set; }

    public TaskCompletionSource? QueryNewsgroupsStarted { get; set; }

    public IReadOnlyList<NntpSharedConfigurationCandidate>? SharedConfigurationRows { get; set; }

    public TransitCatalogueRecord? TransitCatalogue { get; set; } = TransitCatalogueRecord.Empty;

    public Exception? SharedConfigurationException { get; set; }

    public IReadOnlyList<FakeNntpDbConnection> Connections
    {
        get
        {
            lock (_sync)
            {
                return _connections.ToArray();
            }
        }
    }

    public int QueryNewsgroupsCount
    {
        get
        {
            lock (_sync)
            {
                var total = 0;
                foreach (var connection in _connections)
                {
                    total += connection.QueryNewsgroupsCount;
                }

                return total;
            }
        }
    }

    public async Task<INntpDbSession> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        OpenAttemptCount++;
        OpenStarted?.TrySetResult();
        if (BlockOpen is not null)
        {
            await BlockOpen.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (RemainingOpenFailures > 0)
        {
            RemainingOpenFailures--;
            throw new NntpDbUnavailableException("down");
        }

        if (OpenException is not null)
        {
            throw OpenException;
        }

        var connection = new FakeNntpDbConnection
        {
            HealthCheckResult = NextHealthCheckResult,
            HealthCheckException = NextHealthCheckException,
            BlockSelectOne = BlockSelectOne,
            SelectOneStarted = SelectOneStarted,
            Newsgroups = Newsgroups,
            QueryNewsgroupsException = QueryNewsgroupsException,
            Users = Users,
            QueryUserException = QueryUserException,
            ConsumeAccountBytesException = ConsumeAccountBytesException,
            Moderators = Moderators,
            QueryModeratorsException = QueryModeratorsException,
            PostFilterPolicy = PostFilterPolicy,
            PostFilterRevisions = PostFilterRevisions,
            PostFilterCurrentRevision = PostFilterCurrentRevision,
            QueryPostFilterPolicyException = QueryPostFilterPolicyException,
            InsertPostFilterRejectionException = InsertPostFilterRejectionException,
            BlockQueryNewsgroups = BlockQueryNewsgroups,
            QueryNewsgroupsStarted = QueryNewsgroupsStarted,
            SharedConfigurationRows = SharedConfigurationRows,
            SharedConfigurationException = SharedConfigurationException,
            TransitCatalogue = TransitCatalogue,
        };
        lock (_sync)
        {
            OpenCount++;
            _connections.Add(connection);
        }

        return connection;
    }
}

/// <summary>In-memory logical MySQL connection with failure injection.</summary>
internal sealed class FakeNntpDbConnection : INntpDbConnection, INntpSharedConfigurationRowSource
{
    public int SelectOneCount { get; private set; }

    public int DisposeCount { get; private set; }

    public int HealthCheckResult { get; set; } = 1;

    public Exception? HealthCheckException { get; set; }

    public TaskCompletionSource? BlockSelectOne { get; set; }

    public TaskCompletionSource? SelectOneStarted { get; set; }

    public IReadOnlyList<NntpGroupRow> Newsgroups { get; set; } = [];

    public Exception? QueryNewsgroupsException { get; set; }

    public Dictionary<string, NntpUserRecord> Users { get; set; } = new(StringComparer.Ordinal);

    public Exception? QueryUserException { get; set; }

    public Exception? ConsumeAccountBytesException { get; set; }

    public int ConsumeAccountBytesCount { get; private set; }

    public int QueryAccountByteRemainingCount { get; private set; }

    public IReadOnlyList<NntpModeratorRow> Moderators { get; set; } = [];

    public Exception? QueryModeratorsException { get; set; }

    public int QueryModeratorsCount { get; private set; }

    public PostFilterPolicyRecord? PostFilterPolicy { get; set; }

    public Dictionary<long, PostFilterPolicyRecord> PostFilterRevisions { get; set; } = [];

    public long? PostFilterCurrentRevision { get; set; }

    public Exception? QueryPostFilterPolicyException { get; set; }

    public int QueryPostFilterPolicyCount { get; private set; }

    public TaskCompletionSource? BlockQueryNewsgroups { get; set; }

    public TaskCompletionSource? QueryNewsgroupsStarted { get; set; }

    public int QueryNewsgroupsCount { get; private set; }

    public int QueryUserCount { get; private set; }

    public async ValueTask<int> SelectOneAsync(CancellationToken cancellationToken)
    {
        SelectOneStarted?.TrySetResult();
        if (BlockSelectOne is not null)
        {
            await BlockSelectOne.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        SelectOneCount++;
        if (HealthCheckException is not null)
        {
            throw HealthCheckException;
        }

        return HealthCheckResult;
    }

    public async ValueTask<IReadOnlyList<NntpGroupRow>> QueryNewsgroupsAsync(CancellationToken cancellationToken)
    {
        QueryNewsgroupsStarted?.TrySetResult();
        if (BlockQueryNewsgroups is not null)
        {
            await BlockQueryNewsgroups.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        QueryNewsgroupsCount++;
        if (QueryNewsgroupsException is not null)
        {
            throw QueryNewsgroupsException;
        }

        return Newsgroups;
    }

    public ValueTask<NntpUserRecord?> QueryUserAccountAsync(string accountName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        cancellationToken.ThrowIfCancellationRequested();
        QueryUserCount++;
        if (QueryUserException is not null)
        {
            throw QueryUserException;
        }

        return Users.TryGetValue(accountName, out var record)
            ? ValueTask.FromResult<NntpUserRecord?>(record)
            : ValueTask.FromResult<NntpUserRecord?>(null);
    }

    public ValueTask<AccountByteConsumeResult> ConsumeAccountBytesAsync(
        string accountName,
        long bytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        ConsumeAccountBytesCount++;
        if (ConsumeAccountBytesException is not null)
        {
            throw ConsumeAccountBytesException;
        }

        lock (Users)
        {
            if (!Users.TryGetValue(accountName, out var record))
            {
                return ValueTask.FromResult(
                    new AccountByteConsumeResult(AccountByteConsumeStatus.AccountNotFound, 0, 0));
            }

            var current = record.ByteLimit < 0 ? 0 : record.ByteLimit;
            var consumed = bytes > current ? current : bytes;
            var remaining = current - consumed;
            Users[accountName] = WithByteLimit(record, remaining);
            return ValueTask.FromResult(
                new AccountByteConsumeResult(AccountByteConsumeStatus.Consumed, remaining, consumed));
        }
    }

    public ValueTask<AccountByteConsumeResult> QueryAccountByteRemainingAsync(
        string accountName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        cancellationToken.ThrowIfCancellationRequested();
        QueryAccountByteRemainingCount++;
        if (QueryUserException is not null)
        {
            throw QueryUserException;
        }

        if (!Users.TryGetValue(accountName, out var record))
        {
            return ValueTask.FromResult(
                new AccountByteConsumeResult(AccountByteConsumeStatus.AccountNotFound, 0, 0));
        }

        var remaining = record.ByteLimit < 0 ? 0 : record.ByteLimit;
        return ValueTask.FromResult(
            new AccountByteConsumeResult(AccountByteConsumeStatus.Consumed, remaining, 0));
    }

    public ValueTask<IReadOnlyList<NntpModeratorRow>> QueryEnabledModeratorsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QueryModeratorsCount++;
        if (QueryModeratorsException is not null)
        {
            throw QueryModeratorsException;
        }

        return ValueTask.FromResult(Moderators);
    }

    public ValueTask<PostFilterPolicyRecord?> QueryPostFilterPolicyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QueryPostFilterPolicyCount++;
        if (QueryPostFilterPolicyException is not null)
        {
            throw QueryPostFilterPolicyException;
        }

        if (PostFilterRevisions.Count > 0)
        {
            if (PostFilterCurrentRevision is not { } revision)
            {
                return ValueTask.FromResult<PostFilterPolicyRecord?>(null);
            }

            return ValueTask.FromResult(
                PostFilterRevisions.TryGetValue(revision, out var versioned) ? versioned : null);
        }

        return ValueTask.FromResult(PostFilterPolicy);
    }

    public TransitCatalogueRecord? TransitCatalogue { get; set; }

    public Exception? QueryTransitCatalogueException { get; set; }

    public ValueTask<TransitCatalogueRecord?> QueryTransitCatalogueAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (QueryTransitCatalogueException is not null)
        {
            throw QueryTransitCatalogueException;
        }

        return ValueTask.FromResult(TransitCatalogue);
    }

    public List<PostFilterRejectionEvidence> Rejections { get; } = [];

    public Exception? InsertPostFilterRejectionException { get; set; }

    public int InsertPostFilterRejectionCount { get; private set; }

    public ValueTask InsertPostFilterRejectionAsync(
        PostFilterRejectionEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();
        InsertPostFilterRejectionCount++;
        if (InsertPostFilterRejectionException is not null)
        {
            throw InsertPostFilterRejectionException;
        }

        Rejections.Add(evidence);
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<NntpSharedConfigurationCandidate>? SharedConfigurationRows { get; set; }

    public Exception? SharedConfigurationException { get; set; }

    public MySqlCommand CreateCommand() =>
        throw new NotSupportedException("Fake NntpDB connections do not create provider commands.");

    public ValueTask<MySqlTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Fake NntpDB connections do not begin transactions.");

    public ValueTask<MySqlTransaction> BeginTransactionAsync(
        System.Data.IsolationLevel isolationLevel,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Fake NntpDB connections do not begin transactions.");

    public ValueTask<IReadOnlyList<NntpSharedConfigurationCandidate>> ReadCandidatesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SharedConfigurationException is not null)
        {
            throw SharedConfigurationException;
        }

        IReadOnlyList<NntpSharedConfigurationCandidate> rows = SharedConfigurationRows
            ?? [new NntpSharedConfigurationCandidate(
                5 * 1024 * 1024,
                "news.usenet.ninja",
                null,
                "https://acme-v02.api.letsencrypt.org/directory",
                14,
                "5811a29d39a0732afb5f160c9b137c3d",
                "usenet.ninja")];
        return ValueTask.FromResult(rows);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }

    private static NntpUserRecord WithByteLimit(NntpUserRecord record, long byteLimit) =>
        new(
            record.AccountName,
            record.AccountPassword,
            record.AllowAuthPlain,
            record.AllowAuthScram256,
            record.ScramSalt,
            record.ScramIterations,
            record.ScramStoredKey,
            record.ScramServerKey,
            record.RateLimitBps,
            byteLimit,
            record.SessionLimit,
            record.SrcIpLimit,
            record.IsEnabled,
            record.CustomerId,
            record.AllowedArtTypes);
}
