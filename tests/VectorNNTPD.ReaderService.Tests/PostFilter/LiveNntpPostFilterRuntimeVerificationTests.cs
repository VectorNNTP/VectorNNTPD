using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.PostFilter.Quota;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

/// <summary>
/// Live-runtime PostFilter verification against the real <c>nntp</c> database.
/// </summary>
/// <remarks>
/// Requires <c>VECTORNNTP_NNTPDB_INTEGRATION</c> with <c>Database=nntp</c>.
/// Any other database name fails rather than running against a disposable
/// <c>nntpgroups_pfval_*</c> target. Loads revision 2 through
/// <see cref="MySqlPostFilterPolicyRepository"/> and
/// <see cref="PostFilterPolicyService"/>; does not publish, update, delete,
/// or roll back PostFilter policy. The only permitted write is append-only
/// rejection evidence from the real POST/evidence path.
/// </remarks>
public sealed class LiveNntpPostFilterRuntimeVerificationTests
{
    private const string DeploymentMessageId = "<vectortest-postfilter-deployment@localhost>";
    private const string Gtube =
        "XJS*C4JDBQADN1.NSBN3*2IDNEN*GTUBE-STANDARD-ANTI-UBE-TEST-EMAIL*C.34X";

    [NntpDbIntegrationFact]
    public async Task LiveNntp_Revision2_IsLoadedAndEnforcedOnPost()
    {
        var connectionString = RequireLiveNntp();
        await AssertLiveRevisionUnchangedAsync(connectionString);
        var deploymentBefore = await ReadEvidenceAsync(connectionString, DeploymentMessageId);
        Assert.NotNull(deploymentBefore);
        Assert.Equal(2L, deploymentBefore.Revision);
        var rejectionCountBefore = await CountRejectionsAsync(connectionString);

        var leftover = ValidateProductionAppsettingsHasNoPostFilterSection();
        Assert.True(leftover.Succeeded, leftover.FailureMessage);

        var policyLog = new CollectingLogger<PostFilterPolicyService>();
        await using var nntpDb = new NntpDbService(
            new MySqlNntpDbConnectionFactory(),
            Options.Create(NntpDbIntegration.CreateOptions(connectionString)),
            NullLogger<NntpDbService>.Instance);
        await nntpDb.StartAsync(CancellationToken.None);
        var repository = new MySqlPostFilterPolicyRepository(
            nntpDb,
            NullLogger<MySqlPostFilterPolicyRepository>.Instance);
        var loaded = await repository.LoadAsync();
        Assert.Equal(2, loaded.Revision);
        Assert.Equal(PostFilterGateState.Active, loaded.Options.Gate);
        Assert.True(loaded.Options.SpamAssassin.Enabled);
        Assert.Equal(PostFilterSpamOnFailure.Reject, loaded.Options.SpamAssassin.OnFailure);
        Assert.Equal(131072, loaded.Options.SpamAssassin.MaxArticleSize);
        Assert.Equal(["198.18.0.70"], loaded.Options.SpamAssassin.Hosts);
        Assert.Equal(["YEncoded"], loaded.Options.SpamAssassin.ExcludeArtTypes);

        await using var policy = new PostFilterPolicyService(repository, policyLog);
        await policy.StartAsync(CancellationToken.None);
        Assert.Equal(2, policy.Current.Revision);
        Assert.Equal(PostFilterGateState.Active, policy.Current.Gate);
        Assert.True(policy.Current.SpamAssassinEnabled);
        Assert.Equal(PostFilterSpamOnFailure.Reject, policy.Current.SpamAssassinOnFailure);
        Assert.Equal(131072, policy.Current.SpamAssassinMaxArticleSize);
        Assert.Equal(["198.18.0.70"], policy.Current.SpamAssassinHosts);
        Assert.Equal(ArticleType.YEncoded, policy.Current.SpamAssassinExcludeArtTypes);
        Assert.Contains(
            policyLog.Messages,
            static message => message.Contains("revision=2", StringComparison.Ordinal)
                && message.Contains("gate=Active", StringComparison.Ordinal)
                && message.Contains("spamAssassin=True", StringComparison.Ordinal));
        Assert.DoesNotContain(
            policyLog.Messages,
            static message => message.Contains("Nntpd:PostFilter", StringComparison.Ordinal)
                || message.Contains("appsettings", StringComparison.OrdinalIgnoreCase)
                || message.Contains("fallback", StringComparison.OrdinalIgnoreCase));

        var queue = new PostFilterRejectionEvidenceQueue();
        var writer = new PostFilterRejectionEvidenceService(
            nntpDb,
            queue,
            NullLogger<PostFilterRejectionEvidenceService>.Instance);
        await writer.StartAsync(CancellationToken.None);
        await using var spamd = new SpamdCheckClient();
        var filter = new PostFilterEvaluator(
            policy,
            new InMemoryPostFilterQuotaStore(),
            new PostFilterReservationIdentity("runtime-verify", "inc"),
            spamd,
            NullLogger<PostFilterEvaluator>.Instance);

        var rejectToken = "vectortest-postfilter-runtime-reject-" + Guid.NewGuid().ToString("N");
        var rejectId = "<" + rejectToken + "@example.com>";
        await using (var duplex = new PostFilterPostDuplex())
        {
            var session = duplex.CreateSession(
                PostFilterPostHarness.NewQueue(),
                filter,
                clientAddress: IPAddress.Parse("198.18.0.70"),
                postFilterEvidence: queue);
            await PostFilterPostHarness.PostAsync(
                duplex,
                session,
                "441 Posting failed",
                article: PostFilterPostHarness.TextArticle(Gtube + "\r\n", rejectToken));
        }

        await WaitForAsync(() => queue.Written >= 1 && queue.WriteFailures == 0);
        var rejectRow = await ReadEvidenceAsync(connectionString, rejectId);
        Assert.NotNull(rejectRow);
        var liveCurrent = Convert.ToInt64(
            await ScalarAsync(connectionString, "SELECT revision FROM nntppostfiltercurrent WHERE policy_id = 1"),
            CultureInfo.InvariantCulture);
        Assert.Equal(2L, liveCurrent);
        Assert.Equal(liveCurrent, rejectRow.Revision);
        Assert.Equal("198.18.0.70", rejectRow.SourceIpText);
        Assert.Equal(4, rejectRow.SourceIpLength);
        Assert.True(rejectRow.HasPayload);
        Assert.Equal(rejectRow.ArticleSize, rejectRow.PayloadLength);
        Assert.True(rejectRow.ArticleSize > 0);
        Assert.Equal("SpamAssassin", rejectRow.Stage);
        Assert.True(
            rejectRow.Reason is "spam" or "scanner-failure",
            "Unexpected PostFilter reason: " + rejectRow.Reason);

        var acceptToken = "vectortest-postfilter-runtime-accept-" + Guid.NewGuid().ToString("N");
        var acceptId = "<" + acceptToken + "@example.com>";
        await using (var duplex = new PostFilterPostDuplex())
        {
            var session = duplex.CreateSession(
                PostFilterPostHarness.NewQueue(),
                filter,
                clientAddress: IPAddress.Parse("198.18.0.70"),
                postFilterEvidence: queue);
            await PostFilterPostHarness.PostAsync(
                duplex,
                session,
                "240 Article received OK",
                article: PostFilterPostHarness.TextArticle("This is a short ham body.\r\n", acceptToken));
        }

        await writer.StopAsync(CancellationToken.None);
        await policy.StopAsync(CancellationToken.None);

        await AssertLiveRevisionUnchangedAsync(connectionString);
        var deploymentAfter = await ReadEvidenceAsync(connectionString, DeploymentMessageId);
        Assert.NotNull(deploymentAfter);
        Assert.Equal(deploymentBefore.RejectionId, deploymentAfter.RejectionId);
        Assert.Equal(deploymentBefore.ArticleSize, deploymentAfter.ArticleSize);
        Assert.Equal(deploymentBefore.Reason, deploymentAfter.Reason);
        Assert.Equal(rejectionCountBefore + 1, await CountRejectionsAsync(connectionString));
        Assert.Null(await ReadEvidenceAsync(connectionString, acceptId));
    }

    private static string RequireLiveNntp()
    {
        var connectionString = NntpDbIntegration.TryGetConnectionString()
            ?? throw new InvalidOperationException(NntpDbIntegration.SkipReason);
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        Assert.Equal("nntp", database);
        return connectionString;
    }

    private static ValidateOptionsResult ValidateProductionAppsettingsHasNoPostFilterSection()
    {
        var path = FindProductionAppsettings();
        Assert.True(File.Exists(path), "Production VectorNNTPD.ReaderService.json was not found: " + path);
        var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
        Assert.False(configuration.GetSection("Nntpd:PostFilter").Exists());
        return new PostFilterLeftoverConfigurationValidator(configuration).Validate(null, new NntpdOptions());
    }

    private static string FindProductionAppsettings()
    {
        var relative = Path.Join("src", "VectorNNTPD.ReaderService", "VectorNNTPD.ReaderService.json");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Join(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return Path.GetFullPath(relative);
    }

    private static async Task AssertLiveRevisionUnchangedAsync(string connectionString)
    {
        var current = Convert.ToInt64(
            await ScalarAsync(connectionString, "SELECT revision FROM nntppostfiltercurrent WHERE policy_id = 1"),
            CultureInfo.InvariantCulture);
        Assert.Equal(2L, current);
        var gate = Convert.ToString(
            await ScalarAsync(
                connectionString,
                "SELECT gate FROM nntppostfilterpolicy WHERE revision = 2"),
            CultureInfo.InvariantCulture);
        Assert.Equal("Active", gate);
        var sa = Convert.ToString(
            await ScalarAsync(
                connectionString,
                "SELECT sa_enabled FROM nntppostfilterpolicy WHERE revision = 2"),
            CultureInfo.InvariantCulture);
        Assert.Equal("Y", sa);
        var historical = Convert.ToString(
            await ScalarAsync(
                connectionString,
                "SELECT gate FROM nntppostfilterpolicy WHERE revision = 1"),
            CultureInfo.InvariantCulture);
        Assert.Equal("Disabled", historical);
    }

    private static async Task<long> CountRejectionsAsync(string connectionString) =>
        Convert.ToInt64(
            await ScalarAsync(connectionString, "SELECT COUNT(*) FROM nntppostfilterrejections"),
            CultureInfo.InvariantCulture);

    private static async Task<EvidenceRow?> ReadEvidenceAsync(string connectionString, string messageId)
    {
        await using var connection = MySqlNntpDbConnectionFactory.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT rejection_id, revision, INET6_NTOA(source_ip), OCTET_LENGTH(source_ip),
                   article_size, OCTET_LENGTH(article_payload), stage, reason
            FROM nntppostfilterrejections
            WHERE message_id = @id
            """;
        command.Parameters.AddWithValue("@id", messageId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new EvidenceRow(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            !reader.IsDBNull(5),
            reader.GetString(6),
            reader.GetString(7));
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = MySqlNntpDbConnectionFactory.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }

    private sealed record EvidenceRow(
        long RejectionId,
        long Revision,
        string SourceIpText,
        int SourceIpLength,
        int ArticleSize,
        int PayloadLength,
        bool HasPayload,
        string Stage,
        string Reason);

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
