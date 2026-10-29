using System.Net;
using System.Net.Sockets;
using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

/// <summary>
/// SPAMD CHECK coverage against a loopback server. Real SpamAssassin is opt-in only.
/// </summary>
public sealed class PostFilterSpamdLiveTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LocalSpamd_Check_AcceptsEmailLikeScanAsHam()
    {
        var original = Encoding.ASCII.GetBytes(
            "Path: news.example!not-for-mail\r\n" +
            "From: ham@example.com\r\n" +
            "Newsgroups: misc.test\r\n" +
            "Subject: lab ham\r\n" +
            "Message-ID: <ham-vectornntp-lab@example.com>\r\n" +
            "Date: 1 Jan 2026 00:00:00 +0000\r\n" +
            "Xref: nntpd01.usenet.ninja misc.test:1\r\n" +
            "Injection-Info: nntpd01.usenet.ninja; posting-account=poster\r\n" +
            "X-Trace: secret-trace\r\n" +
            "X-Complaints-To: abuse@example.com\r\n" +
            "NNTP-Posting-Host: 203.0.113.9\r\n" +
            "\r\n" +
            "This is a short ham body.\r\n");
        var copy = original.ToArray();
        var article = Article(original, "<ham-vectornntp-lab@example.com>", artHash: 1);
        var context = new SpamdScanContext(IPAddress.Parse("203.0.113.8"), "nntpd01.usenet.ninja", Now);
        var scan = SpamdScanArticleBuilder.Build(article, context);
        var scanText = Encoding.ASCII.GetString(scan);
        Assert.Equal(1, CountOccurrences(scanText, "Received:"));
        Assert.Contains("Received: from [203.0.113.8]", scanText, StringComparison.Ordinal);
        Assert.Contains("To: usenet@nntpd01.usenet.ninja", scanText, StringComparison.Ordinal);
        Assert.Contains("X-Usenet-Newsgroups: misc.test", scanText, StringComparison.Ordinal);
        Assert.Contains("Message-ID: <ham-vectornntp-lab@example.com>", scanText, StringComparison.Ordinal);
        Assert.DoesNotContain("Path:", scanText, StringComparison.Ordinal);
        Assert.DoesNotContain("Xref:", scanText, StringComparison.Ordinal);
        Assert.DoesNotContain("Injection-Info:", scanText, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Trace:", scanText, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Complaints-To:", scanText, StringComparison.Ordinal);
        Assert.DoesNotContain("NNTP-Posting-Host:", scanText, StringComparison.Ordinal);
        Assert.True(copy.AsSpan().SequenceEqual(article.ArtData.Span));

        await using var server = await LocalSpamdServer.StartAsync(keepOpen: true);
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        var result = await client.CheckAsync(article, "poster", Target(server.Port, 1), context, CancellationToken.None);
        Assert.Equal(PostFilterSpamAssassinStatus.Ham, result.Status);
        Assert.Equal(0m, result.Score);
        Assert.Equal(5.0m, result.Threshold);
        Assert.True(copy.AsSpan().SequenceEqual(article.ArtData.Span));
        Assert.Equal(1, metrics.CheckRequests);
        Assert.Equal(1, metrics.ConnectionsEstablished);
        Assert.Equal(1, server.Checks);
        Assert.Equal(1, server.Connections);
        var captured = Assert.Single(server.Requests);
        Assert.True(scan.AsSpan().SequenceEqual(captured));
    }

    [Fact]
    public async Task LocalSpamd_SequentialChecks_ReuseOneConnection()
    {
        var bytes = Encoding.ASCII.GetBytes(
            "From: ham@example.com\r\n" +
            "Newsgroups: misc.test\r\n" +
            "Subject: reuse\r\n" +
            "Message-ID: <ham-reuse-lab@example.com>\r\n" +
            "Date: 1 Jan 2026 00:00:00 +0000\r\n" +
            "\r\n" +
            "short\r\n");
        var article = Article(bytes, "<ham-reuse-lab@example.com>", artHash: 2);
        var context = new SpamdScanContext(IPAddress.Loopback, "nntpd01.usenet.ninja", Now);
        await using var server = await LocalSpamdServer.StartAsync(keepOpen: true);
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        var target = Target(server.Port, 1);
        for (var i = 0; i < 20; i++)
        {
            var result = await client.CheckAsync(article, "poster", target, context);
            Assert.Equal(PostFilterSpamAssassinStatus.Ham, result.Status);
        }

        Assert.Equal(20, metrics.CheckRequests);
        Assert.Equal(1, metrics.ConnectionsEstablished);
        Assert.Equal(19, metrics.ConnectionReuses);
        Assert.Equal(20, server.Checks);
        Assert.Equal(1, server.Connections);
        Assert.True(bytes.AsSpan().SequenceEqual(article.ArtData.Span));
    }

    [Fact]
    public async Task LocalSpamd_SpamResponse_ParsesScoreAndThreshold()
    {
        var spam = "SPAMD/1.1 0 EX_OK\r\nSpam: True ; 6.5 / 5.0\r\n\r\n"u8.ToArray();
        await using var server = await LocalSpamdServer.StartAsync(keepOpen: true, response: spam);
        await using var client = new SpamdCheckClient();
        var result = await client.CheckAsync(SampleArticle(), "poster", Target(server.Port, 1), SampleContext());
        Assert.Equal(PostFilterSpamAssassinStatus.Spam, result.Status);
        Assert.Equal(6.5m, result.Score);
        Assert.Equal(5.0m, result.Threshold);
        Assert.Equal(1, server.Checks);
    }

    [Fact]
    public async Task LocalSpamd_ServerError_IsFailedAndNotReused()
    {
        var error = "SPAMD/1.1 69 EX_TEMPFAIL\r\n\r\n"u8.ToArray();
        await using var server = await LocalSpamdServer.StartAsync(keepOpen: true, response: error);
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        var result = await client.CheckAsync(SampleArticle(), "poster", Target(server.Port, 1), SampleContext());
        Assert.Equal(PostFilterSpamAssassinStatus.Failed, result.Status);
        Assert.Contains("EX_TEMPFAIL", result.Detail, StringComparison.Ordinal);
        Assert.Equal(1, metrics.Evictions);
    }

    [Fact]
    public async Task LocalSpamd_PrematureDisconnect_IsClosed()
    {
        await using var server = await LocalSpamdServer.StartAsync(keepOpen: false, dropAfterRequest: true);
        await using var client = new SpamdCheckClient();
        var result = await client.CheckAsync(SampleArticle(), "poster", Target(server.Port, 1), SampleContext());
        Assert.Equal(PostFilterSpamAssassinStatus.Failed, result.Status);
        Assert.Equal("closed", result.Detail);
        Assert.Equal(1, server.Checks);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task LocalSpamd_MalformedRequest_IsRecorded()
    {
        await using var server = await LocalSpamdServer.StartAsync(keepOpen: false);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = tcp.GetStream();
        var garbage = "NOT-SPAMC\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(garbage);
        await stream.FlushAsync();
        tcp.Client.Shutdown(SocketShutdown.Send);
        await server.Accepted.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => server.ProtocolFaults.Count > 0);
        Assert.Contains("unexpected-command", server.ProtocolFaults);
        server.AcknowledgeProtocolFaults();
    }

    [SpamdIntegrationFact]
    public async Task RealSpamd_Check_AcceptsEmailLikeScanAsHam()
    {
        var (host, port) = SpamdIntegrationFactAttribute.RequireEndpoint();
        var original = Encoding.ASCII.GetBytes(
            "From: ham@example.com\r\n" +
            "Newsgroups: misc.test\r\n" +
            "Subject: lab ham\r\n" +
            "Message-ID: <ham-vectornntp-lab@example.com>\r\n" +
            "Date: 1 Jan 2026 00:00:00 +0000\r\n" +
            "\r\n" +
            "This is a short ham body.\r\n");
        var article = Article(original, "<ham-vectornntp-lab@example.com>", artHash: 1);
        var context = new SpamdScanContext(IPAddress.Parse("203.0.113.8"), "nntpd01.usenet.ninja", Now);
        var scan = SpamdScanArticleBuilder.Build(article, context);
        var target = Target(port, 1, host);
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        var result = await client.CheckAsync(article, "poster", target, context, CancellationToken.None);
        Assert.Equal(PostFilterSpamAssassinStatus.Ham, result.Status);
        Assert.True(original.AsSpan().SequenceEqual(article.ArtData.Span));

        var symbols = await RequestSymbolsAsync(host, port, scan);
        Assert.Contains("SPAMD/", symbols, StringComparison.Ordinal);
        Assert.Contains("EX_OK", symbols, StringComparison.Ordinal);
        Assert.DoesNotContain("NO_RECEIVED", symbols, StringComparison.Ordinal);
        Assert.DoesNotContain("MISSING_HEADERS", symbols, StringComparison.Ordinal);
        Assert.Equal(1, metrics.CheckRequests);
        Assert.Equal(1, metrics.ConnectionsEstablished);
    }

    [SpamdIntegrationFact]
    public async Task RealSpamd_SequentialChecks_ReportConnectionReuse()
    {
        var (host, port) = SpamdIntegrationFactAttribute.RequireEndpoint();
        var bytes = Encoding.ASCII.GetBytes(
            "From: ham@example.com\r\n" +
            "Newsgroups: misc.test\r\n" +
            "Subject: reuse\r\n" +
            "Message-ID: <ham-reuse-lab@example.com>\r\n" +
            "Date: 1 Jan 2026 00:00:00 +0000\r\n" +
            "\r\n" +
            "short\r\n");
        var article = Article(bytes, "<ham-reuse-lab@example.com>", artHash: 2);
        var context = new SpamdScanContext(IPAddress.Loopback, "nntpd01.usenet.ninja", Now);
        var target = Target(port, 1, host);
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        for (var i = 0; i < 20; i++)
        {
            var result = await client.CheckAsync(article, "poster", target, context);
            Assert.Equal(PostFilterSpamAssassinStatus.Ham, result.Status);
        }

        Assert.Equal(20, metrics.CheckRequests);
        Assert.True(metrics.ConnectionsEstablished >= 1);
        Assert.True(metrics.ConnectionsEstablished <= 20);
    }

    private static PostFilterSpamAssassinTarget Target(int port, int maxConnections, string host = "127.0.0.1") =>
        new(
            [host],
            port,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            PostFilterSpamAssassinOptions.DefaultProtocolVersion,
            maxConnections,
            PostFilterSpamAssassinHostSelection.RoundRobin);

    private static ArticleRecord SampleArticle() =>
        Article(
            "From: a@b\r\nNewsgroups: misc.test\r\nSubject: t\r\nMessage-ID: <h@example.com>\r\nDate: 1 Jan 2026 00:00:00 +0000\r\n\r\nbody\r\n"u8.ToArray(),
            "<h@example.com>",
            1);

    private static SpamdScanContext SampleContext() =>
        new(IPAddress.Loopback, "nntpd01.usenet.ninja", Now);

    private static ArticleRecord Article(byte[] bytes, string messageId, ulong artHash) =>
        new(
            ArticleId.FromMessageId(Encoding.ASCII.GetBytes(messageId)),
            artHash,
            ArticleType.Default,
            artLines: 1,
            Now.UtcDateTime,
            ArticleParseStatus.CanonicalV1,
            bytes,
            fields: default);

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private static async Task<string> RequestSymbolsAsync(string host, int port, byte[] scan)
    {
        using var tcp = new TcpClient();
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await tcp.ConnectAsync(host, port, connectCts.Token);
        await using var stream = tcp.GetStream();
        var header = Encoding.ASCII.GetBytes(
            $"SYMBOLS SPAMC/1.5\r\nContent-length: {scan.Length}\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(scan);
        await stream.FlushAsync();
        tcp.Client.Shutdown(SocketShutdown.Send);
        var buffer = new byte[4096];
        var total = 0;
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), readCts.Token);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return Encoding.ASCII.GetString(buffer.AsSpan(0, total));
    }
}
