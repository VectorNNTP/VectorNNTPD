using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.PostFilter.Quota;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Tests.PostFilter;

public sealed class PostFilterSpamAssassinEligibilityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(5_000);
    private const int Kilobyte768 = 768 * 1024;

    [Fact]
    public async Task SmallEligibleArticle_CallsSpamAssassin()
    {
        var sa = new RecordingSpamAssassin();
        var result = await EvaluateAsync(EnabledOptions(), ArticleOfSize(200), sa);
        Assert.Equal(PostFilterDecision.Accept, result.Decision);
        Assert.Equal(1, sa.Calls);
        Assert.NotNull(result.Lease);
    }

    [Fact]
    public async Task ArticleExactlyAtMaximum_IsSkipped()
    {
        var sa = new RecordingSpamAssassin();
        var options = EnabledOptions();
        options.SpamAssassin.MaxArticleSize = 256;
        var result = await EvaluateAsync(options, ArticleOfSize(256), sa);
        Assert.Equal(PostFilterDecision.Accept, result.Decision);
        Assert.Equal(0, sa.Calls);
        Assert.NotNull(result.Lease);
    }

    [Fact]
    public async Task ArticleOneByteBelowMaximum_IsScanned()
    {
        var sa = new RecordingSpamAssassin();
        var options = EnabledOptions();
        options.SpamAssassin.MaxArticleSize = 256;
        var result = await EvaluateAsync(options, ArticleOfSize(255), sa);
        Assert.Equal(PostFilterDecision.Accept, result.Decision);
        Assert.Equal(1, sa.Calls);
    }

    [Fact]
    public async Task ArticleAboveMaximum_DoesNotCallSpamAssassin()
    {
        var sa = new RecordingSpamAssassin();
        var options = EnabledOptions();
        options.SpamAssassin.MaxArticleSize = 256;
        var result = await EvaluateAsync(options, ArticleOfSize(257), sa);
        Assert.Equal(PostFilterDecision.Accept, result.Decision);
        Assert.Equal(0, sa.Calls);
        Assert.NotNull(result.Lease);
    }

    [Fact]
    public async Task YEncoded_IsExcludedByDefault()
    {
        var sa = new RecordingSpamAssassin();
        var result = await EvaluateAsync(EnabledOptions(), ArticleOfSize(200, ArticleType.YEncoded), sa);
        Assert.Equal(PostFilterDecision.Accept, result.Decision);
        Assert.Equal(0, sa.Calls);
    }

    [Fact]
    public async Task Binary_IsScannedUnlessExcluded()
    {
        var scanned = new RecordingSpamAssassin();
        await EvaluateAsync(EnabledOptions(), ArticleOfSize(200, ArticleType.Binary), scanned);
        Assert.Equal(1, scanned.Calls);

        var skipped = new RecordingSpamAssassin();
        var options = EnabledOptions();
        options.SpamAssassin.ExcludeArtTypes = ["Binary"];
        await EvaluateAsync(options, ArticleOfSize(200, ArticleType.Binary), skipped);
        Assert.Equal(0, skipped.Calls);
    }

    [Fact]
    public async Task MultipleExcludedFlags_SkipWhenAnyMatch()
    {
        var sa = new RecordingSpamAssassin();
        var options = EnabledOptions();
        options.SpamAssassin.ExcludeArtTypes = ["YEncoded", "Binary", "Mime"];
        await EvaluateAsync(options, ArticleOfSize(200, ArticleType.Mime | ArticleType.Default), sa);
        Assert.Equal(0, sa.Calls);
        await EvaluateAsync(options, ArticleOfSize(200, ArticleType.Default), sa);
        Assert.Equal(1, sa.Calls);
    }

    [Fact]
    public async Task ZeroMaximum_DisablesSizeGate()
    {
        var sa = new RecordingSpamAssassin();
        var options = EnabledOptions();
        options.SpamAssassin.MaxArticleSize = 0;
        var result = await EvaluateAsync(options, ArticleOfSize(Kilobyte768), sa);
        Assert.Equal(PostFilterDecision.Accept, result.Decision);
        Assert.Equal(1, sa.Calls);
    }

    [Fact]
    public async Task EligibilityUsesCapturedSnapshot()
    {
        var first = PostFilterPolicyCompiler.Compile(EnabledOptions());
        var secondOptions = EnabledOptions();
        secondOptions.SpamAssassin.MaxArticleSize = 1;
        var second = PostFilterPolicyCompiler.Compile(secondOptions);
        var source = new MutablePolicySource(first);
        var sa = new RecordingSpamAssassin { Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var filter = new PostFilterEvaluator(
            source,
            new InMemoryPostFilterQuotaStore(),
            new PostFilterReservationIdentity("n1", "inc"),
            sa,
            NullLogger<PostFilterEvaluator>.Instance);
        var evaluate = filter.EvaluateAsync(Request(ArticleOfSize(200)), CancellationToken.None).AsTask();
        await sa.BlockEntered.Task;
        source.Current = second;
        sa.Block.SetResult();
        Assert.Equal(PostFilterDecision.Accept, (await evaluate).Decision);
        Assert.Equal(1, sa.Calls);
        Assert.Equal(first.SpamAssassinHosts, sa.LastTarget.Hosts);
        Assert.Equal(first.SpamAssassinMaxConnections, sa.LastTarget.MaxConnections);
        var skipped = await filter.EvaluateAsync(Request(ArticleOfSize(200)));
        Assert.Equal(PostFilterDecision.Accept, skipped.Decision);
        Assert.NotNull(skipped.Lease);
        Assert.Equal(1, sa.Calls);
    }

    [Fact]
    public async Task DefaultPolicy_DoesNotSend768KbToSpamAssassin()
    {
        var sa = new RecordingSpamAssassin();
        var snapshot = PostFilterPolicyCompiler.Compile(EnabledOptions());
        Assert.Equal(PostFilterSpamAssassinOptions.DefaultMaxArticleSize, snapshot.SpamAssassinMaxArticleSize);
        Assert.Equal(ArticleType.YEncoded, snapshot.SpamAssassinExcludeArtTypes);
        var result = await EvaluateAsync(EnabledOptions(), ArticleOfSize(Kilobyte768), sa);
        Assert.Equal(PostFilterDecision.Accept, result.Decision);
        Assert.Equal(0, sa.Calls);
        Assert.True(Kilobyte768 >= snapshot.SpamAssassinMaxArticleSize);
    }

    [Fact]
    public void Compiler_RejectsNegativeMaxArticleSize()
    {
        var options = EnabledOptions();
        options.SpamAssassin.MaxArticleSize = -1;
        Assert.Throws<InvalidOperationException>(() => PostFilterPolicyCompiler.Compile(options));
    }

    [Fact]
    public void Compiler_RejectsMaxConnectionsOutsideRange()
    {
        var options = EnabledOptions();
        options.SpamAssassin.MaxConnections = 0;
        Assert.Throws<InvalidOperationException>(() => PostFilterPolicyCompiler.Compile(options));
        options.SpamAssassin.MaxConnections = 33;
        Assert.Throws<InvalidOperationException>(() => PostFilterPolicyCompiler.Compile(options));
    }

    private static async Task<PostFilterResult> EvaluateAsync(
        PostFilterOptions options,
        ArticleRecord article,
        RecordingSpamAssassin sa)
    {
        var filter = new PostFilterEvaluator(
            new StaticPostFilterPolicySource(PostFilterPolicyCompiler.Compile(options)),
            new InMemoryPostFilterQuotaStore(),
            new PostFilterReservationIdentity("n1", "inc"),
            sa,
            NullLogger<PostFilterEvaluator>.Instance);
        return await filter.EvaluateAsync(Request(article));
    }

    private static PostFilterOptions EnabledOptions() =>
        new()
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10_000 },
            SpamAssassin = new PostFilterSpamAssassinOptions
            {
                Enabled = true,
                OnFailure = PostFilterSpamOnFailure.Reject,
                Hosts = ["127.0.0.1"],
            },
        };

    private static PostFilterRequest Request(ArticleRecord article) =>
        new(
            article,
            "poster",
            ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119)),
            accountPolicy: null,
            Now,
            ["misc.test"],
            Now,
            "nntpd01.usenet.ninja");

    private static ArticleRecord ArticleOfSize(int size, ArticleType type = ArticleType.Default)
    {
        var header = "From: a@b\r\nNewsgroups: misc.test\r\nSubject: t\r\nMessage-ID: <h@example.com>\r\nDate: 1 Jan 2026 00:00:00 +0000\r\n\r\n"u8;
        if (size < header.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Size must cover the header block.");
        }

        var bytes = new byte[size];
        header.CopyTo(bytes);
        bytes.AsSpan(header.Length).Fill((byte)'x');
        return new ArticleRecord(
            ArticleId.FromMessageId("<h@example.com>"u8),
            artHash: 1,
            artType: type,
            artLines: 1,
            canonicalUtc: Now.UtcDateTime,
            parseStatus: ArticleParseStatus.CanonicalV1,
            artData: bytes,
            fields: default);
    }

    private sealed class MutablePolicySource : IPostFilterPolicySource
    {
        public MutablePolicySource(PostFilterPolicySnapshot current) => Current = current;

        public PostFilterPolicySnapshot Current { get; set; }
    }

    private sealed class RecordingSpamAssassin : IPostFilterSpamAssassin
    {
        public int Calls { get; private set; }

        public PostFilterSpamAssassinTarget LastTarget { get; private set; }

        public TaskCompletionSource? Block { get; set; }

        public TaskCompletionSource BlockEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<PostFilterSpamAssassinResult> CheckAsync(
            ArticleRecord article,
            string? accountName,
            PostFilterSpamAssassinTarget target,
            SpamdScanContext scanContext,
            CancellationToken cancellationToken = default)
        {
            _ = article;
            _ = accountName;
            _ = scanContext;
            LastTarget = target;
            Calls++;
            if (Block is not null)
            {
                BlockEntered.TrySetResult();
                await Block.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return PostFilterSpamAssassinResult.Ham();
        }
    }
}
