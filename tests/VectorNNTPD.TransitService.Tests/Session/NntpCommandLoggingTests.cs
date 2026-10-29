using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Newsgroups;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Authentication;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.Networking.Transport;

namespace VectorNNTPD.TransitService.Tests.Session;

/// <summary>Command RX/TX logging contract (ownership, redaction, timing shape).</summary>
[Collection(nameof(NntpCommandLoggerCollection))]
public sealed class NntpCommandLoggingTests
{
    private static readonly Regex TxPattern = new(
        @"TX:\s+(?<cmd>.+?)(?:\s+\[(?<status>[^\]]*)\])?\s+executed in (?<sec>\d+\.\d{3})s",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [Fact]
    public async Task Capabilities_ProducesExactlyOneRxAndOneTx_UnderCapabilitiesLogger()
    {
        var recording = new RecordingLoggerFactory();
        await using var duplex = await LoggingDuplex.CreateAsync();
        var session = duplex.CreateSession(recording);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync(); // greeting

        await duplex.WriteClientLineAsync("CAPABILITIES");
        Assert.StartsWith("101 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        _ = await duplex.ReadMultilineBodyAsync();

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;

        var rx = recording.Messages.Where(m => m.Contains("RX:", StringComparison.Ordinal)).ToArray();
        var tx = recording.Messages.Where(m => m.Contains("TX:", StringComparison.Ordinal)).ToArray();

        Assert.Contains(rx, m => m.Contains("RX: CAPABILITIES", StringComparison.Ordinal));
        Assert.Equal(1, tx.Count(m => m.Contains("TX: CAPABILITIES [101 Capability list:] executed in", StringComparison.Ordinal)));
        Assert.Contains(tx, m => TxPattern.IsMatch(m) && m.Contains("CAPABILITIES", StringComparison.Ordinal));
        Assert.DoesNotContain(tx, m => m.Contains("VERSION 2", StringComparison.Ordinal));
        Assert.DoesNotContain(tx, m => m.Contains("IMPLEMENTATION", StringComparison.Ordinal));
        Assert.Contains(
            recording.Categories,
            c => c == typeof(Capabilities).FullName);
        Assert.DoesNotContain(
            recording.Messages,
            m => m.Contains("NNTP command CAPABILITIES failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Help_ProducesExactlyOneRxAndOneTx_UnderHelpLogger()
    {
        var recording = new RecordingLoggerFactory();
        await using var duplex = await LoggingDuplex.CreateAsync();
        var session = duplex.CreateSession(recording);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("HELP");
        Assert.Equal("100 Help text follows", await duplex.ReadClientLineAsync());
        var body = await duplex.ReadMultilineBodyAsync();
        Assert.Equal(Help.BodyLines, body);
        Assert.DoesNotContain(body, l => l.Contains("BENCHIT", StringComparison.OrdinalIgnoreCase));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;

        var rx = recording.Messages.Where(m => m.Contains("RX:", StringComparison.Ordinal)).ToArray();
        var tx = recording.Messages.Where(m => m.Contains("TX:", StringComparison.Ordinal)).ToArray();

        Assert.Contains(rx, m => m.Contains("RX: HELP", StringComparison.Ordinal));
        Assert.Equal(1, tx.Count(m => m.Contains("TX: HELP [100 Help text follows] executed in", StringComparison.Ordinal)));
        Assert.DoesNotContain(tx, m => m.Contains("ARTICLE [message-id / article-number]", StringComparison.Ordinal));
        Assert.DoesNotContain(tx, m => m.Contains("CHECK message-id", StringComparison.Ordinal));
        Assert.Contains(recording.Categories, c => c == typeof(Help).FullName);
        Assert.DoesNotContain(
            recording.Messages,
            m => m.Contains("NNTP command HELP failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartTls_CompletionOwnedByStartTlsLogger_NotDispatcher()
    {
        var recording = new RecordingLoggerFactory();
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        var session = new NntpSession(
            server,
            recording.CreateLogger<NntpSession>(),
            certificateProvider: host.CertificateProvider,
            loggerFactory: recording);

        var run = session.RunAsync();
        _ = await ReadPlainLineAsync(clientSocket);

        await clientSocket.SendAsync("STARTTLS\r\n"u8.ToArray());
        Assert.StartsWith("382 ", await ReadPlainLineAsync(clientSocket), StringComparison.Ordinal);

        await using var network = new NetworkStream(clientSocket, ownsSocket: true);
        await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(CreateClientSslOptions());
        await WaitForServerTlsAsync(server);

        await server.CompleteAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(recording.Messages, m => m.Contains("RX: STARTTLS", StringComparison.Ordinal));
        var startTlsTx = recording.Messages
            .Where(m => m.Contains("TX: STARTTLS [382 Continue with TLS negotiation] executed in", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(startTlsTx);
        Assert.Matches(TxPattern, startTlsTx[0]);
        Assert.Contains("TlsVersion=", startTlsTx[0], StringComparison.Ordinal);
        Assert.Contains("Cipher=", startTlsTx[0], StringComparison.Ordinal);
        Assert.DoesNotContain("[failed]", startTlsTx[0], StringComparison.Ordinal);

        Assert.True(server.TryGetNegotiatedTlsParameters(out var tlsVersion, out var cipher));
        Assert.Contains($"TlsVersion={tlsVersion}", startTlsTx[0], StringComparison.Ordinal);
        Assert.Contains($"Cipher={cipher}", startTlsTx[0], StringComparison.Ordinal);

        Assert.Contains(recording.Categories, c => c == typeof(StartTls).FullName);
        Assert.DoesNotContain(
            recording.Messages,
            m => m.Contains("NNTP command STARTTLS failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Post_OnTransit_Gated502_ProducesExactlyOneTxCompletion()
    {
        var recording = new RecordingLoggerFactory();
        await using var duplex = await LoggingDuplex.CreateAsync();
        var session = duplex.CreateSession(
            recording,
            authorization: new NntpAuthorization(
                isAuthenticated: true,
                authorizedReader: true,
                authorizedTransit: false,
                postingPermitted: true,
                streamingPermitted: false));

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("502 Command not available on this server", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;

        Assert.Equal(
            1,
            recording.Messages.Count(m =>
                m.Contains("TX: POST [502 Command not available on this server] executed in", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Quit_ProducesExactlyOneTxCompletion()
    {
        var recording = new RecordingLoggerFactory();
        await using var duplex = await LoggingDuplex.CreateAsync();
        var session = duplex.CreateSession(recording);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;

        Assert.Equal(
            1,
            recording.Messages.Count(m =>
                m.Contains("TX: QUIT [205 Connection closing] executed in", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AuthinfoPass_RxIsRedacted_AndPasswordNeverLogged()
    {
        var recording = new RecordingLoggerFactory();
        var provider = new AcceptFredProvider();
        await using var duplex = await LoggingDuplex.CreateAsync();
        var session = duplex.CreateSession(recording, authenticationProvider: provider, allowCleartextAuth: true);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("AUTHINFO USER fred");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("AUTHINFO PASS super-secret-password-xyz");
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;

        var joined = string.Join('\n', recording.Messages);
        Assert.DoesNotContain("super-secret-password-xyz", joined, StringComparison.Ordinal);
        Assert.Contains(recording.Messages, m => m.Contains("RX: AUTHINFO PASS <redacted>", StringComparison.Ordinal));
        Assert.Equal(
            1,
            recording.Messages.Count(m =>
                m.Contains("TX: AUTHINFO PASS [281 Authentication accepted] executed in", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EveryInventoryCommand_ProducesRxWhenIssued()
    {
        var recording = new RecordingLoggerFactory();
        await using var duplex = await LoggingDuplex.CreateAsync();
        var session = duplex.CreateSession(
            recording,
            authorization: new NntpAuthorization(
                isAuthenticated: true,
                authorizedReader: true,
                authorizedTransit: true,
                postingPermitted: true,
                streamingPermitted: true));

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var commands = new (string Line, string RxNeedle)[]
        {
            ("CAPABILITIES", "RX: CAPABILITIES"),
            ("DATE", "RX: DATE"),
            ("HELP", "RX: HELP"),
            ("MODE READER", "RX: MODE READER"),
            ("LIST", "RX: LIST"),
            ("GROUP", "RX: GROUP"),
            ("LISTGROUP", "RX: LISTGROUP"),
            ("ARTICLE", "RX: ARTICLE"),
            ("HEAD", "RX: HEAD"),
            ("BODY", "RX: BODY"),
            ("STAT", "RX: STAT"),
            ("LAST", "RX: LAST"),
            ("NEXT", "RX: NEXT"),
            ("OVER", "RX: OVER"),
            ("HDR", "RX: HDR"),
            ("POST", "RX: POST"),
            ("IHAVE", "RX: IHAVE"),
            ("CHECK", "RX: CHECK"),
            ("MODE STREAM", "RX: MODE STREAM"),
            ("COMPRESS DEFLATE", "RX: COMPRESS DEFLATE"),
            ("AUTHINFO SASL", "RX: AUTHINFO SASL"),
        };

        foreach (var (line, _) in commands)
        {
            await duplex.WriteClientLineAsync(line);
            await DrainResponseAsync(duplex);
            if (line == "POST")
            {
                await duplex.WriteClientBytesAsync(".\r\n"u8.ToArray());
                await DrainResponseAsync(duplex);
            }
        }

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;

        foreach (var (_, needle) in commands)
        {
            Assert.Contains(recording.Messages, m => m.Contains(needle, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task TakeThis_ProducesRxAndTxCompletion_Logs()
    {
        var recording = new RecordingLoggerFactory();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await LoggingDuplex.CreateAsync();
        var session = duplex.CreateSession(
            recording,
            authorization: new NntpAuthorization(
                isAuthenticated: true,
                authorizedReader: false,
                authorizedTransit: true,
                postingPermitted: false,
                streamingPermitted: true),
            articleIngestion: queue);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("TAKETHIS <bench@ex.com>");
        await duplex.WriteClientBytesAsync(Encoding.ASCII.GetBytes(CanonicalArticleText.Destuffed("<bench@ex.com>") + ".\r\n"));
        Assert.Equal("239 <bench@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal(1, queue.Count);

        await duplex.WriteClientLineAsync("DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;

        // Authorized STREAM TAKETHIS is admitted by NntpStreamDataPlaneRx and never
        // enters DispatchCommandAsync, so there is no RX: TAKETHIS line. TX completion
        // (previously hot-path suppressed) is restored.
        Assert.DoesNotContain(
            recording.Messages,
            m => m.Contains("RX: TAKETHIS", StringComparison.Ordinal));
        Assert.Contains(
            recording.Messages,
            m => m.Contains("TX: TAKETHIS [239 <bench@ex.com>] executed in", StringComparison.Ordinal)
                 && m.Contains("[accepted]", StringComparison.Ordinal));

        Assert.Contains(recording.Messages, m => m.Contains("RX: DATE", StringComparison.Ordinal));
        Assert.Equal(
            1,
            recording.Messages.Count(m =>
                m.Contains("TX: DATE [111 ", StringComparison.Ordinal)
                && m.Contains("] executed in", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Date_StillProducesRxAndTx_WhenTakeThisSuppressed()
    {
        var recording = new RecordingLoggerFactory();
        await using var duplex = await LoggingDuplex.CreateAsync();
        var session = duplex.CreateSession(recording);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;

        Assert.Contains(recording.Messages, m => m.Contains("RX: DATE", StringComparison.Ordinal));
        Assert.Equal(
            1,
            recording.Messages.Count(m =>
                m.Contains("TX: DATE [111 ", StringComparison.Ordinal)
                && m.Contains("] executed in", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task StartTls_HandshakeFailure_TxHasFailed_WithoutFakeTlsDetails()
    {
        var recording = new RecordingLoggerFactory();
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        var session = new NntpSession(
            server,
            recording.CreateLogger<NntpSession>(),
            certificateProvider: host.CertificateProvider,
            loggerFactory: recording);

        var run = session.RunAsync();
        _ = await ReadPlainLineAsync(clientSocket);
        await clientSocket.SendAsync("STARTTLS\r\n"u8.ToArray());
        Assert.StartsWith("382 ", await ReadPlainLineAsync(clientSocket), StringComparison.Ordinal);
        await clientSocket.SendAsync(new byte[] { 0x15, 0x03, 0x03, 0x00, 0x02, 0x02, 0x28 });
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        var tx = Assert.Single(
            recording.Messages,
            m => m.Contains("TX: STARTTLS [382 Continue with TLS negotiation] executed in", StringComparison.Ordinal));
        Assert.Contains("[failed]", tx, StringComparison.Ordinal);
        Assert.DoesNotContain("TlsVersion=", tx, StringComparison.Ordinal);
        Assert.DoesNotContain("Cipher=", tx, StringComparison.Ordinal);
        Assert.DoesNotContain(
            recording.Messages,
            m => m.Contains("NNTP command STARTTLS failed", StringComparison.Ordinal));
        Assert.Contains(recording.Categories, c => c == typeof(StartTls).FullName);
    }

    [Fact]
    public async Task Check_Wanted_LogsExactStatusLineFromWriter()
    {
        var recording = new RecordingLoggerFactory();
        await using var duplex = await LoggingDuplex.CreateAsync();
        var session = duplex.CreateSession(
            recording,
            authorization: TransitAuthorization());

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <want@example.com>");
        Assert.Equal(
            "238 <want@example.com> send article to be transferred",
            await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;

        var tx = Assert.Single(
            recording.Messages,
            m => m.Contains("TX: CHECK", StringComparison.Ordinal));
        Assert.Matches(
            @"^\[198\.18\.0\.70:49860\] TX: CHECK \[238 <want@example.com> send article to be transferred\] executed in \d+\.\d{3}s$",
            tx);
        Assert.DoesNotContain("438", tx, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_Duplicate_LogsActual438StatusLine()
    {
        var recording = new RecordingLoggerFactory();
        await using var duplex = await LoggingDuplex.CreateAsync();
        var session = duplex.CreateSession(
            recording,
            authorization: TransitAuthorization(),
            historyDb: new SeenHistoryDb());

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <dup@example.com>");
        Assert.Equal("438 <dup@example.com>", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;

        var tx = Assert.Single(
            recording.Messages,
            m => m.Contains("TX: CHECK", StringComparison.Ordinal));
        Assert.Contains("TX: CHECK [438 <dup@example.com>] executed in", tx, StringComparison.Ordinal);
        Assert.DoesNotContain("238", tx, StringComparison.Ordinal);
        Assert.DoesNotContain("send article to be transferred", tx, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_Unavailable_Logs431StatusLine()
    {
        var recording = new RecordingLoggerFactory();
        await using var duplex = await LoggingDuplex.CreateAsync();
        var session = duplex.CreateSession(
            recording,
            authorization: TransitAuthorization(),
            historyDb: new UnavailableHistoryDb());

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <later@example.com>");
        Assert.Equal("431 <later@example.com>", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;

        var tx = Assert.Single(
            recording.Messages,
            m => m.Contains("TX: CHECK", StringComparison.Ordinal));
        Assert.Contains("TX: CHECK [431 <later@example.com>] executed in", tx, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TakeThis_RejectedTooLarge_Logs439StatusLine()
    {
        var recording = new RecordingLoggerFactory();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions
        {
            QueueCapacity = 4,
            MaxArticleBytes = 16,
        });
        await using var duplex = await LoggingDuplex.CreateAsync();
        var session = duplex.CreateSession(
            recording,
            authorization: TransitAuthorization(),
            articleIngestion: queue);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("TAKETHIS <big@example.com>");
        await duplex.WriteClientBytesAsync("Subject: oversized-payload-for-439\r\n\r\nbody\r\n.\r\n"u8.ToArray());
        Assert.Equal("439 <big@example.com>", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;

        var tx = Assert.Single(
            recording.Messages,
            m => m.Contains("TX: TAKETHIS", StringComparison.Ordinal));
        Assert.Contains("TX: TAKETHIS [439 <big@example.com>] executed in", tx, StringComparison.Ordinal);
        Assert.Contains("[rejected too large]", tx, StringComparison.Ordinal);
        Assert.DoesNotContain("Subject: oversized", tx, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TakeThis_HistoryUnavailable_Logs400StatusLine()
    {
        var recording = new RecordingLoggerFactory();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await LoggingDuplex.CreateAsync();
        var session = duplex.CreateSession(
            recording,
            authorization: TransitAuthorization(),
            articleIngestion: queue,
            historyDb: new UnavailableHistoryDb());

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("TAKETHIS <tmp@example.com>");
        await duplex.WriteClientBytesAsync("Subject: t\r\n\r\nbody\r\n.\r\n"u8.ToArray());
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        await run;

        var tx = Assert.Single(
            recording.Messages,
            m => m.Contains("TX: TAKETHIS", StringComparison.Ordinal));
        Assert.Contains(
            "TX: TAKETHIS [400 Service temporarily unavailable] executed in",
            tx,
            StringComparison.Ordinal);
        Assert.Contains("[temporary failure]", tx, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_OnTransit_Logs502StatusLine_WithoutServingSnapshot()
    {
        var recording = new RecordingLoggerFactory();
        await using var duplex = await LoggingDuplex.CreateAsync();
        var session = duplex.CreateSession(
            recording,
            authorization: new NntpAuthorization(
                isAuthenticated: true,
                authorizedReader: true,
                authorizedTransit: false,
                postingPermitted: true,
                streamingPermitted: false),
            newsgroupCatalogue: new StaticNewsgroupCatalogue(NewsgroupSnapshot.Empty));

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("LIST");
        Assert.Equal("502 Command not available on this server", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;

        var tx = Assert.Single(
            recording.Messages,
            m => m.Contains("TX: LIST", StringComparison.Ordinal));
        Assert.Contains(
            "TX: LIST [502 Command not available on this server] executed in",
            tx,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RedactRxLine_ProtectsPassAndSasl()
    {
        Assert.Equal("AUTHINFO PASS <redacted>", NntpCommandLogFormat.RedactRxLine("AUTHINFO PASS secret"));
        Assert.Equal("AUTHINFO SASL <redacted>", NntpCommandLogFormat.RedactRxLine("AUTHINFO SASL PLAIN abc"));
        Assert.Equal("CAPABILITIES", NntpCommandLogFormat.RedactRxLine("CAPABILITIES"));
    }

    private static NntpAuthorization TransitAuthorization() =>
        new(
            isAuthenticated: true,
            authorizedReader: false,
            authorizedTransit: true,
            postingPermitted: false,
            streamingPermitted: true);

    private static async Task DrainResponseAsync(LoggingDuplex duplex)
    {
        var line = await duplex.ReadClientLineAsync();
        // Multi-line responses (CAPABILITIES 101, HELP 100, LIST 215, …).
        if (line.StartsWith("100 ", StringComparison.Ordinal) ||
            line.StartsWith("101 ", StringComparison.Ordinal) ||
            line.StartsWith("215 ", StringComparison.Ordinal) ||
            line.StartsWith("224 ", StringComparison.Ordinal) ||
            line.StartsWith("225 ", StringComparison.Ordinal) ||
            line.StartsWith("220 ", StringComparison.Ordinal) ||
            line.StartsWith("221 ", StringComparison.Ordinal) ||
            line.StartsWith("222 ", StringComparison.Ordinal) ||
            line.StartsWith("230 ", StringComparison.Ordinal) ||
            line.StartsWith("231 ", StringComparison.Ordinal))
        {
            _ = await duplex.ReadMultilineBodyAsync();
        }
    }

    private static async Task<string> ReadPlainLineAsync(System.Net.Sockets.Socket socket)
    {
        var buffer = new byte[512];
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(total));
            Assert.True(n > 0);
            total += n;
            var text = System.Text.Encoding.ASCII.GetString(buffer.AsSpan(0, total));
            var idx = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (idx >= 0)
            {
                return text[..idx];
            }
        }

        throw new InvalidOperationException("line too long");
    }

    private static async Task WaitForServerTlsAsync(INntpConnection server)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.IsTls)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private static async Task WriteSslLineAsync(System.Net.Security.SslStream ssl, string line)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(line + "\r\n");
        await ssl.WriteAsync(bytes);
    }

    private static async Task<string> ReadSslLineAsync(System.Net.Security.SslStream ssl)
    {
        var buffer = new byte[512];
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await ssl.ReadAsync(buffer.AsMemory(total));
            Assert.True(n > 0);
            total += n;
            var text = System.Text.Encoding.ASCII.GetString(buffer.AsSpan(0, total));
            var idx = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (idx >= 0)
            {
                return text[..idx];
            }
        }

        throw new InvalidOperationException("line too long");
    }

    private static System.Net.Security.SslClientAuthenticationOptions CreateClientSslOptions() =>
        new()
        {
            TargetHost = "nntpd01.usenet.ninja",
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
        };

    private sealed class AcceptFredProvider : INntpAuthenticationProvider
    {
        public ValueTask<NntpAuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default) =>
            new(
                username == "fred"
                    ? NntpAuthenticationResult.Success(
                        "fred",
                        new NntpAuthorization(
                            isAuthenticated: true,
                            authorizedReader: true,
                            authorizedTransit: false,
                            postingPermitted: false,
                            streamingPermitted: false))
                    : NntpAuthenticationResult.Failed);
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public ConcurrentBag<string> Messages { get; } = [];
        public ConcurrentBag<string> Categories { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            Categories.Add(categoryName);
            return new RecordingLogger(Messages);
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentBag<string> _messages;

        public RecordingLogger(ConcurrentBag<string> messages) => _messages = messages;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Add(formatter(state, exception));
        }
    }

    private sealed class LoggingDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public static Task<LoggingDuplex> CreateAsync() => Task.FromResult(new LoggingDuplex());

        public NntpSession CreateSession(
            ILoggerFactory loggerFactory,
            INntpAuthenticationProvider? authenticationProvider = null,
            bool allowCleartextAuth = true,
            NntpAuthorization? authorization = null,
            IArticleIngestionQueue? articleIngestion = null,
            VectorNNTPD.TransitService.History.IHistoryDb? historyDb = null,
            INewsgroupCatalogue? newsgroupCatalogue = null)
        {
            var connection = new PipeConnection(_clientToServer.Reader, _serverToClient.Writer);
            var session = new NntpSession(
                connection,
                loggerFactory.CreateLogger<NntpSession>(),
                authenticationProvider: authenticationProvider,
                allowCleartextAuth: allowCleartextAuth,
                loggerFactory: loggerFactory,
                articleIngestion: articleIngestion,
                historyDb: historyDb,
                newsgroupCatalogue: newsgroupCatalogue);
            if (authorization is not null)
            {
                session.SetAuthorization(authorization);
            }

            return session;
        }

        public async Task WriteClientLineAsync(string line)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            await _clientToServer.Writer.WriteAsync(bytes);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task WriteClientBytesAsync(ReadOnlyMemory<byte> bytes)
        {
            await _clientToServer.Writer.WriteAsync(bytes);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
            Assert.NotNull(line);
            return line!;
        }

        public async Task<List<string>> ReadMultilineBodyAsync()
        {
            var lines = new List<string>();
            while (true)
            {
                var line = await ReadClientLineAsync();
                if (line == ".")
                {
                    break;
                }

                lines.Add(line);
            }

            return lines;
        }

        public async ValueTask DisposeAsync()
        {
            await _clientToServer.Writer.CompleteAsync();
            await _clientToServer.Reader.CompleteAsync();
            await _serverToClient.Writer.CompleteAsync();
            await _serverToClient.Reader.CompleteAsync();
        }

        private sealed class PipeConnection : INntpConnection
        {
            private readonly CancellationTokenSource _cts = new();

            public PipeConnection(PipeReader input, PipeWriter output)
            {
                Input = input;
                Output = output;
                ClientIdentity = ConnectionClientIdentity.Direct(
                    new IPEndPoint(IPAddress.Parse("198.18.0.70"), 49860));
            }

            public PipeReader Input { get; }
            public PipeWriter Output { get; }
            public EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
            public EndPoint? LocalEndPoint => null;
            public ConnectionClientIdentity ClientIdentity { get; }
            public bool IsTls => false;

            public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
            {
                tlsVersion = string.Empty;
                cipher = string.Empty;
                return false;
            }

            public bool IsCompressed => Volatile.Read(ref _compressed) == 1;
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

            public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default)
            {
                Volatile.Write(ref _compressed, 1);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                _cts.Dispose();
                return ValueTask.CompletedTask;
            }

            private int _compressed;
        }
    }

    private sealed class UnavailableHistoryDb : VectorNNTPD.TransitService.History.IHistoryDb
    {
        public ValueTask<VectorNNTPD.TransitService.History.HistoryLookupResult> LookupAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default) =>
            new(VectorNNTPD.TransitService.History.HistoryLookupResult.Unavailable);

        public ValueTask<VectorNNTPD.TransitService.History.HistoryLookupResult> PeekAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default) =>
            new(VectorNNTPD.TransitService.History.HistoryLookupResult.Unavailable);

        public void Remember(ReadOnlyMemory<byte> messageId)
        {
        }

        public bool ContainsLocal(in VectorNNTPD.TransitService.History.HistoryDigest digest) => false;
    }

    private sealed class SeenHistoryDb : VectorNNTPD.TransitService.History.IHistoryDb
    {
        public ValueTask<VectorNNTPD.TransitService.History.HistoryLookupResult> LookupAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default) =>
            new(VectorNNTPD.TransitService.History.HistoryLookupResult.Seen);

        public ValueTask<VectorNNTPD.TransitService.History.HistoryLookupResult> PeekAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default) =>
            new(VectorNNTPD.TransitService.History.HistoryLookupResult.Seen);

        public void Remember(ReadOnlyMemory<byte> messageId)
        {
        }

        public bool ContainsLocal(in VectorNNTPD.TransitService.History.HistoryDigest digest) => true;
    }
}
