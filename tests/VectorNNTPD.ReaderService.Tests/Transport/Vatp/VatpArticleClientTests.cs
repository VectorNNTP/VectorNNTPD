using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Transport.ArticleTransfer;
using VectorNNTPD.ReaderService.Transport.Vatp;

namespace VectorNNTPD.ReaderService.Tests.Transport.Vatp;

public sealed class VatpArticleClientTests
{
    /// <summary>Body larger than the production initial window so WINDOW replenishment is required.</summary>
    private static int BodyLargerThanInitialWindow =>
        ArticleTransferLimits.Default.InitialStreamWindowBytes + (64 * 1024);

    [Fact]
    public async Task Coalesced_multi_frame_transfer_succeeds()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.CoalesceOutboundFrames;
        var prepared = CreateArticle("<coalesce-multi@example.test>", body: BuildLargeBody(96 * 1024));
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);

        var result = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);

        Assert.Equal(VatpFetchKind.Success, result.Kind);
        Assert.Equal(prepared.Record.ArtId, result.Record.ArtId);
        Assert.Equal(prepared.Record.ArtSize, result.Record.ArtSize);
        Assert.Equal(prepared.Record.ArtHash, result.Record.ArtHash);
    }

    [Fact]
    public async Task Coalesced_240KiB_article_matches_live_failure_scale()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.CoalesceOutboundFrames;
        var prepared = CreateArticle("<coalesce-240k@example.test>", body: BuildLargeBody(240 * 1024));
        Assert.True(prepared.Record.ArtSize > VatpProtocol.DefaultMaxFramePayload);
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);

        var result = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);

        Assert.Equal(VatpFetchKind.Success, result.Kind);
        Assert.Equal(prepared.Record.ArtId, result.Record.ArtId);
        Assert.Equal(prepared.Record.ArtSize, result.Record.ArtSize);
        Assert.Equal(prepared.Record.ArtHash, result.Record.ArtHash);
        Assert.True(result.Record.ArtData.ToArray().AsSpan()
            .SequenceEqual(prepared.Record.ArtData.ToArray()));
    }

    [Fact]
    public async Task Article_larger_than_initial_window_completes_with_window_pacing()
    {
        // Articles larger than the initial window must complete by refunding each accepted DATA chunk.
        // A missing local replenish is a flow-control failure.
        await using var server = VatpLoopbackTestServer.Start();
        var prepared = CreateArticle("<window-740k@example.test>", body: BuildLargeBody(BodyLargerThanInitialWindow));
        Assert.True(prepared.Record.ArtSize > ArticleTransferLimits.Default.InitialStreamWindowBytes);
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);

        var result = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);

        Assert.Equal(VatpFetchKind.Success, result.Kind);
        Assert.Equal(prepared.Record.ArtId, result.Record.ArtId);
        Assert.Equal(prepared.Record.ArtSize, result.Record.ArtSize);
        Assert.Equal(prepared.Record.ArtHash, result.Record.ArtHash);
        Assert.True(result.Record.ArtData.ToArray().AsSpan()
            .SequenceEqual(prepared.Record.ArtData.ToArray()));
    }

    [Fact]
    public async Task Eight_concurrent_large_articles_share_connection_with_independent_windows()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var articles = new PreparedArticle[8];
        for (var i = 0; i < articles.Length; i++)
        {
            var bodyBytes = i % 2 == 0 ? BodyLargerThanInitialWindow : 64 * 1024;
            articles[i] = CreateArticle($"<mux-large-{i}@example.test>", body: BuildLargeBody(bodyBytes));
            server.Register(articles[i].Record, articles[i].SelectedDateHeaderName);
        }

        Assert.Contains(
            articles,
            a => a.Record.ArtSize > ArticleTransferLimits.Default.InitialStreamWindowBytes);

        var pool = CreatePool(server);
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);
        var tasks = articles.Select(a =>
            client.FetchArticleAsync(server.Host,
            server.Port, a.RequestId, a.Record.ArtId, CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(VatpFetchKind.Success, r.Kind));
        for (var i = 0; i < articles.Length; i++)
        {
            Assert.Equal(articles[i].Record.ArtId, results[i].Record.ArtId);
            Assert.Equal(articles[i].Record.ArtSize, results[i].Record.ArtSize);
            Assert.Equal(articles[i].Record.ArtHash, results[i].Record.ArtHash);
        }

        Assert.Equal(1, pool.ConnectionCountForTest(server.Host, server.Port));
    }

    [Fact]
    public async Task Cancel_one_large_stream_does_not_fail_sibling_large_stream()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var keep = CreateArticle("<keep-large@example.test>", body: BuildLargeBody(BodyLargerThanInitialWindow));
        var cancel = CreateArticle("<cancel-large@example.test>", body: BuildLargeBody(BodyLargerThanInitialWindow));
        Assert.True(keep.Record.ArtSize > ArticleTransferLimits.Default.InitialStreamWindowBytes);
        Assert.True(cancel.Record.ArtSize > ArticleTransferLimits.Default.InitialStreamWindowBytes);
        var heldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Register(keep.Record, keep.SelectedDateHeaderName);
        server.Register(
            cancel.Record,
            cancel.SelectedDateHeaderName,
            VatpLoopbackTestServer.TransferMode.HoldAfterFirstData,
            heldStarted);

        var pool = CreatePool(server);
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);
        using var cts = new CancellationTokenSource();

        var keepTask = client.FetchArticleAsync(
            server.Host,
            server.Port,
            keep.RequestId,
            keep.Record.ArtId,
            CancellationToken.None);
        var cancelTask = client.FetchArticleAsync(
            server.Host,
            server.Port,
            cancel.RequestId,
            cancel.Record.ArtId,
            cts.Token);

        await heldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        var keepResult = await keepTask;
        var cancelResult = await cancelTask;
        Assert.Equal(VatpFetchKind.Success, keepResult.Kind);
        Assert.Equal(keep.Record.ArtId, keepResult.Record.ArtId);
        Assert.Equal(keep.Record.ArtSize, keepResult.Record.ArtSize);
        Assert.True(
            cancelResult.Kind is VatpFetchKind.Cancelled
                or VatpFetchKind.ConnectionFailure
                or VatpFetchKind.IncompleteOrMalformedArticle,
            cancelResult.Kind.ToString());
        Assert.NotEqual(VatpFetchKind.Success, cancelResult.Kind);
        Assert.Equal(default, cancelResult.Record);

        // Connection must remain usable for a subsequent large transfer.
        var retry = CreateArticle("<post-cancel-large@example.test>", body: BuildLargeBody(740 * 1024));
        server.Register(retry.Record, retry.SelectedDateHeaderName);
        var retryResult = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            retry.RequestId,
            retry.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.Success, retryResult.Kind);
        Assert.Equal(retry.Record.ArtSize, retryResult.Record.ArtSize);
    }

    [Fact]
    public async Task Tiny_writes_reconstruct_partial_frames_across_reads()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.TinyOutboundWrites;
        var prepared = CreateArticle("<tiny-writes@example.test>", body: BuildLargeBody(8 * 1024));
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);

        var result = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);

        Assert.Equal(VatpFetchKind.Success, result.Kind);
        Assert.Equal(prepared.Record.ArtId, result.Record.ArtId);
        Assert.Equal(prepared.Record.ArtSize, result.Record.ArtSize);
    }

    [Fact]
    public async Task Max_sized_single_data_frame_assembles_across_32KiB_reads()
    {
        // Live root cause: assembling one DefaultMaxFramePayload DATA across multiple
        // 32 KiB socket reads must not trip the old one-frame accumulation guard.
        await using var server = VatpLoopbackTestServer.Start();
        var bodyBytes = (int)VatpProtocol.DefaultMaxFramePayload - 256;
        var prepared = CreateArticle("<max-frame@example.test>", body: BuildLargeBody(bodyBytes));
        Assert.True(prepared.Record.ArtSize > 32 * 1024);
        Assert.True(prepared.Record.ArtSize <= VatpProtocol.DefaultMaxFramePayload);
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);

        var result = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);

        Assert.Equal(VatpFetchKind.Success, result.Kind);
        Assert.Equal(prepared.Record.ArtSize, result.Record.ArtSize);
        Assert.Equal(prepared.Record.ArtHash, result.Record.ArtHash);
    }

    [Fact]
    public async Task Oversized_data_frame_still_rejected()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.OversizedDataFrame;
        var prepared = CreateArticle("<oversized-frame@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);

        var result = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);

        Assert.Equal(VatpFetchKind.ConnectionFailure, result.Kind);
        Assert.NotEqual(VatpFetchKind.Success, result.Kind);
        Assert.Equal(default, result.Record);
    }

    [Fact]
    public async Task Hello_and_single_transfer_success()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var prepared = CreateArticle("<vatp-success@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);
        var result = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.Success, result.Kind);
        Assert.Equal(prepared.Record.ArtId, result.Record.ArtId);
    }

    [Fact]
    public void Fin_without_end_does_not_complete_on_receive_stream()
    {
        var prepared = CreateArticle("<fin-only@example.test>");
        var stream = new ArticleTransferReceiveStream(1, prepared.RequestId, prepared.Record.ArtId);
        var record = prepared.Record;
        var meta = ArticleCanonicalTransferMeta.FromRecord(in record, prepared.SelectedDateHeaderName);
        var metaBytes = VatpMetaCodec.Encode(in meta);
        Assert.True(stream.TryAcceptMeta(metaBytes).Success);
        var artData = prepared.Record.ArtData.ToArray();
        Assert.True(stream.TryAcceptData(artData, fin: true).Success);
        Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
        Assert.False(stream.HasConsumableRecord);
        Assert.False(stream.TryTakeRecord(out _));
    }

    [Fact]
    public async Task Wrong_article_id_open_yields_remote_failure()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var prepared = CreateArticle("<registered@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var wrongId = ArticleId.FromMessageId("<other@example.test>"u8);
        var client = CreateClient(server);
        var result = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            wrongId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.RemoteTransferFailure, result.Kind);
        Assert.Equal(VatpErrorCode.OpenRejected, result.ErrorCode);
    }

    [Fact]
    public async Task Eight_concurrent_streams_share_one_connection()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var articles = new PreparedArticle[8];
        for (var i = 0; i < articles.Length; i++)
        {
            articles[i] = CreateArticle($"<mux{i}@example.test>");
            server.Register(articles[i].Record, articles[i].SelectedDateHeaderName);
        }

        var pool = CreatePool(server);
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);
        var tasks = articles.Select(a =>
            client.FetchArticleAsync(server.Host,
            server.Port, a.RequestId, a.Record.ArtId, CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(VatpFetchKind.Success, r.Kind));
        Assert.Equal(1, pool.ConnectionCountForTest(server.Host, server.Port));
    }

    [Fact]
    public async Task Cancel_one_stream_returns_cancelled()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.FinWithoutEnd;
        var prepared = CreateArticle("<cancel-me@example.test>", body: BuildLargeBody(512 * 1024));
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var result = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            cts.Token);
        Assert.True(
            result.Kind is VatpFetchKind.Cancelled or VatpFetchKind.ConnectionFailure or VatpFetchKind.IncompleteOrMalformedArticle,
            result.Kind.ToString());
    }

    [Fact]
    public async Task Connection_loss_mid_data_fails_fetch()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.DisconnectMidData;
        var prepared = CreateArticle("<drop@example.test>", body: BuildLargeBody(256 * 1024));
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);
        var result = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.ConnectionFailure, result.Kind);
    }

    [Fact]
    public async Task Pool_removes_dead_connection()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var prepared = CreateArticle("<dead-conn@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var pool = CreatePool(server);
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);
        server.Mode = VatpLoopbackTestServer.TransferMode.DisconnectMidData;
        _ = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        await WaitUntilAsync(() => pool.AliveConnectionCount(server.Host, server.Port) == 0, TimeSpan.FromSeconds(3));
        server.Mode = VatpLoopbackTestServer.TransferMode.Complete;
        var retry = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.Success, retry.Kind);
    }

    [Fact]
    public async Task Tls_wrong_target_host_fails_without_test_callback()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var prepared = CreateArticle("<tls-host@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var pool = new VatpConnectionPool(NullLogger<VatpConnectionPool>.Instance)
        {
            TestTcpConnectHost = IPAddress.Loopback.ToString(),
        };
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);
        var result = await client.FetchArticleAsync(
            "wrong.host.test",
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.ConnectionFailure, result.Kind);
    }

    [Fact]
    public async Task Fin_without_end_over_tls_does_not_succeed()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.FinWithoutEnd;
        var prepared = CreateArticle("<fin-no-end@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);
        var result = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.ConnectionFailure, result.Kind);
    }

    [Fact]
    public async Task ConnectionLoss_MidMeta_FailsTransferCleanly()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.DisconnectMidMeta;
        var prepared = CreateArticle("<mid-meta@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var pool = CreatePool(server);
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);

        var result = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);

        Assert.Equal(VatpFetchKind.ConnectionFailure, result.Kind);
        Assert.NotEqual(VatpFetchKind.Success, result.Kind);
        Assert.Equal(default, result.Record);
        await WaitUntilAsync(() => pool.AliveConnectionCount(server.Host, server.Port) == 0, TimeSpan.FromSeconds(3));

        server.Mode = VatpLoopbackTestServer.TransferMode.Complete;
        var retry = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.Success, retry.Kind);
        Assert.Equal(prepared.Record.ArtId, retry.Record.ArtId);
    }

    [Fact]
    public async Task ConnectionLoss_AfterFinBeforeEnd_FailsTransferCleanly()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.DisconnectAfterFinBeforeEnd;
        var prepared = CreateArticle("<fin-before-end@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var pool = CreatePool(server);
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);

        var result = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);

        Assert.True(
            result.Kind is VatpFetchKind.ConnectionFailure or VatpFetchKind.IncompleteOrMalformedArticle,
            result.Kind.ToString());
        Assert.NotEqual(VatpFetchKind.Success, result.Kind);
        Assert.Equal(default, result.Record);
        await WaitUntilAsync(() => pool.AliveConnectionCount(server.Host, server.Port) == 0, TimeSpan.FromSeconds(3));

        server.Mode = VatpLoopbackTestServer.TransferMode.Complete;
        var retry = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.Success, retry.Kind);
        Assert.Equal(prepared.Record.ArtId, retry.Record.ArtId);
    }

    [Fact]
    public async Task Late_and_never_allocated_stream_frames_do_not_poison_connection()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.EmitSpuriousFramesAfterTerminal = true;
        var first = CreateArticle("<stream-lifecycle-1@example.test>");
        var second = CreateArticle("<stream-lifecycle-2@example.test>");
        server.Register(first.Record, first.SelectedDateHeaderName);
        server.Register(second.Record, second.SelectedDateHeaderName);

        var pool = CreatePool(server);
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);

        var r1 = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            first.RequestId,
            first.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.Success, r1.Kind);
        Assert.Equal(first.Record.ArtId, r1.Record.ArtId);

        // Spurious late END/FAIL for stream 1 and META/DATA/END for 0x7FFFFFFE must not
        // inject state or kill the pooled connection.
        var r2 = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            second.RequestId,
            second.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.Success, r2.Kind);
        Assert.Equal(second.Record.ArtId, r2.Record.ArtId);
        Assert.Equal(second.Record.ArtSize, r2.Record.ArtSize);
        Assert.Equal(1, pool.AliveConnectionCount(server.Host, server.Port));

        var openIds = server.ObservedOpenStreamIds.OrderBy(static id => id).ToArray();
        Assert.Equal(new uint[] { 1, 2 }, openIds);
        Assert.DoesNotContain(0x7FFFFFFEu, openIds);
    }

    [Fact]
    public async Task Stream_ids_are_monotonic_and_not_reused_on_same_connection()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var pool = CreatePool(server);
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);

        for (var i = 0; i < 4; i++)
        {
            var prepared = CreateArticle($"<stream-mono-{i}@example.test>");
            server.Register(prepared.Record, prepared.SelectedDateHeaderName);
            var result = await client.FetchArticleAsync(
                server.Host,
            server.Port,
                prepared.RequestId,
                prepared.Record.ArtId,
                CancellationToken.None);
            Assert.Equal(VatpFetchKind.Success, result.Kind);
        }

        var ids = server.ObservedOpenStreamIds.OrderBy(static id => id).ToArray();
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, ids);
        Assert.Equal(1, pool.ConnectionCountForTest(server.Host, server.Port));
    }

    [Fact]
    public async Task Active_stream_id_is_not_reused_while_sibling_is_held()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var held = CreateArticle("<held-active-id@example.test>", body: BuildLargeBody(64 * 1024));
        var other = CreateArticle("<other-while-held@example.test>");
        var heldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Register(
            held.Record,
            held.SelectedDateHeaderName,
            VatpLoopbackTestServer.TransferMode.HoldAfterFirstData,
            heldStarted);
        server.Register(other.Record, other.SelectedDateHeaderName);

        var pool = CreatePool(server);
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);
        using var cts = new CancellationTokenSource();

        var heldTask = client.FetchArticleAsync(
            server.Host,
            server.Port,
            held.RequestId,
            held.Record.ArtId,
            cts.Token);
        await heldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var otherResult = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            other.RequestId,
            other.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.Success, otherResult.Kind);

        await cts.CancelAsync();
        var heldResult = await heldTask;
        Assert.NotEqual(VatpFetchKind.Success, heldResult.Kind);

        var ids = server.ObservedOpenStreamIds.ToArray();
        Assert.Equal(2, ids.Length);
        Assert.Contains(1u, ids);
        Assert.Contains(2u, ids);
        Assert.Equal(2, ids.Distinct().Count());
    }

    [Fact]
    public async Task Cancelled_stream_spurious_frames_do_not_fail_sibling()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.EmitSpuriousFramesAfterTerminal = true;
        var keep = CreateArticle("<keep-after-cancel-spurious@example.test>", body: BuildLargeBody(128 * 1024));
        var cancel = CreateArticle("<cancel-spurious@example.test>", body: BuildLargeBody(128 * 1024));
        var heldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Register(keep.Record, keep.SelectedDateHeaderName);
        server.Register(
            cancel.Record,
            cancel.SelectedDateHeaderName,
            VatpLoopbackTestServer.TransferMode.HoldAfterFirstData,
            heldStarted);

        var pool = CreatePool(server);
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);
        using var cts = new CancellationTokenSource();

        var keepTask = client.FetchArticleAsync(
            server.Host,
            server.Port,
            keep.RequestId,
            keep.Record.ArtId,
            CancellationToken.None);
        var cancelTask = client.FetchArticleAsync(
            server.Host,
            server.Port,
            cancel.RequestId,
            cancel.Record.ArtId,
            cts.Token);

        await heldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        var keepResult = await keepTask;
        var cancelResult = await cancelTask;
        Assert.Equal(VatpFetchKind.Success, keepResult.Kind);
        Assert.Equal(keep.Record.ArtId, keepResult.Record.ArtId);
        Assert.NotEqual(VatpFetchKind.Success, cancelResult.Kind);
        Assert.Equal(1, pool.AliveConnectionCount(server.Host, server.Port));
    }

    [Fact]
    public async Task Meta_on_connection_stream_id_marks_connection_dead()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.MetaOnConnectionStreamId;
        var prepared = CreateArticle("<stream0-meta@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var pool = CreatePool(server);
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);

        var result = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.ConnectionFailure, result.Kind);
        await WaitUntilAsync(() => pool.AliveConnectionCount(server.Host, server.Port) == 0, TimeSpan.FromSeconds(3));

        server.Mode = VatpLoopbackTestServer.TransferMode.Complete;
        var retry = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.Success, retry.Kind);
    }

    [Fact]
    public async Task AcceptedDataBytes_ProtocolFailureBeforeStream_IsZero()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var prepared = CreateArticle("<pre-stream-protocol@example.test>");
        var client = CreateClient(server);

        var emptyRequest = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            Guid.Empty,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.ProtocolFailure, emptyRequest.Kind);
        Assert.Equal(0, emptyRequest.AcceptedDataBytes);

        var badEndpoint = await client.FetchArticleAsync(
            "vatp" + "://" + "backfiller.test",
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.ProtocolFailure, badEndpoint.Kind);
        Assert.Equal(0, badEndpoint.AcceptedDataBytes);
    }

    [Fact]
    public async Task AcceptedDataBytes_ConnectionFailureBeforeStream_IsZero()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var prepared = CreateArticle("<pre-stream-connect@example.test>");
        var pool = new VatpConnectionPool(NullLogger<VatpConnectionPool>.Instance)
        {
            TestTcpConnectHost = IPAddress.Loopback.ToString(),
        };
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);
        var result = await client.FetchArticleAsync(
            "wrong.host.test",
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.ConnectionFailure, result.Kind);
        Assert.Equal(0, result.AcceptedDataBytes);
    }

    [Fact]
    public async Task AcceptedDataBytes_ConnectionFailureBeforeData_IsZero()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.DisconnectMidMeta;
        var prepared = CreateArticle("<mid-meta-bytes@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var result = await FetchAsync(server, prepared);
        Assert.Equal(VatpFetchKind.ConnectionFailure, result.Kind);
        Assert.Equal(0, result.AcceptedDataBytes);
    }

    [Fact]
    public async Task AcceptedDataBytes_ConnectionFailureAfterData_IsPositive()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.DisconnectMidData;
        var prepared = CreateArticle("<mid-data-bytes@example.test>", body: BuildLargeBody(256 * 1024));
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        Assert.True(prepared.Record.ArtData.Length > 64 * 1024);
        var result = await FetchAsync(server, prepared);
        Assert.Equal(VatpFetchKind.ConnectionFailure, result.Kind);
        Assert.Equal(64 * 1024, result.AcceptedDataBytes);
    }

    [Fact]
    public async Task AcceptedDataBytes_RemoteFailureBeforeData_IsZero()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var prepared = CreateArticle("<registered-bytes@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var wrongId = ArticleId.FromMessageId("<other-bytes@example.test>"u8);
        var client = CreateClient(server);
        var result = await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            wrongId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.RemoteTransferFailure, result.Kind);
        Assert.Equal(VatpErrorCode.OpenRejected, result.ErrorCode);
        Assert.Equal(0, result.AcceptedDataBytes);
    }

    [Fact]
    public async Task AcceptedDataBytes_RemoteFailureAfterData_IsProbeLength()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.DataThenRemoteFail;
        var prepared = CreateProbeArticle("<fail-after-data@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var result = await FetchAsync(server, prepared);
        Assert.Equal(VatpFetchKind.RemoteTransferFailure, result.Kind);
        Assert.Equal(VatpErrorCode.OpenRejected, result.ErrorCode);
        Assert.Equal(VatpLoopbackTestServer.ProbePayloadBytes, result.AcceptedDataBytes);
    }

    [Fact]
    public async Task AcceptedDataBytes_MetaRejected_IsZero()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.InvalidMeta;
        var prepared = CreateProbeArticle("<bad-meta-bytes@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var result = await FetchAsync(server, prepared);
        Assert.Equal(VatpFetchKind.IncompleteOrMalformedArticle, result.Kind);
        Assert.Equal(0, result.AcceptedDataBytes);
    }

    [Fact]
    public async Task AcceptedDataBytes_ShortFin_IsCopiedLength()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.ShortDataFin;
        var prepared = CreateProbeArticle("<short-fin-bytes@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var result = await FetchAsync(server, prepared);
        Assert.Equal(VatpFetchKind.IncompleteOrMalformedArticle, result.Kind);
        Assert.Equal(VatpErrorCode.ArtSizeMismatch, result.ErrorCode);
        Assert.Equal(VatpLoopbackTestServer.ProbePayloadBytes, result.AcceptedDataBytes);
        Assert.NotEqual(prepared.Record.ArtSize, result.AcceptedDataBytes);
    }

    [Fact]
    public async Task AcceptedDataBytes_LaterDataRejectedBeforeCopy_KeepsFirstChunk()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.DataThenSizeReject;
        var prepared = CreateProbeArticle("<size-reject-bytes@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var result = await FetchAsync(server, prepared);
        Assert.Equal(VatpFetchKind.IncompleteOrMalformedArticle, result.Kind);
        Assert.Equal(VatpErrorCode.ArtSizeMismatch, result.ErrorCode);
        Assert.Equal(VatpLoopbackTestServer.ProbePayloadBytes, result.AcceptedDataBytes);
    }

    [Fact]
    public async Task AcceptedDataBytes_HashMismatchAfterFullBody_IsArtSize()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.CorruptArtHash;
        var prepared = CreateProbeArticle("<hash-mismatch-bytes@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var result = await FetchAsync(server, prepared);
        Assert.Equal(VatpFetchKind.IncompleteOrMalformedArticle, result.Kind);
        Assert.Equal(VatpErrorCode.ArtHashMismatch, result.ErrorCode);
        Assert.Equal(prepared.Record.ArtSize, result.AcceptedDataBytes);
        Assert.True(result.AcceptedDataBytes > VatpLoopbackTestServer.ProbePayloadBytes);
    }

    [Fact]
    public async Task AcceptedDataBytes_Success_IsArtSize()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var prepared = CreateProbeArticle("<success-bytes@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var result = await FetchAsync(server, prepared);
        Assert.Equal(VatpFetchKind.Success, result.Kind);
        Assert.Equal(prepared.Record.ArtId, result.Record.ArtId);
        Assert.Equal(prepared.Record.ArtSize, result.AcceptedDataBytes);
        Assert.Equal(prepared.Record.ArtData.Length, result.AcceptedDataBytes);
    }

    private static async Task<VatpFetchResult> FetchAsync(VatpLoopbackTestServer server, PreparedArticle prepared)
    {
        var client = CreateClient(server);
        return await client.FetchArticleAsync(
            server.Host,
            server.Port,
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
    }

    private static PreparedArticle CreateProbeArticle(string messageId)
    {
        var prepared = CreateArticle(messageId);
        Assert.True(prepared.Record.ArtData.Length > VatpLoopbackTestServer.ProbePayloadBytes);
        Assert.Equal(prepared.Record.ArtData.Length, prepared.Record.ArtSize);
        return prepared;
    }

    private static VatpArticleClient CreateClient(VatpLoopbackTestServer server) =>
        new(CreatePool(server), NullLogger<VatpArticleClient>.Instance);

    private static VatpConnectionPool CreatePool(VatpLoopbackTestServer server)
    {
        var pool = new VatpConnectionPool(
            NullLogger<VatpConnectionPool>.Instance,
            new VatpClientOptions
            {
                IoTimeout = TimeSpan.FromSeconds(2),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                TlsHandshakeTimeout = TimeSpan.FromSeconds(5),
            })
        {
            ServerCertificateValidationCallback = static (_, _, _, _) => true,
            TestTcpConnectHost = IPAddress.Loopback.ToString(),
        };
        return pool;
    }

    private static PreparedArticle CreateArticle(string messageId, string body = "body\r\n")
    {
        var parser = new NntpArticleParser("backfiller.test");
        var destuffed = BuildDestuffed(messageId, body);
        var created = ArticleRecordFactory.TryCreate(parser, destuffed);
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return new PreparedArticle(Guid.NewGuid(), created.Record, created.SelectedDateHeaderName);
    }

    private const string BodyLine = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\r\n";

    private static string BuildLargeBody(int minimumBytes)
    {
        var builder = new System.Text.StringBuilder(minimumBytes + BodyLine.Length);
        while (builder.Length < minimumBytes)
        {
            builder.Append(BodyLine);
        }

        return builder.ToString();
    }

    private static byte[] BuildDestuffed(string messageId, string body) =>
        System.Text.Encoding.ASCII.GetBytes(
            "Path: peer.example\r\n"
            + "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n"
            + "Message-ID: " + messageId + "\r\n"
            + "Newsgroups: alt.test\r\n"
            + "From: user@example.test\r\n"
            + "Subject: s\r\n"
            + "\r\n"
            + body);

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("Timed out waiting for condition.");
    }

    private readonly record struct PreparedArticle(
        Guid RequestId,
        ArticleRecord Record,
        NntpArticleHeaderName SelectedDateHeaderName);
}

internal static class VatpConnectionPoolTestExtensions
{
    public static int ConnectionCountForTest(this VatpConnectionPool pool, string host, int port) =>
        pool.GetEndpointConnectionCount(host, port);

    public static int AliveConnectionCount(this VatpConnectionPool pool, string host, int port) =>
        pool.GetEndpointAliveConnectionCount(host, port);
}
