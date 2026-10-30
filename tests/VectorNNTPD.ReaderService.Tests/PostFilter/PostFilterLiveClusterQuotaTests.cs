using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.PostFilter.Quota;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Tests.PostFilter;

/// <summary>
/// Two reservation identities against one live Redis store, using the same
/// authenticated account name that POST passes (<c>Authentication.Username</c>).
/// </summary>
public sealed class PostFilterLiveClusterQuotaTests : IClassFixture<PostFilterQuotaRedisIntegrationFixture>
{
    private readonly PostFilterQuotaRedisIntegrationFixture _redis;

    public PostFilterLiveClusterQuotaTests(PostFilterQuotaRedisIntegrationFixture redis)
    {
        _redis = redis;
    }

    [SessionStateRedisIntegrationFact]
    public async Task TwoNodes_SameAccountUsername_ShareLiveRedisCeiling()
    {
        var account = await _redis.CreateAccountAsync();
        var snapshot = PostFilterPolicyCompiler.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 1 },
        });
        var nodeA = new PostFilterEvaluator(
            new StaticPostFilterPolicySource(snapshot),
            _redis.Store,
            new PostFilterReservationIdentity("n1", "incA"),
            new NullPostFilterSpamAssassin(),
            NullLogger<PostFilterEvaluator>.Instance);
        var nodeB = new PostFilterEvaluator(
            new StaticPostFilterPolicySource(snapshot),
            _redis.Store,
            new PostFilterReservationIdentity("n2", "incB"),
            new NullPostFilterSpamAssassin(),
            NullLogger<PostFilterEvaluator>.Instance);

        var first = await nodeA.EvaluateAsync(Request(account));
        Assert.Equal(PostFilterDecision.Accept, first.Decision);
        Assert.NotNull(first.Lease);
        Assert.Equal(account, first.Lease!.AccountName);
        Assert.Equal(
            PostFilterQuotaCommitStatus.Committed,
            await first.Lease.CommitAsync(
                DateTimeOffset.FromUnixTimeMilliseconds(5_000),
                CancellationToken.None));

        var second = await nodeB.EvaluateAsync(Request(account));
        Assert.Equal(PostFilterDecision.Reject, second.Decision);
        Assert.Equal(PostFilterQuotaReserveStatus.DeniedMessagesLong, second.QuotaStatus);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Scenario10_CrossNode_SameAccountSharesLiveRedisCeiling_AtPost()
    {
        var account = await _redis.CreateAccountAsync();
        var snapshot = PostFilterPolicyCompiler.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 1 },
        });
        var nodeA = PostFilterPostHarness.CreateFilter(
            snapshot,
            new PolicyRecordingQuotaStore(_redis.Store),
            identity: new PostFilterReservationIdentity("n1", "incA"));
        var nodeB = PostFilterPostHarness.CreateFilter(
            snapshot,
            new PolicyRecordingQuotaStore(_redis.Store),
            identity: new PostFilterReservationIdentity("n2", "incB"));

        await using var first = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            first,
            first.CreateSession(PostFilterPostHarness.NewQueue(), nodeA),
            "240 Article received OK",
            username: account);
        await using var second = new PostFilterPostDuplex();
        var queue = PostFilterPostHarness.NewQueue();
        await PostFilterPostHarness.PostAsync(
            second,
            second.CreateSession(queue, nodeB),
            "441 Posting failed",
            username: account);
        Assert.Equal(0, queue.Count);
        await _redis.DeleteKeysAsync(account);
    }

    private static PostFilterRequest Request(string account) =>
        PostFilterEvaluatorTestsRequest.Create(account);
}

/// <summary>Shares the evaluator test article constructor without coupling session duplex.</summary>
internal static class PostFilterEvaluatorTestsRequest
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(5_000);

    public static PostFilterRequest Create(string account)
    {
        var bytes = "From: a@b\r\nNewsgroups: misc.test\r\nSubject: t\r\nMessage-ID: <h@example.com>\r\nDate: 1 Jan 2026 00:00:00 +0000\r\n\r\nbody\r\n"u8.ToArray();
        var article = new VectorNNTPD.Common.Articles.ArticleRecord(
            VectorNNTPD.Common.Articles.ArticleId.FromMessageId("<h@example.com>"u8),
            artHash: 1,
            artType: VectorNNTPD.Common.Articles.ArticleType.Default,
            artLines: 1,
            canonicalUtc: Now.UtcDateTime,
            parseStatus: VectorNNTPD.Common.Articles.ArticleParseStatus.CanonicalV1,
            artData: bytes,
            fields: default);
        return new PostFilterRequest(
            article,
            account,
            VectorNNTPD.Common.Networking.Proxy.ConnectionClientIdentity.Direct(
                new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)),
            accountPolicy: null,
            Now,
            ["misc.test"],
            Now);
    }
}
