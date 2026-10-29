using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;

namespace VectorNNTPD.TransitService.Tests.Session;

/// <summary>HELP command — static syntax listing (RFC 3977 §7.2) with ABNF-consistent notation.</summary>
[Collection(nameof(NntpCommandLoggerCollection))]
public sealed class HelpCommandTests
{
    [Fact]
    public async Task Help_Returns100_AndStaticSyntaxLines()
    {
        await using var duplex = await HelpDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        var readTask = duplex.ReadMultilineResponseAsync();
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "HELP");
        var (status, body) = await readTask;

        Assert.Equal(1, response.ChannelEnqueueCount);
        Assert.Equal("100 Help text follows", status);
        Assert.Equal(Help.BodyLines, body);
        Assert.DoesNotContain(body, l => l.Contains("BENCHIT", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("NEWGROUPS", body);
        Assert.DoesNotContain("NEWNEWS", body);
        AssertUnsupportedListVariantsAbsent(body);
        AssertHelpBodyStructure(body);
    }

    [Fact]
    public async Task Help_IsIdenticalAcrossSessionStates()
    {
        await using var duplex = await HelpDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: true,
            postingPermitted: true,
            streamingPermitted: true));
        session.SetMode(NntpSessionMode.Reader);

        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        var readTask = duplex.ReadMultilineResponseAsync();
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "HELP");
        var (_, body) = await readTask;

        Assert.Equal(Help.BodyLines, body);
    }

    [Fact]
    public async Task Help_TrailingArgs_Returns501()
    {
        await using var duplex = await HelpDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "HELP MORE");
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task Help_SessionRemainsOpen_ForSubsequentCommand()
    {
        await using var duplex = await HelpDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync(); // greeting

        await duplex.WriteClientLineAsync("HELP");
        Assert.Equal("100 Help text follows", await duplex.ReadClientLineAsync());
        var body = await duplex.ReadMultilineBodyAsync();
        Assert.Equal(Help.BodyLines, body);

        await duplex.WriteClientLineAsync("DATE");
        var date = await duplex.ReadClientLineAsync();
        Assert.StartsWith("111 ", date, StringComparison.Ordinal);
        Assert.Equal(14, date.Length - 4);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public void SyntaxLines_AreDeterministicAndUseAbnfConsistentNotation()
    {
        Assert.Equal(Help.SyntaxLines, Help.SyntaxLines.ToArray());
        Assert.Equal(Help.BodyLines, Help.BodyLines.ToArray());
        Assert.Contains("HELP", Help.SyntaxLines);
        Assert.Contains("COMPRESS DEFLATE", Help.SyntaxLines);
        Assert.DoesNotContain(Help.SyntaxLines, l => l.Contains("BENCHIT", StringComparison.OrdinalIgnoreCase));
        AssertUnsupportedListVariantsAbsent(Help.SyntaxLines);
        Assert.Contains("LIST", Help.SyntaxLines);
        Assert.Contains("LIST ACTIVE [wildmat]", Help.SyntaxLines);
        Assert.Contains("LIST COUNTS [wildmat]", Help.SyntaxLines);
        Assert.Contains("LIST HEADERS [MSGID / RANGE]", Help.SyntaxLines);
        Assert.Contains("LIST NEWSGROUPS [wildmat]", Help.SyntaxLines);
        Assert.Contains("LIST OVERVIEW.FMT", Help.SyntaxLines);
        Assert.Contains("LIST MOTD", Help.SyntaxLines);

        // ABNF-consistent: optional args use [], alternatives use /; no {…} or |.
        Assert.DoesNotContain(Help.SyntaxLines, l => l.Contains('{', StringComparison.Ordinal));
        Assert.DoesNotContain(Help.SyntaxLines, l => l.Contains('}', StringComparison.Ordinal));
        Assert.DoesNotContain(Help.SyntaxLines, l => l.Contains('|', StringComparison.Ordinal));
        Assert.Contains("ARTICLE [message-id / article-number]", Help.SyntaxLines);
        Assert.Contains("AUTHINFO USER username", Help.SyntaxLines);
        Assert.Contains("AUTHINFO PASS password", Help.SyntaxLines);
        Assert.Contains("AUTHINFO SASL mechanism [initial-response]", Help.SyntaxLines);
        Assert.Contains("GROUP newsgroup", Help.SyntaxLines);
        Assert.DoesNotContain(Help.SyntaxLines, l => l.Equals("GROUP [newsgroup]", StringComparison.Ordinal));
        Assert.DoesNotContain(Help.SyntaxLines, l => l.StartsWith("GROUP [", StringComparison.Ordinal));
        Assert.Contains("HDR header [range / message-id]", Help.SyntaxLines);
        Assert.Contains("CHECK message-id", Help.SyntaxLines);
        Assert.Contains("IHAVE message-id", Help.SyntaxLines);
        Assert.Contains("TAKETHIS message-id", Help.SyntaxLines);
        Assert.Contains("SPEEDTEST <peer>", Help.SyntaxLines);
        Assert.Contains("LISTGROUP [newsgroup [range]]", Help.SyntaxLines);
        Assert.Contains("XOVER [range]", Help.SyntaxLines);

        Assert.Equal("ARTICLE [message-id / article-number]", Help.SyntaxLines[0]);
        Assert.Equal("XOVER [range]", Help.SyntaxLines[^1]);
        AssertHelpBodyStructure(Help.BodyLines);
    }

    private static void AssertHelpBodyStructure(IReadOnlyList<string> body)
    {
        Assert.Equal(Help.BodyLines, body);

        var syntaxEnd = Help.SyntaxLines.Count;
        Assert.Equal(string.Empty, body[syntaxEnd]);
        Assert.Equal("Range formats:", body[syntaxEnd + 1]);
        Assert.Equal(Help.RangeHelpLines, body.Skip(syntaxEnd + 1).Take(Help.RangeHelpLines.Count).ToArray());

        var afterRangeBlank = syntaxEnd + 1 + Help.RangeHelpLines.Count;
        Assert.Equal(string.Empty, body[afterRangeBlank]);
        Assert.Equal("Wildmat formats:", body[afterRangeBlank + 1]);
        Assert.Equal(Help.WildmatHelpLines, body.Skip(afterRangeBlank + 1).Take(Help.WildmatHelpLines.Count).ToArray());

        Assert.Contains("123       a single article number", body);
        Assert.Contains("123-456   articles from 123 through 456 inclusive", body);
        Assert.Contains("123-      article 123 and all following article numbers", body);
        Assert.Contains("*         matches zero or more characters", body);
        Assert.Contains("?         matches exactly one character", body);
        Assert.Contains("Examples: a* ; a*,!*b ; *.recovery", body);
        Assert.DoesNotContain(body, l => l.Contains("regex", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(body, l => l.Contains("[^", StringComparison.Ordinal));
    }

    private static void AssertUnsupportedListVariantsAbsent(IReadOnlyList<string> lines)
    {
        Assert.DoesNotContain(lines, l => l.Contains("ACTIVE.TIMES", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("DISTRIB.PATS", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("DISTRIBUTIONS", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("MODERATORS", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("SUBSCRIPTIONS", StringComparison.Ordinal));
    }

    private sealed class HelpDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public static Task<HelpDuplex> CreateAsync() => Task.FromResult(new HelpDuplex());

        public NntpSession CreateSession()
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)));
            return new NntpSession(connection, NullLogger<NntpSession>.Instance);
        }

        public async Task WriteClientLineAsync(string line)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
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

        public async Task<(string Status, List<string> Body)> ReadMultilineResponseAsync()
        {
            // Caller must start this before DispatchAsync so FlushAsync backpressure can drain.
            var status = await ReadClientLineAsync().ConfigureAwait(false);
            var body = await ReadMultilineBodyAsync().ConfigureAwait(false);
            return (status, body);
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
        private int _compressed;

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
            ITlsCertificateContextProvider certificateProvider,
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
    }
}
