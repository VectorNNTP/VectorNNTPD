using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Transport.ArticleTransfer;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.History;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Networking.Transport;
using VectorNNTPD.ReaderService.RabbitMq;
using VectorNNTPD.ReaderService.RabbitMq.ArticleWork;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Framing;
using VectorNNTPD.ReaderService.Storage;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.RabbitMq;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.ReaderService.Transport.Vatp;

namespace VectorNNTPD.ReaderService.Tests.Session;

/// <summary>
/// Message-id ARTICLE/HEAD/BODY/STAT through StorageServer lookup, then ArticleWork only on silence, then VATP.
/// </summary>
public sealed class ArticleRetrievalVatpCommandTests
{
    private static readonly NntpAuthorization Reader = new(
        isAuthenticated: true,
        authorizedReader: true,
        authorizedTransit: false,
        postingPermitted: false,
        streamingPermitted: false);

    private const string MessageId = "<vatp-article@example.test>";
    private const string BackfillFqdn = "backfiller.test";
    private const int BackfillPort = 119;
    private const string StorageFqdn = "cache01.usenet.ninja";
    private const int StoragePort = 563;

    [Theory]
    [InlineData("ARTICLE")]
    [InlineData("HEAD")]
    [InlineData("BODY")]
    [InlineData("STAT")]
    public async Task MessageId_Success_ServesFromVatp(string verb)
    {
        var prepared = PrepareArticle(MessageId, body: "hello\r\n.dot\r\n");
        await using var duplex = await ArticleDuplex.CreateAsync();
        var queue = new RecordingIngestionQueue();
        var lookup = new StubStorageLookup(StorageSilence(prepared.Record.ArtId));
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, BackfillFqdn, BackfillPort);
        var vatp = new RecordingVatpArticleClient(VatpFetchResult.FromSuccess(prepared.Record, prepared.RequestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, ingestion: queue, storageLookup: lookup);

        await DispatchLineAsync(duplex, session, $"{verb} {MessageId}");

        byte[]? served = null;
        switch (verb)
        {
            case "ARTICLE":
                {
                    var multiline = await duplex.ReadMultilineAsync();
                    var wire = Encoding.ASCII.GetString(multiline);
                    Assert.StartsWith($"220 0 {MessageId}\r\n", wire, StringComparison.Ordinal);
                    Assert.Contains("Message-ID: " + MessageId, wire, StringComparison.Ordinal);
                    Assert.Contains("hello\r\n", wire, StringComparison.Ordinal);
                    Assert.Contains("..dot\r\n", wire, StringComparison.Ordinal); // restuffed
                    Assert.EndsWith(".\r\n", wire, StringComparison.Ordinal);
                    served = DestuffCustomerMultiline(multiline);
                    break;
                }
            case "HEAD":
                {
                    var multiline = await duplex.ReadMultilineAsync();
                    var wire = Encoding.ASCII.GetString(multiline);
                    Assert.StartsWith($"221 0 {MessageId}\r\n", wire, StringComparison.Ordinal);
                    Assert.Contains("Message-ID: " + MessageId, wire, StringComparison.Ordinal);
                    Assert.DoesNotContain("hello", wire, StringComparison.Ordinal);
                    Assert.EndsWith(".\r\n", wire, StringComparison.Ordinal);
                    served = DestuffCustomerMultiline(multiline);
                    break;
                }
            case "BODY":
                {
                    var wire = Encoding.ASCII.GetString(await duplex.ReadMultilineAsync());
                    Assert.StartsWith($"222 0 {MessageId}\r\n", wire, StringComparison.Ordinal);
                    Assert.DoesNotContain("Message-ID:", wire, StringComparison.Ordinal);
                    Assert.Contains("hello\r\n", wire, StringComparison.Ordinal);
                    Assert.Contains("..dot\r\n", wire, StringComparison.Ordinal);
                    Assert.EndsWith(".\r\n", wire, StringComparison.Ordinal);
                    break;
                }
            case "STAT":
                Assert.Equal($"223 0 {MessageId}", await duplex.ReadClientLineAsync());
                break;
        }

        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(1, rpc.LookupCount);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(BackfillFqdn, vatp.Fqdn);
        Assert.Equal(BackfillPort, vatp.VatpPort);
        Assert.Equal(1, queue.AdmitCalls);
        var admitted = Assert.Single(queue.Admitted);
        Assert.Equal(InboundArticleProducer.BackFiller, admitted.Producer);
        var hopped = ArticleRecordFactory.TryCreate(
            new NntpArticleParser(NntpdOptions.FormatFqdn(1, "usenet.ninja")),
            prepared.Record.ArtData);
        Assert.True(hopped.IsAccepted, hopped.MaterializeFailure.ToString());
        Assert.Equal(prepared.Record.ArtId, hopped.Record.ArtId);
        Assert.True(admitted.Record.ArtData.Span.SequenceEqual(hopped.Record.ArtData.Span));
        var path = Encoding.ASCII.GetString(admitted.Record.Path);
        Assert.StartsWith("nntpd01.usenet.ninja!", path, StringComparison.Ordinal);
        Assert.Contains("news.usenet.ninja!", path, StringComparison.Ordinal);
        Assert.Contains("!nntpd.test!", path, StringComparison.Ordinal);
        Assert.EndsWith("!peer.example", path, StringComparison.Ordinal);
        Assert.DoesNotContain("cache01", path, StringComparison.Ordinal);
        if (verb == "ARTICLE")
        {
            Assert.NotNull(served);
            Assert.True(served.AsSpan().SequenceEqual(admitted.Record.ArtData.Span));
            Assert.False(served.AsSpan().SequenceEqual(prepared.Record.ArtData.Span));
        }
        else if (verb == "HEAD")
        {
            Assert.NotNull(served);
            Assert.True(ArticleWireReconstructor.TrySplitHeadersAndBody(
                admitted.Record.ArtData.Span,
                out var admittedHeaders,
                out _));
            Assert.True(served.AsSpan().SequenceEqual(admittedHeaders));
            Assert.True(ArticleWireReconstructor.TrySplitHeadersAndBody(
                prepared.Record.ArtData.Span,
                out var fetchedHeaders,
                out _));
            Assert.False(served.AsSpan().SequenceEqual(fetchedHeaders));
        }
    }

    [Theory]
    [InlineData("ARTICLE")]
    [InlineData("HEAD")]
    public async Task StorageHit_TraversesOnceOnIngress(string verb)
    {
        var stored = PrepareArticle(MessageId, body: "hello\r\n");
        var entered = ArticleRecordFactory.TryCreate(
            new NntpArticleParser(NntpdOptions.FormatFqdn(1, "usenet.ninja")),
            stored.Record.ArtData,
            ArticlePathMode.Traverse);
        Assert.True(entered.IsAccepted, entered.MaterializeFailure.ToString());
        Assert.Equal(stored.Record.ArtId, entered.Record.ArtId);
        Assert.False(entered.Record.ArtData.Span.SequenceEqual(stored.Record.ArtData.Span));

        await using var duplex = await ArticleDuplex.CreateAsync();
        var queue = new RecordingIngestionQueue();
        var lookup = new StubStorageLookup(StorageFound(stored.Record.ArtId, Guid.NewGuid()));
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, stored.Record.ArtId, BackfillFqdn, BackfillPort);
        var vatp = new RecordingVatpArticleClient(
            VatpFetchResult.FromSuccess(stored.Record, Guid.NewGuid(), stored.Record.ArtId));
        var session = duplex.CreateSession(
            articleWorkRpc: rpc,
            vatp: vatp,
            ingestion: queue,
            storageLookup: lookup);

        await DispatchLineAsync(duplex, session, $"{verb} {MessageId}");

        var served = DestuffCustomerMultiline(await duplex.ReadMultilineAsync());
        var path = Encoding.ASCII.GetString(entered.Record.Path);
        Assert.StartsWith("nntpd01.usenet.ninja!", path, StringComparison.Ordinal);
        Assert.Contains("news.usenet.ninja!", path, StringComparison.Ordinal);
        Assert.Contains("!nntpd.test!", path, StringComparison.Ordinal);
        Assert.Equal(1, CountPathToken(path, "news.usenet.ninja"));
        Assert.Equal(1, CountPathToken(path, "nntpd01.usenet.ninja"));
        if (verb == "ARTICLE")
        {
            Assert.True(served.AsSpan().SequenceEqual(entered.Record.ArtData.Span));
            Assert.False(served.AsSpan().SequenceEqual(stored.Record.ArtData.Span));
        }
        else
        {
            Assert.True(ArticleWireReconstructor.TrySplitHeadersAndBody(
                entered.Record.ArtData.Span,
                out var headers,
                out _));
            Assert.True(served.AsSpan().SequenceEqual(headers));
        }

        Assert.Contains("Path: " + path, Encoding.ASCII.GetString(served), StringComparison.Ordinal);
        Assert.Equal(0, queue.AdmitCalls);
        Assert.Equal(0, rpc.LookupCount);
        Assert.Equal(StorageFqdn, vatp.Fqdn);
        Assert.Equal(StoragePort, vatp.VatpPort);
    }

    [Fact]
    public async Task Article_Head_Body_Share_SingleFetchPerCommand()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var lookup = new StubStorageLookup(StorageSilence(prepared.Record.ArtId));
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, BackfillFqdn, BackfillPort);
        var vatp = new StubVatpArticleClient(VatpFetchResult.FromSuccess(prepared.Record, prepared.RequestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);

        await DispatchLineAsync(duplex, session, $"HEAD {MessageId}");
        _ = await duplex.ReadMultilineAsync();
        Assert.Equal(1, vatp.FetchCount);

        await DispatchLineAsync(duplex, session, $"BODY {MessageId}");
        _ = await duplex.ReadMultilineAsync();
        Assert.Equal(2, vatp.FetchCount);
        Assert.Equal(2, lookup.LookupCount);
        Assert.Equal(2, rpc.LookupCount);
    }

    [Fact]
    public async Task ArticleWork_NotFound_Returns430_WithoutVatp()
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var lookup = new StubStorageLookup(StorageSilence(ArticleId.FromMessageId(Encoding.ASCII.GetBytes(MessageId))));
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var vatp = new StubVatpArticleClient(VatpFetchResult.ConnectionFailure("unused"));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(1, rpc.LookupCount);
        Assert.Equal(0, vatp.FetchCount);
    }

    [Fact]
    public async Task Vatp_ConnectionFailure_Returns400()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var lookup = new StubStorageLookup(StorageSilence(prepared.Record.ArtId));
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, BackfillFqdn, BackfillPort);
        var vatp = new StubVatpArticleClient(VatpFetchResult.ConnectionFailure("down"));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        await DispatchLineAsync(duplex, session, "DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Vatp_RemoteFailure_Returns400()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var lookup = new StubStorageLookup(StorageSilence(prepared.Record.ArtId));
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, BackfillFqdn, BackfillPort);
        var vatp = new StubVatpArticleClient(VatpFetchResult.RemoteFailure("open", VatpErrorCode.OpenRejected, prepared.RequestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"BODY {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task Vatp_Incomplete_NeverServed_Returns400()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var lookup = new StubStorageLookup(StorageSilence(prepared.Record.ArtId));
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, BackfillFqdn, BackfillPort);
        var vatp = new StubVatpArticleClient(
            VatpFetchResult.IncompleteOrMalformed("truncated", VatpErrorCode.IncompleteTransfer, prepared.RequestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task ArticleId_Mismatch_Returns430()
    {
        var prepared = PrepareArticle(MessageId);
        var other = PrepareArticle("<other@example.test>");
        await using var duplex = await ArticleDuplex.CreateAsync();
        var lookup = new StubStorageLookup(StorageSilence(prepared.Record.ArtId));
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, BackfillFqdn, BackfillPort);
        // VATP returns a valid record whose ArtId/MID do not match the OPEN identity.
        var vatp = new StubVatpArticleClient(VatpFetchResult.FromSuccess(other.Record, prepared.RequestId, other.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task IngestQueueFull_StillServesArticle()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var queue = new RecordingIngestionQueue { NextResult = ArticleEnqueueResult.Full };
        var lookup = new StubStorageLookup(StorageSilence(prepared.Record.ArtId));
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, BackfillFqdn, BackfillPort);
        var vatp = new StubVatpArticleClient(VatpFetchResult.FromSuccess(prepared.Record, prepared.RequestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, ingestion: queue, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        var wire = Encoding.ASCII.GetString(await duplex.ReadMultilineAsync());
        Assert.StartsWith($"220 0 {MessageId}\r\n", wire, StringComparison.Ordinal);
        Assert.Empty(queue.Admitted);
        Assert.Equal(1, queue.AdmitCalls);
        Assert.Equal(ArticleEnqueueResult.Full, queue.LastResult);
    }

    [Fact]
    public async Task SuccessfulAdmit_RemembersHistoryOnce()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var history = new RecordingHistoryDb();
        var queue = new RecordingIngestionQueue();
        var lookup = new StubStorageLookup(StorageSilence(prepared.Record.ArtId));
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, BackfillFqdn, BackfillPort);
        var vatp = new StubVatpArticleClient(VatpFetchResult.FromSuccess(prepared.Record, prepared.RequestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(
            articleWorkRpc: rpc,
            vatp: vatp,
            ingestion: queue,
            historyDb: history,
            storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"STAT {MessageId}");
        Assert.Equal($"223 0 {MessageId}", await duplex.ReadClientLineAsync());
        Assert.Equal(MessageId, Encoding.ASCII.GetString(Assert.Single(history.Remembered).Span));
    }

    [Fact]
    public async Task Cancellation_DuringVatp_Propagates()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var lookup = new StubStorageLookup(StorageSilence(prepared.Record.ArtId));
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, BackfillFqdn, BackfillPort);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vatp = new GatedVatpArticleClient(gate.Task);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        using var cts = new CancellationTokenSource();
        var dispatch = DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}", cts.Token);
        await vatp.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        gate.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);
        Assert.True(vatp.SawCancellation);
    }

    [Fact]
    public async Task StorageHit_DoesNotCallArticleWork_FetchesStorageEndpoint()
    {
        var prepared = PrepareArticle(MessageId);
        var requestId = Guid.NewGuid();
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, BackfillFqdn, BackfillPort);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId));
        var vatp = new RecordingVatpArticleClient(
            VatpFetchResult.FromSuccess(prepared.Record, requestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        var wire = Encoding.ASCII.GetString(await duplex.ReadMultilineAsync());
        Assert.StartsWith($"220 0 {MessageId}\r\n", wire, StringComparison.Ordinal);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(0, rpc.LookupCount);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(StorageFqdn, vatp.Fqdn);
        Assert.Equal(StoragePort, vatp.VatpPort);
        Assert.NotEqual(BackfillFqdn, vatp.Fqdn);
    }

    [Theory]
    [InlineData("ARTICLE")]
    [InlineData("HEAD")]
    [InlineData("BODY")]
    [InlineData("STAT")]
    public async Task StorageHit_ServesWithoutIngestion(string verb)
    {
        var prepared = PrepareArticle(MessageId, body: "hello\r\n");
        var requestId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        await using var duplex = await ArticleDuplex.CreateAsync();
        var queue = new RecordingIngestionQueue();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId));
        var vatp = new RecordingVatpArticleClient(
            VatpFetchResult.FromSuccess(prepared.Record, requestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(
            articleWorkRpc: rpc,
            vatp: vatp,
            ingestion: queue,
            storageLookup: lookup);

        await DispatchLineAsync(duplex, session, $"{verb} {MessageId}");

        switch (verb)
        {
            case "ARTICLE":
                Assert.StartsWith($"220 0 {MessageId}\r\n", Encoding.ASCII.GetString(await duplex.ReadMultilineAsync()), StringComparison.Ordinal);
                break;
            case "HEAD":
                Assert.StartsWith($"221 0 {MessageId}\r\n", Encoding.ASCII.GetString(await duplex.ReadMultilineAsync()), StringComparison.Ordinal);
                break;
            case "BODY":
                Assert.StartsWith($"222 0 {MessageId}\r\n", Encoding.ASCII.GetString(await duplex.ReadMultilineAsync()), StringComparison.Ordinal);
                break;
            default:
                Assert.Equal($"223 0 {MessageId}", await duplex.ReadClientLineAsync());
                break;
        }

        Assert.Equal(0, rpc.LookupCount);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(prepared.Record.ArtId, lookup.LastArticleId);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(StorageFqdn, vatp.Fqdn);
        Assert.Equal(StoragePort, vatp.VatpPort);
        Assert.Equal(requestId, vatp.RequestId);
        Assert.Equal(prepared.Record.ArtId, vatp.ArticleId);
        Assert.Equal(0, queue.AdmitCalls);
        Assert.Empty(queue.Admitted);
    }

    [Fact]
    public async Task StorageSilence_ArticleWorkNotFound_Returns430_WithoutVatp()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageSilence(prepared.Record.ArtId));
        var vatp = new RecordingVatpArticleClient(VatpFetchResult.ConnectionFailure("unused"));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
        Assert.Equal(1, rpc.LookupCount);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(0, vatp.FetchCount);
    }

    [Theory]
    [InlineData("Storage article lookup was canceled.")]
    [InlineData("Lookup could not be registered.")]
    public async Task StorageUnavailable_Returns400_WithoutArticleWork(string error)
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageNotFound(prepared.Record.ArtId, error));
        var vatp = new RecordingVatpArticleClient(VatpFetchResult.ConnectionFailure("unused"));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Equal(0, rpc.LookupCount);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(0, vatp.FetchCount);
    }

    [Fact]
    public async Task StorageThrows_Returns400_WithoutArticleWork()
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new ThrowingStorageLookup();
        var vatp = new RecordingVatpArticleClient(VatpFetchResult.ConnectionFailure("unused"));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Equal(0, rpc.LookupCount);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(0, vatp.FetchCount);
    }

    [Fact]
    public async Task StorageSilence_ArticleWorkThrows_Returns400_WithoutRetryingStorage()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new ThrowingArticleWorkRpcClient();
        var lookup = new StubStorageLookup(StorageSilence(prepared.Record.ArtId));
        var vatp = new RecordingVatpArticleClient(VatpFetchResult.ConnectionFailure("unused"));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Equal(1, rpc.LookupCount);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(0, vatp.FetchCount);
    }

    [Fact]
    public async Task Cancellation_DuringStorageLookup_PropagatesWithoutReply()
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookup = new GatedStorageLookup(gate.Task);
        var vatp = new RecordingVatpArticleClient(VatpFetchResult.ConnectionFailure("unused"));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        using var cts = new CancellationTokenSource();
        var dispatch = DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}", cts.Token);
        await lookup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        gate.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);
        Assert.True(lookup.SawCancellation);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(0, rpc.LookupCount);
        Assert.Equal(0, vatp.FetchCount);
        Assert.Null(await duplex.TryReadClientLineAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task StorageHit_OpenRejected_Returns400_WithoutRetry()
    {
        var prepared = PrepareArticle(MessageId);
        var requestId = Guid.NewGuid();
        await using var duplex = await ArticleDuplex.CreateAsync();
        var queue = new RecordingIngestionQueue();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId));
        var vatp = new RecordingVatpArticleClient(
            VatpFetchResult.RemoteFailure("open", VatpErrorCode.OpenRejected, requestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, ingestion: queue, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Equal(0, rpc.LookupCount);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(requestId, vatp.RequestId);
        Assert.Equal(0, queue.AdmitCalls);
    }

    [Fact]
    public async Task StorageHit_IdentityMismatch_Returns430_WithoutRetry()
    {
        var prepared = PrepareArticle(MessageId);
        var other = PrepareArticle("<other@example.test>");
        var requestId = Guid.NewGuid();
        await using var duplex = await ArticleDuplex.CreateAsync();
        var queue = new RecordingIngestionQueue();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId));
        var vatp = new RecordingVatpArticleClient(
            VatpFetchResult.FromSuccess(other.Record, requestId, other.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, ingestion: queue, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
        Assert.Equal(0, rpc.LookupCount);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(0, queue.AdmitCalls);
    }

    [Fact]
    public async Task StorageHit_ClientDisconnectDuringServe_DoesNotAdmit()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var queue = new RecordingIngestionQueue();
        using var cts = new CancellationTokenSource();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, BackfillFqdn, BackfillPort);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, Guid.NewGuid()));
        var vatp = new CancelOnReturnVatp(
            VatpFetchResult.FromSuccess(prepared.Record, prepared.RequestId, prepared.Record.ArtId),
            cts);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, ingestion: queue, storageLookup: lookup);
        var dispatch = DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}", cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(0, rpc.LookupCount);
        Assert.Equal(0, queue.AdmitCalls);
        Assert.Empty(queue.Admitted);
    }

    [Fact]
    public async Task StorageHit_IncompleteTransfer_Returns400_WithoutRetry()
    {
        var prepared = PrepareArticle(MessageId);
        var requestId = Guid.NewGuid();
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId));
        var vatp = new RecordingVatpArticleClient(
            VatpFetchResult.IncompleteOrMalformed(
                "truncated",
                VatpErrorCode.IncompleteTransfer,
                requestId,
                prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Equal(0, rpc.LookupCount);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(1, vatp.FetchCount);
    }

    [Fact]
    public async Task StorageHit_CancellationDuringVatp_PropagatesWithoutReply()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, Guid.NewGuid()));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vatp = new GatedVatpArticleClient(gate.Task);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        using var cts = new CancellationTokenSource();
        var dispatch = DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}", cts.Token);
        await vatp.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        gate.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);
        Assert.True(vatp.SawCancellation);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(0, rpc.LookupCount);
        Assert.Null(await duplex.TryReadClientLineAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task TwoSessions_SameMessageId_LookupIndependently()
    {
        var prepared = PrepareArticle(MessageId);
        var requestId = Guid.NewGuid();
        await using var firstDuplex = await ArticleDuplex.CreateAsync();
        await using var secondDuplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId));
        var vatp = new RecordingVatpArticleClient(
            VatpFetchResult.FromSuccess(prepared.Record, requestId, prepared.Record.ArtId));
        var first = firstDuplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        var second = secondDuplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);

        await DispatchLineAsync(firstDuplex, first, $"ARTICLE {MessageId}");
        await DispatchLineAsync(secondDuplex, second, $"ARTICLE {MessageId}");

        Assert.StartsWith(
            $"220 0 {MessageId}\r\n",
            Encoding.ASCII.GetString(await firstDuplex.ReadMultilineAsync()),
            StringComparison.Ordinal);
        Assert.StartsWith(
            $"220 0 {MessageId}\r\n",
            Encoding.ASCII.GetString(await secondDuplex.ReadMultilineAsync()),
            StringComparison.Ordinal);
        Assert.Equal(0, rpc.LookupCount);
        Assert.Equal(2, lookup.LookupCount);
        Assert.Equal(2, vatp.FetchCount);
    }

    [Fact]
    public async Task StorageHit_OneCandidate_StartsFetchWithoutWaitingForAlternate()
    {
        var prepared = PrepareArticle(MessageId);
        var requestId = Guid.NewGuid();
        var alternates = new ManualAlternates();
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId) with { Alternates = alternates });
        var vatp = new RecordingVatpArticleClient(
            VatpFetchResult.FromSuccess(prepared.Record, requestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.StartsWith($"220 0 {MessageId}\r\n", Encoding.ASCII.GetString(await duplex.ReadMultilineAsync()), StringComparison.Ordinal);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(requestId, vatp.RequestId);
        Assert.Equal(0, alternates.WaitCount);
    }

    [Theory]
    [InlineData("ARTICLE", "220")]
    [InlineData("HEAD", "221")]
    [InlineData("BODY", "222")]
    [InlineData("STAT", "223")]
    public async Task StorageFailover_PreDataFailure_UsesSecondCandidate(string verb, string code)
    {
        var prepared = PrepareArticle(MessageId, body: "hello\r\n");
        var requestId = Guid.NewGuid();
        var alternates = new ManualAlternates();
        alternates.Publish(new StorageArticleCandidate(2, "cache02.usenet.ninja", 564));
        await using var duplex = await ArticleDuplex.CreateAsync();
        var queue = new RecordingIngestionQueue();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
        [
            VatpFetchResult.ConnectionFailure("down", requestId, prepared.Record.ArtId, 0),
            VatpFetchResult.FromSuccess(prepared.Record, requestId, prepared.Record.ArtId),
        ]);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, ingestion: queue, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"{verb} {MessageId}");
        if (verb == "STAT")
        {
            Assert.Equal($"{code} 0 {MessageId}", await duplex.ReadClientLineAsync());
        }
        else
        {
            Assert.StartsWith($"{code} 0 {MessageId}\r\n", Encoding.ASCII.GetString(await duplex.ReadMultilineAsync()), StringComparison.Ordinal);
        }

        Assert.Equal(0, rpc.LookupCount);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(2, vatp.FetchCount);
        Assert.Equal([(StorageFqdn, StoragePort), ("cache02.usenet.ninja", 564)], vatp.Endpoints);
        Assert.All(vatp.RequestIds, id => Assert.Equal(requestId, id));
        Assert.Equal(0, queue.AdmitCalls);
        Assert.Empty(queue.Admitted);
    }

    [Fact]
    public async Task StorageFailover_OpenRejectedBeforeData_UsesSecondCandidate()
    {
        await AssertSecondCandidateUsedAsync(
            VatpFetchResult.RemoteFailure("open", VatpErrorCode.OpenRejected, Guid.Empty, default, 0));
    }

    [Fact]
    public async Task StorageFailover_ProtocolFailureBeforeData_UsesSecondCandidate()
    {
        await AssertSecondCandidateUsedAsync(VatpFetchResult.ProtocolFailure("local", Guid.Empty, null, 0));
    }

    [Fact]
    public async Task StorageFailover_ConnectionFailureAfterData_DoesNotUseSecondCandidate()
    {
        await AssertNoSecondCandidateAsync(
            VatpFetchResult.ConnectionFailure("reset", Guid.Empty, default, 1024),
            "400 Service temporarily unavailable");
    }

    [Fact]
    public async Task StorageFailover_RemoteFailureAfterData_DoesNotUseSecondCandidate()
    {
        await AssertNoSecondCandidateAsync(
            VatpFetchResult.RemoteFailure("fail", VatpErrorCode.OpenRejected, Guid.Empty, default, 64),
            "400 Service temporarily unavailable");
    }

    [Fact]
    public async Task StorageFailover_IncompleteBeforeData_DoesNotUseSecondCandidate()
    {
        await AssertNoSecondCandidateAsync(
            VatpFetchResult.IncompleteOrMalformed("meta", VatpErrorCode.InvalidMeta, Guid.Empty, default, 0),
            "400 Service temporarily unavailable");
    }

    [Fact]
    public async Task StorageFailover_IncompleteAfterData_DoesNotUseSecondCandidate()
    {
        await AssertNoSecondCandidateAsync(
            VatpFetchResult.IncompleteOrMalformed("truncated", VatpErrorCode.ArtSizeMismatch, Guid.Empty, default, 32),
            "400 Service temporarily unavailable");
    }

    [Fact]
    public async Task StorageFailover_IdentityMismatch_Returns430_WithoutSecondCandidate()
    {
        var prepared = PrepareArticle(MessageId);
        var other = PrepareArticle("<other@example.test>");
        var alternates = new ManualAlternates();
        alternates.Publish(new StorageArticleCandidate(2, "cache02.usenet.ninja", 564));
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, Guid.NewGuid()) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
            [VatpFetchResult.FromSuccess(other.Record, Guid.NewGuid(), other.Record.ArtId)]);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(0, alternates.WaitCount);
    }

    [Fact]
    public async Task StorageFailover_EligibleWithoutAlternate_PreservesFirstFailure()
    {
        var prepared = PrepareArticle(MessageId);
        var alternates = new ManualAlternates();
        alternates.Publish(null);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, Guid.NewGuid()) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
            [VatpFetchResult.ConnectionFailure("down", Guid.Empty, default, 0)]);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(1, alternates.WaitCount);
    }

    [Fact]
    public async Task StorageFailover_SecondCandidateFails_DoesNotAttemptThird()
    {
        var prepared = PrepareArticle(MessageId);
        var requestId = Guid.NewGuid();
        var alternates = new ManualAlternates();
        alternates.Publish(new StorageArticleCandidate(
            2,
            "cache02.usenet.ninja",
            564));
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
        [
            VatpFetchResult.ConnectionFailure("down", requestId, prepared.Record.ArtId, 0),
            VatpFetchResult.RemoteFailure("open", VatpErrorCode.OpenRejected, requestId, prepared.Record.ArtId, 0),
        ]);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(2, vatp.FetchCount);
        Assert.Equal(requestId, vatp.RequestIds[0]);
        Assert.Equal(requestId, vatp.RequestIds[1]);
    }

    [Fact]
    public async Task StorageFailover_AlternateArrivesDuringFirstAttempt()
    {
        var prepared = PrepareArticle(MessageId);
        var requestId = Guid.NewGuid();
        const int secondPort = 564;
        var alternates = new ManualAlternates();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
            [
                VatpFetchResult.ConnectionFailure("down", requestId, prepared.Record.ArtId, 0),
                VatpFetchResult.FromSuccess(prepared.Record, requestId, prepared.Record.ArtId),
            ],
            entered,
            release.Task);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        var dispatch = DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, vatp.FetchCount);
        alternates.Publish(new StorageArticleCandidate(2, "cache02.usenet.ninja", secondPort));
        release.TrySetResult();
        await dispatch;
        Assert.StartsWith($"220 0 {MessageId}\r\n", Encoding.ASCII.GetString(await duplex.ReadMultilineAsync()), StringComparison.Ordinal);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(2, vatp.FetchCount);
        Assert.Equal(("cache02.usenet.ninja", secondPort), vatp.Endpoints[1]);
    }

    [Fact]
    public async Task StorageFailover_CancellationDuringFirstAttempt_DoesNotUseSecond()
    {
        var prepared = PrepareArticle(MessageId);
        var alternates = new ManualAlternates();
        alternates.Publish(new StorageArticleCandidate(
            2,
            "cache02.usenet.ninja",
            564));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, Guid.NewGuid()) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
            [VatpFetchResult.ConnectionFailure("down", Guid.Empty, default, 0)],
            entered,
            release.Task);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        using var cts = new CancellationTokenSource();
        var dispatch = DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}", cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(0, alternates.WaitCount);
        Assert.Null(await duplex.TryReadClientLineAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task StorageFailover_CancellationWhileWaitingForAlternate_Propagates()
    {
        var prepared = PrepareArticle(MessageId);
        var alternates = new ManualAlternates();
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, Guid.NewGuid()) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
            [VatpFetchResult.ConnectionFailure("down", Guid.Empty, default, 0)]);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        using var cts = new CancellationTokenSource();
        var dispatch = DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}", cts.Token);
        await alternates.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Null(await duplex.TryReadClientLineAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task StorageFailover_CancellationBeforeAlternateDecision_DoesNotFetchSecond()
    {
        var prepared = PrepareArticle(MessageId);
        var alternates = new ManualAlternates();
        alternates.Publish(new StorageArticleCandidate(
            2,
            "cache02.usenet.ninja",
            564));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, Guid.NewGuid()) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
            [
                VatpFetchResult.ConnectionFailure("down", Guid.Empty, default, 0),
                VatpFetchResult.FromSuccess(prepared.Record, Guid.NewGuid(), prepared.Record.ArtId),
            ],
            entered,
            release.Task,
            returnDespiteCancellation: true);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        using var cts = new CancellationTokenSource();
        var dispatch = DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}", cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(0, alternates.WaitCount);
        Assert.Null(await duplex.TryReadClientLineAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task StorageFailover_CancellationClosesOpenLookupWindow_DoesNotBecome400()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateStorageLookupRabbit(factory);
        var time = new FakeTimeProvider();
        var service = new StorageArticleLookupService(
            rabbit,
            Options.Create(CreateStorageLookupOptions()),
            NullLogger<StorageArticleLookupService>.Instance,
            time);
        var window = new CancelClosedLookupWindow(service);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ReleaseProductionWait = release.Task;
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var vatp = new SequenceVatpArticleClient(
            [VatpFetchResult.ConnectionFailure("down", Guid.Empty, default, 0)]);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: window);
        await rabbit.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        try
        {
            var dispatch = DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}", cts.Token);
            var publication = await WaitForStoragePublicationAsync(factory);
            Assert.True(StorageArticleLookupWireProtocol.TryParseRequestV1(publication.Body, out var request, out _));
            var consume = factory.LastConnection!.RpcChannels.First(static channel => channel.ConsumedQueue is not null);
            await consume.DeliverAsync(
                publication.CorrelationId,
                StorageArticleLookupWireProtocol.SerializeResponseV1(
                    CreateStorageLookupResponse(request!, 1, "cache01.usenet.ninja")));

            await window.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, service.OutstandingCorrelations);
            Assert.Equal(1, vatp.FetchCount);
            await cts.CancelAsync();
            await window.WindowClosed!.WaitAsync(TimeSpan.FromSeconds(2));
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);
            Assert.Equal(1, vatp.FetchCount);
            Assert.Null(await duplex.TryReadClientLineAsync(TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            release.TrySetResult();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StorageFailover_ConcurrentCommands_DoNotShareCandidates()
    {
        var prepared = PrepareArticle(MessageId);
        var requestA = Guid.NewGuid();
        var requestB = Guid.NewGuid();
        var alternatesA = new ManualAlternates();
        var alternatesB = new ManualAlternates();
        alternatesA.Publish(new StorageArticleCandidate(2, "cache02.usenet.ninja", 564));
        alternatesB.Publish(new StorageArticleCandidate(3, "cache03.usenet.ninja", 565));
        await using var firstDuplex = await ArticleDuplex.CreateAsync();
        await using var secondDuplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new PairedStorageLookup(
            StorageFound(prepared.Record.ArtId, requestA) with { Alternates = alternatesA },
            StorageFound(prepared.Record.ArtId, requestB, "cache09.usenet.ninja", 569) with { Alternates = alternatesB });
        var vatp = new SequenceVatpArticleClient(
        [
            VatpFetchResult.ConnectionFailure("a", requestA, prepared.Record.ArtId, 0),
            VatpFetchResult.FromSuccess(prepared.Record, requestA, prepared.Record.ArtId),
            VatpFetchResult.ProtocolFailure("b", requestB, prepared.Record.ArtId, 0),
            VatpFetchResult.FromSuccess(prepared.Record, requestB, prepared.Record.ArtId),
        ]);
        var first = firstDuplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        var second = secondDuplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(firstDuplex, first, $"ARTICLE {MessageId}");
        await DispatchLineAsync(secondDuplex, second, $"ARTICLE {MessageId}");
        Assert.StartsWith($"220 0 {MessageId}\r\n", Encoding.ASCII.GetString(await firstDuplex.ReadMultilineAsync()), StringComparison.Ordinal);
        Assert.StartsWith($"220 0 {MessageId}\r\n", Encoding.ASCII.GetString(await secondDuplex.ReadMultilineAsync()), StringComparison.Ordinal);
        Assert.Equal(2, lookup.LookupCount);
        Assert.Equal(
            [
                (StorageFqdn, StoragePort),
                ("cache02.usenet.ninja", 564),
                ("cache09.usenet.ninja", 569),
                ("cache03.usenet.ninja", 565),
            ],
            vatp.Endpoints);
        Assert.Equal([requestA, requestA, requestB, requestB], vatp.RequestIds);
        Assert.Equal(0, rpc.LookupCount);
    }

    [Fact]
    public async Task StorageMalformedEndpoint_Returns400_WithoutArticleWork()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, BackfillFqdn, BackfillPort);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, Guid.NewGuid(), "not-a-cache-uri", 563));
        var vatp = new RecordingVatpArticleClient(VatpFetchResult.ConnectionFailure("unused"));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(0, rpc.LookupCount);
        Assert.Equal(0, vatp.FetchCount);
    }

    [Fact]
    public async Task StorageInvalidArticleId_Returns400_WithoutArticleWork()
    {
        var prepared = PrepareArticle(MessageId);
        var other = PrepareArticle("<other@example.test>");
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, BackfillFqdn, BackfillPort);
        var lookup = new StubStorageLookup(StorageFound(other.Record.ArtId, Guid.NewGuid()));
        var vatp = new RecordingVatpArticleClient(VatpFetchResult.ConnectionFailure("unused"));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(0, rpc.LookupCount);
        Assert.Equal(0, vatp.FetchCount);
    }

    [Theory]
    [InlineData(ArticleWorkOutcome.ArticleNotFound)]
    [InlineData(ArticleWorkOutcome.InvalidArticle)]
    public async Task StorageSilence_ArticleWorkTerminalMiss_Returns430_WithoutVatp(ArticleWorkOutcome outcome)
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(outcome, articleId: null);
        var lookup = new StubStorageLookup(StorageSilence(prepared.Record.ArtId));
        var vatp = new RecordingVatpArticleClient(VatpFetchResult.ConnectionFailure("unused"));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(1, rpc.LookupCount);
        Assert.Equal(0, vatp.FetchCount);
    }

    [Fact]
    public async Task ConcurrentCommands_DoNotShareSourceSelection()
    {
        var stored = PrepareArticle(MessageId);
        var filled = PrepareArticle("<backfill-only@example.test>");
        await using var storedDuplex = await ArticleDuplex.CreateAsync();
        await using var filledDuplex = await ArticleDuplex.CreateAsync();
        var storedLookup = new StubStorageLookup(StorageFound(stored.Record.ArtId, Guid.NewGuid()));
        var filledLookup = new StubStorageLookup(StorageSilence(filled.Record.ArtId));
        var storedRpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, filled.Record.ArtId, BackfillFqdn, BackfillPort);
        var filledRpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, filled.Record.ArtId, BackfillFqdn, BackfillPort);
        var storedVatp = new RecordingVatpArticleClient(
            VatpFetchResult.FromSuccess(stored.Record, Guid.NewGuid(), stored.Record.ArtId));
        var filledVatp = new RecordingVatpArticleClient(
            VatpFetchResult.FromSuccess(filled.Record, Guid.NewGuid(), filled.Record.ArtId));
        var storedQueue = new RecordingIngestionQueue();
        var filledQueue = new RecordingIngestionQueue();
        var storedSession = storedDuplex.CreateSession(
            articleWorkRpc: storedRpc,
            vatp: storedVatp,
            ingestion: storedQueue,
            storageLookup: storedLookup);
        var filledSession = filledDuplex.CreateSession(
            articleWorkRpc: filledRpc,
            vatp: filledVatp,
            ingestion: filledQueue,
            storageLookup: filledLookup);

        await Task.WhenAll(
            DispatchLineAsync(storedDuplex, storedSession, $"ARTICLE {MessageId}"),
            DispatchLineAsync(filledDuplex, filledSession, "ARTICLE <backfill-only@example.test>"));

        Assert.StartsWith(
            $"220 0 {MessageId}\r\n",
            Encoding.ASCII.GetString(await storedDuplex.ReadMultilineAsync()),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "220 0 <backfill-only@example.test>\r\n",
            Encoding.ASCII.GetString(await filledDuplex.ReadMultilineAsync()),
            StringComparison.Ordinal);
        Assert.Equal(0, storedRpc.LookupCount);
        Assert.Equal(1, filledRpc.LookupCount);
        Assert.Equal(StorageFqdn, storedVatp.Fqdn);
        Assert.Equal(StoragePort, storedVatp.VatpPort);
        Assert.Equal(stored.Record.ArtId, storedVatp.ArticleId);
        Assert.Equal(BackfillFqdn, filledVatp.Fqdn);
        Assert.Equal(BackfillPort, filledVatp.VatpPort);
        Assert.Equal(filled.Record.ArtId, filledVatp.ArticleId);
        Assert.Equal(1, storedLookup.LookupCount);
        Assert.Equal(stored.Record.ArtId, storedLookup.LastArticleId);
        Assert.Equal(1, filledLookup.LookupCount);
        Assert.Equal(filled.Record.ArtId, filledLookup.LastArticleId);
        Assert.Equal(0, storedQueue.AdmitCalls);
        Assert.Empty(storedQueue.Admitted);
        Assert.Equal(InboundArticleProducer.BackFiller, Assert.Single(filledQueue.Admitted).Producer);
        Assert.Equal(filled.Record.ArtId, filledQueue.Admitted[0].Record.ArtId);
    }

    private static async Task AssertSecondCandidateUsedAsync(VatpFetchResult firstFailure)
    {
        var prepared = PrepareArticle(MessageId);
        var requestId = Guid.NewGuid();
        const int secondPort = 564;
        var alternates = new ManualAlternates();
        alternates.Publish(new StorageArticleCandidate(2, "cache02.usenet.ninja", secondPort));
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
            [firstFailure, VatpFetchResult.FromSuccess(prepared.Record, requestId, prepared.Record.ArtId)]);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.StartsWith($"220 0 {MessageId}\r\n", Encoding.ASCII.GetString(await duplex.ReadMultilineAsync()), StringComparison.Ordinal);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(2, vatp.FetchCount);
        Assert.Equal(("cache02.usenet.ninja", secondPort), vatp.Endpoints[1]);
        Assert.All(vatp.RequestIds, id => Assert.Equal(requestId, id));
    }

    private static async Task AssertNoSecondCandidateAsync(VatpFetchResult firstFailure, string expectedLine)
    {
        var prepared = PrepareArticle(MessageId);
        var alternates = new ManualAlternates();
        alternates.Publish(new StorageArticleCandidate(
            2,
            "cache02.usenet.ninja",
            564));
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, Guid.NewGuid()) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient([firstFailure]);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal(expectedLine, await duplex.ReadClientLineAsync());
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(0, alternates.WaitCount);
    }

    private static StorageArticleLookupResult StorageFound(
        ArticleId articleId,
        Guid requestId,
        string fqdn = StorageFqdn,
        int vatpPort = StoragePort) =>
        new(
            StorageArticleLookupOutcome.Found,
            requestId,
            articleId,
            1,
            fqdn,
            vatpPort,
            Error: null);

    private static StorageArticleLookupResult StorageSilence(ArticleId articleId) =>
        StorageNotFound(articleId, "Storage article lookup timed out with no positive response.");

    private static StorageArticleLookupResult StorageNotFound(ArticleId articleId, string error) =>
        new(
            StorageArticleLookupOutcome.NotFound,
            Guid.NewGuid(),
            articleId,
            null,
            null,
            null,
            error);

    private static int CountPathToken(string path, string token) =>
        path.Split('!').Count(part => part.Equals(token, StringComparison.OrdinalIgnoreCase));

    private static byte[] DestuffCustomerMultiline(byte[] multiline)
    {
        var span = multiline.AsSpan();
        var firstCrlf = span.IndexOf("\r\n"u8);
        Assert.True(firstCrlf >= 0);
        var stuffed = span[(firstCrlf + 2)..];
        Assert.True(stuffed.EndsWith(".\r\n"u8));
        stuffed = stuffed[..^3];
        Assert.True(NntpArticleDestuffer.TryDestuffStuffedWire(
            stuffed,
            NntpdOptions.DefaultMaxArticleSize,
            out var destuffed));
        return destuffed;
    }

    private static Prepared PrepareArticle(string messageId, string body = "body\r\n")
    {
        var parser = new NntpArticleParser("nntpd.test");
        var created = ArticleRecordFactory.TryCreate(
            parser,
            Encoding.ASCII.GetBytes(CanonicalArticleText.Destuffed(messageId, body)));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return new Prepared(Guid.NewGuid(), created.Record);
    }

    private static async Task DispatchLineAsync(
        ArticleDuplex duplex,
        NntpSession session,
        string command,
        CancellationToken cancellationToken = default)
    {
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, command, cancellationToken);
    }

    private readonly record struct Prepared(Guid RequestId, ArticleRecord Record);

    private sealed class StubArticleWorkRpcClient(
        ArticleWorkOutcome outcome,
        ArticleId? articleId,
        string? fqdn = null,
        int? vatpPort = null) : IArticleWorkRpcClient
    {
        public int LookupCount { get; private set; }

        public Task<ArticleWorkRpcResult> LookupByMessageIdAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken)
        {
            LookupCount++;
            var mid = Encoding.ASCII.GetString(messageId.Span);
            return Task.FromResult(new ArticleWorkRpcResult(
                outcome,
                Guid.NewGuid(),
                mid,
                Backbone: outcome == ArticleWorkOutcome.Success ? "Storage" : null,
                Fqdn: fqdn,
                VatpPort: vatpPort,
                ArticleId: articleId,
                Error: outcome == ArticleWorkOutcome.Success ? null : "missing",
                SourceExchange: outcome == ArticleWorkOutcome.Success ? "cache.requests" : null));
        }
    }

    private sealed class StubVatpArticleClient(VatpFetchResult result) : IVatpArticleClient
    {
        public int FetchCount { get; private set; }

        public Task<VatpFetchResult> FetchArticleAsync(
            string fqdn,
            int vatpPort,
            Guid requestId,
            ArticleId articleId,
            CancellationToken cancellationToken)
        {
            FetchCount++;
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// Returns a successful fetch and then cancels the command token, so the serve write
    /// observes the disconnect after the record exists.
    /// </summary>
    private sealed class CancelOnReturnVatp(VatpFetchResult result, CancellationTokenSource cancel) : IVatpArticleClient
    {
        public int FetchCount { get; private set; }

        public Task<VatpFetchResult> FetchArticleAsync(
            string fqdn,
            int vatpPort,
            Guid requestId,
            ArticleId articleId,
            CancellationToken cancellationToken)
        {
            FetchCount++;
            cancel.Cancel();
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingVatpArticleClient(VatpFetchResult result) : IVatpArticleClient
    {
        public int FetchCount { get; private set; }

        public string? Fqdn { get; private set; }

        public int VatpPort { get; private set; }

        public Guid RequestId { get; private set; }

        public ArticleId ArticleId { get; private set; }

        public Task<VatpFetchResult> FetchArticleAsync(
            string fqdn,
            int vatpPort,
            Guid requestId,
            ArticleId articleId,
            CancellationToken cancellationToken)
        {
            FetchCount++;
            Fqdn = fqdn;
            VatpPort = vatpPort;
            RequestId = requestId;
            ArticleId = articleId;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingArticleWorkRpcClient : IArticleWorkRpcClient
    {
        public int LookupCount { get; private set; }

        public Task<ArticleWorkRpcResult> LookupByMessageIdAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken)
        {
            LookupCount++;
            throw new InvalidOperationException("article-work unavailable");
        }
    }

    private sealed class StubStorageLookup(StorageArticleLookupResult result) : IStorageArticleLookupClient
    {
        public int LookupCount { get; private set; }

        public ArticleId LastArticleId { get; private set; }

        public Task<StorageArticleLookupResult> LookupAsync(ArticleId articleId, CancellationToken cancellationToken)
        {
            LookupCount++;
            LastArticleId = articleId;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingStorageLookup : IStorageArticleLookupClient
    {
        public int LookupCount { get; private set; }

        public Task<StorageArticleLookupResult> LookupAsync(ArticleId articleId, CancellationToken cancellationToken)
        {
            LookupCount++;
            throw new InvalidOperationException("storage lookup unavailable");
        }
    }

    private sealed class GatedStorageLookup(Task release) : IStorageArticleLookupClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LookupCount { get; private set; }

        public bool SawCancellation { get; private set; }

        public async Task<StorageArticleLookupResult> LookupAsync(ArticleId articleId, CancellationToken cancellationToken)
        {
            LookupCount++;
            Entered.TrySetResult();
            try
            {
                await release.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }

            return StorageArticleLookupResult.NotFound(Guid.NewGuid(), articleId, "unexpected");
        }
    }

    private sealed class GatedVatpArticleClient(Task release) : IVatpArticleClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SawCancellation { get; private set; }

        public async Task<VatpFetchResult> FetchArticleAsync(
            string fqdn,
            int vatpPort,
            Guid requestId,
            ArticleId articleId,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await release.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }

            return VatpFetchResult.ConnectionFailure("unexpected");
        }
    }

    private sealed class ManualAlternates : IStorageArticleAlternateSource
    {
        private readonly TaskCompletionSource<StorageArticleCandidate?> _next =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int WaitCount { get; private set; }

        public Task WhenClosed => Task.CompletedTask;

        public void Publish(StorageArticleCandidate? candidate) => _next.TrySetResult(candidate);

        public async ValueTask<StorageArticleCandidate?> WaitForAlternateAsync(CancellationToken cancellationToken)
        {
            WaitCount++;
            Entered.TrySetResult();
            return await _next.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class SequenceVatpArticleClient : IVatpArticleClient
    {
        private readonly Queue<VatpFetchResult> _results;
        private readonly TaskCompletionSource? _firstEntered;
        private readonly Task? _firstRelease;

        private readonly bool _returnDespiteCancellation;

        public SequenceVatpArticleClient(
            IEnumerable<VatpFetchResult> results,
            TaskCompletionSource? firstEntered = null,
            Task? firstRelease = null,
            bool returnDespiteCancellation = false)
        {
            _results = new Queue<VatpFetchResult>(results);
            _firstEntered = firstEntered;
            _firstRelease = firstRelease;
            _returnDespiteCancellation = returnDespiteCancellation;
        }

        public List<(string Fqdn, int VatpPort)> Endpoints { get; } = [];

        public List<Guid> RequestIds { get; } = [];

        public int FetchCount => Endpoints.Count;

        public async Task<VatpFetchResult> FetchArticleAsync(
            string fqdn,
            int vatpPort,
            Guid requestId,
            ArticleId articleId,
            CancellationToken cancellationToken)
        {
            Endpoints.Add((fqdn, vatpPort));
            RequestIds.Add(requestId);
            if (Endpoints.Count == 1 && _firstEntered is not null)
            {
                _firstEntered.TrySetResult();
                if (_firstRelease is not null)
                {
                    if (_returnDespiteCancellation)
                    {
                        await _firstRelease.ConfigureAwait(false);
                    }
                    else
                    {
                        await _firstRelease.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (!_returnDespiteCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return _results.Dequeue();
        }
    }

    /// <summary>
    /// Production lookup whose alternate wait is held until the test has cancelled.
    /// The inner wait then observes the window that cancellation already closed and
    /// returns null instead of throwing, which is the MapFetch-to-400 race.
    /// </summary>
    private sealed class CancelClosedLookupWindow(StorageArticleLookupService service) : IStorageArticleLookupClient
    {
        private WindowClosedAlternate? _alternate;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? ReleaseProductionWait { get; set; }

        public Task? WindowClosed => _alternate?.WhenClosed;

        public async Task<StorageArticleLookupResult> LookupAsync(ArticleId articleId, CancellationToken cancellationToken)
        {
            var result = await service.LookupAsync(articleId, cancellationToken).ConfigureAwait(false);
            if (result.Alternates is null)
            {
                return result;
            }

            _alternate = new WindowClosedAlternate(result.Alternates, Entered, ReleaseProductionWait!);
            return result with { Alternates = _alternate };
        }

        private sealed class WindowClosedAlternate(
            IStorageArticleAlternateSource inner,
            TaskCompletionSource entered,
            Task release) : IStorageArticleAlternateSource
        {
            public Task WhenClosed => inner.WhenClosed;

            public async ValueTask<StorageArticleCandidate?> WaitForAlternateAsync(CancellationToken cancellationToken)
            {
                entered.TrySetResult();
                await release.ConfigureAwait(false);
                return await inner.WaitForAlternateAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static RabbitMqService CreateStorageLookupRabbit(FakeRabbitMqConnectionFactory factory)
    {
        var options = RabbitMqOptionsTests.CreateValid();
        options.PoolReconnectBaseDelayMs = 50;
        options.PoolReconnectMaxDelayMs = 50;
        return new RabbitMqService(
            factory,
            Options.Create(options),
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.ReaderService:nntpd01.usenet.ninja"),
            NullLogger<RabbitMqService>.Instance);
    }

    private static NntpdOptions CreateStorageLookupOptions()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ServerId = 1;
        return options;
    }

    private static StorageArticleLookupResponse CreateStorageLookupResponse(
        StorageArticleLookupRequest request,
        int serverId,
        string fqdn) =>
        new(
            1,
            request.RequestId,
            serverId,
            fqdn,
            request.ArticleId,
            1191);

    private static async Task<FakeRabbitMqRpcPublication> WaitForStoragePublicationAsync(FakeRabbitMqConnectionFactory factory)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var connection = factory.LastConnection;
            if (connection is not null)
            {
                var publication = connection.RpcChannels
                    .SelectMany(static channel => channel.Publications)
                    .FirstOrDefault(static candidate => candidate.Exchange == CacheFleetTopology.RequestsExchangeName);
                if (publication is not null)
                {
                    return publication;
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Expected a cache.requests publication.");
    }

    private sealed class PairedStorageLookup(StorageArticleLookupResult first, StorageArticleLookupResult second)
        : IStorageArticleLookupClient
    {
        private readonly Queue<StorageArticleLookupResult> _results = new([first, second]);

        public int LookupCount { get; private set; }

        public Task<StorageArticleLookupResult> LookupAsync(ArticleId articleId, CancellationToken cancellationToken)
        {
            LookupCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class RecordingIngestionQueue : IArticleIngestionQueue
    {
        public List<InboundArticle> Admitted { get; } = [];

        public ArticleEnqueueResult NextResult { get; set; } = ArticleEnqueueResult.Accepted;

        public ArticleEnqueueResult LastResult { get; private set; }

        public long MemoryLimitBytes => 64 * 1024 * 1024;

        public long QueuedBytes => 0;

        public long PeakQueuedBytes => 0;

        public int MaxArticleBytes => 4 * 1024 * 1024;

        public int Count => Admitted.Count;

        public int PeakCount => Admitted.Count;

        public bool IsAccepting => true;

        public int AdmitCalls { get; private set; }

        public ArticleEnqueueResult TryAdmit(InboundArticle article)
        {
            AdmitCalls++;
            LastResult = NextResult;
            if (NextResult == ArticleEnqueueResult.Accepted)
            {
                Admitted.Add(article);
            }

            return NextResult;
        }

        public bool TryEnqueue(InboundArticle article) => TryAdmit(article) == ArticleEnqueueResult.Accepted;

        public ValueTask<ArticleEnqueueResult> EnqueueAsync(
            InboundArticle article,
            CancellationToken cancellationToken) =>
            new(TryAdmit(article));

        public bool TryProbeCapacity() => true;

        public void Complete()
        {
        }

        public ValueTask<InboundArticle?> DequeueAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<InboundArticle?>(null);
    }

    private sealed class RecordingHistoryDb : IHistoryDb
    {
        public List<ReadOnlyMemory<byte>> Remembered { get; } = [];

        public ValueTask<HistoryLookupResult> LookupAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default) =>
            new(HistoryLookupResult.Unseen);

        public ValueTask<HistoryLookupResult> PeekAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default) =>
            new(HistoryLookupResult.Unseen);

        public void Remember(ReadOnlyMemory<byte> messageId) => Remembered.Add(messageId.ToArray());

        public bool ContainsLocal(in HistoryDigest digest) => false;
    }

    private sealed class ArticleDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public static Task<ArticleDuplex> CreateAsync() => Task.FromResult(new ArticleDuplex());

        public NntpSession CreateSession(
            IArticleWorkRpcClient? articleWorkRpc = null,
            IVatpArticleClient? vatp = null,
            IArticleIngestionQueue? ingestion = null,
            IHistoryDb? historyDb = null,
            IStorageArticleLookupClient? storageLookup = null)
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)));
            var session = new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: ingestion,
                historyDb: historyDb,
                articleWorkRpc: articleWorkRpc,
                vatpArticleClient: vatp,
                storageArticleLookup: storageLookup);
            session.SetAuthorization(Reader);
            return session;
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
            Assert.NotNull(line);
            return line!;
        }

        public async Task<string?> TryReadClientLineAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                return await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        public async Task<byte[]> ReadMultilineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            while (true)
            {
                var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
                Assert.NotNull(line);
                var ascii = Encoding.ASCII.GetBytes(line + "\r\n");
                ascii.CopyTo(buffer.GetSpan(ascii.Length));
                buffer.Advance(ascii.Length);
                if (line == ".")
                {
                    return buffer.WrittenSpan.ToArray();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _clientToServer.Writer.CompleteAsync();
            await _clientToServer.Reader.CompleteAsync();
            await _serverToClient.Writer.CompleteAsync();
            await _serverToClient.Reader.CompleteAsync();
        }
    }

    private sealed class PipeNntpConnection : INntpConnection
    {
        private readonly CancellationTokenSource _cts = new();

        public PipeNntpConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity)
        {
            Input = input;
            Output = output;
            ClientIdentity = identity;
        }

        public PipeReader Input { get; }

        public PipeWriter Output { get; }

        public System.Net.EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;

        public System.Net.EndPoint? LocalEndPoint => null;

        public ConnectionClientIdentity ClientIdentity { get; }

        public bool IsTls => false;

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
        {
            tlsVersion = string.Empty;
            cipher = string.Empty;
            return false;
        }

        public bool IsCompressed => false;

        public CancellationToken ConnectionClosed => _cts.Token;

        public bool IsCompleted => _cts.IsCancellationRequested;

        public long OutboundIdleVersion => 0;

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WaitForOutboundDeliveryAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CompleteAsync(Exception? exception = null)
        {
            _cts.Cancel();
            return Task.CompletedTask;
        }

        public Task UpgradeToTlsAsync(
            VectorNNTPD.Common.Networking.Certificates.ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
