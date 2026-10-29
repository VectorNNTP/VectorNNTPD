using System.IO.Pipelines;
using System.Text;
using VectorNNTPD.Common.Articles.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Commands;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Tests.Session;

/// <summary>
/// Full-surface byte parser contracts: classification, syntax status, and reject-before-handler.
/// </summary>
public sealed class NntpCommandParserTests
{
    [Theory]
    [InlineData("ARTICLE", NntpVerb.Article, NntpVerb.None)]
    [InlineData("ARTICLE 123", NntpVerb.Article, NntpVerb.None)]
    [InlineData("ARTICLE 0", NntpVerb.Article, NntpVerb.None)]
    [InlineData("ARTICLE 0001", NntpVerb.Article, NntpVerb.None)]
    [InlineData("ARTICLE 9999999999999999", NntpVerb.Article, NntpVerb.None)]
    [InlineData("ARTICLE <does-not-exist@example.com>", NntpVerb.Article, NntpVerb.None)]
    [InlineData("ARTICLE <123@example.com>", NntpVerb.Article, NntpVerb.None)]
    [InlineData("article", NntpVerb.Article, NntpVerb.None)]
    [InlineData("ArTiClE", NntpVerb.Article, NntpVerb.None)]
    [InlineData("BODY", NntpVerb.Body, NntpVerb.None)]
    [InlineData("BODY 1", NntpVerb.Body, NntpVerb.None)]
    [InlineData("HEAD <a@b.c>", NntpVerb.Head, NntpVerb.None)]
    [InlineData("STAT 300256", NntpVerb.Stat, NntpVerb.None)]
    [InlineData("CAPABILITIES", NntpVerb.Capabilities, NntpVerb.None)]
    [InlineData("CHECK <a@b.c>", NntpVerb.Check, NntpVerb.None)]
    [InlineData("CHECK <x@example>", NntpVerb.Check, NntpVerb.None)]
    [InlineData("CHECK <a@b>", NntpVerb.Check, NntpVerb.None)]
    [InlineData("CHECK <ab@c>", NntpVerb.Check, NntpVerb.None)]
    [InlineData("check <a@b.c>", NntpVerb.Check, NntpVerb.None)]
    [InlineData("cHeCk <a@b.c>", NntpVerb.Check, NntpVerb.None)]
    [InlineData("COMPRESS DEFLATE", NntpVerb.Compress, NntpVerb.None)]
    [InlineData("DATE", NntpVerb.Date, NntpVerb.None)]
    [InlineData("GROUP misc.test", NntpVerb.Group, NntpVerb.None)]
    [InlineData("HDR", NntpVerb.Hdr, NntpVerb.None)]
    [InlineData("HEAD", NntpVerb.Head, NntpVerb.None)]
    [InlineData("HELP", NntpVerb.Help, NntpVerb.None)]
    [InlineData("IHAVE <a@b.c>", NntpVerb.Ihave, NntpVerb.None)]
    [InlineData("LAST", NntpVerb.Last, NntpVerb.None)]
    [InlineData("LIST", NntpVerb.List, NntpVerb.None)]
    [InlineData("LIST ACTIVE", NntpVerb.List, NntpVerb.Active)]
    [InlineData("LIST COUNTS", NntpVerb.List, NntpVerb.Counts)]
    [InlineData("LIST COUNTS misc.*", NntpVerb.List, NntpVerb.Counts)]
    [InlineData("LIST HEADERS", NntpVerb.List, NntpVerb.Headers)]
    [InlineData("LIST HEADERS MSGID", NntpVerb.List, NntpVerb.Headers)]
    [InlineData("LIST HEADERS RANGE", NntpVerb.List, NntpVerb.Headers)]
    [InlineData("LIST MOTD", NntpVerb.List, NntpVerb.Motd)]
    [InlineData("LIST NEWSGROUPS", NntpVerb.List, NntpVerb.Newsgroups)]
    [InlineData("LIST OVERVIEW.FMT", NntpVerb.List, NntpVerb.OverviewFmt)]
    [InlineData("LISTGROUP", NntpVerb.ListGroup, NntpVerb.None)]
    [InlineData("MODE READER", NntpVerb.Mode, NntpVerb.Reader)]
    [InlineData("MODE STREAM", NntpVerb.Mode, NntpVerb.Stream)]
    [InlineData("mode reader", NntpVerb.Mode, NntpVerb.Reader)]
    [InlineData("mode stream", NntpVerb.Mode, NntpVerb.Stream)]
    [InlineData("NEXT", NntpVerb.Next, NntpVerb.None)]
    [InlineData("OVER", NntpVerb.Over, NntpVerb.None)]
    [InlineData("POST", NntpVerb.Post, NntpVerb.None)]
    [InlineData("QUIT", NntpVerb.Quit, NntpVerb.None)]
    [InlineData("STARTTLS", NntpVerb.StartTls, NntpVerb.None)]
    [InlineData("STAT", NntpVerb.Stat, NntpVerb.None)]
    [InlineData("TAKETHIS <a@b.c>", NntpVerb.TakeThis, NntpVerb.None)]
    [InlineData("takethis <a@b.c>", NntpVerb.TakeThis, NntpVerb.None)]
    [InlineData("AUTHINFO USER alice", NntpVerb.AuthInfo, NntpVerb.User)]
    [InlineData("AUTHINFO PASS secret", NntpVerb.AuthInfo, NntpVerb.Pass)]
    [InlineData("AUTHINFO SASL PLAIN", NntpVerb.AuthInfo, NntpVerb.Sasl)]
    [InlineData("authinfo user alice", NntpVerb.AuthInfo, NntpVerb.User)]
    [InlineData("BENCHIT", NntpVerb.BenchIt, NntpVerb.None)]
    [InlineData("SPEEDTEST GIGANEWS", NntpVerb.SpeedTest, NntpVerb.None)]
    [InlineData("SPEEDTEST usenet-ninja", NntpVerb.SpeedTest, NntpVerb.None)]
    [InlineData("speedtest GIGANEWS", NntpVerb.SpeedTest, NntpVerb.None)]
    [InlineData("SpEeDtEsT GIGANEWS", NntpVerb.SpeedTest, NntpVerb.None)]
    public void Parse_SupportedCommands_AreValid(string text, NntpVerb verb, NntpVerb qualifier)
    {
        var (command, _) = NntpCommandTestParse.Parse(text);
        Assert.True(command.IsValid);
        Assert.Equal(NntpParseStatus.Ok, command.Status);
        Assert.Equal(verb, command.Verb);
        Assert.Equal(qualifier, command.Qualifier);
    }

    /// <summary>RFC 3977 Â§3.1: tokens are separated by one or more SP or TAB.</summary>
    [Theory]
    [InlineData("CHECK\t<a@b.c>")]
    [InlineData("MODE\tSTREAM")]
    [InlineData("AUTHINFO\tUSER\talice")]
    [InlineData("QUIT\t")]
    [InlineData("  DATE  ")]
    [InlineData("\tHELP")]
    public void Parse_SpAndTabWhitespace_IsAccepted(string text)
    {
        Assert.True(NntpCommandTestParse.ParseCommand(text).IsValid);
    }

    [Theory]
    [InlineData("", NntpParseStatus.Empty)]
    [InlineData(" ", NntpParseStatus.Empty)]
    [InlineData("\t", NntpParseStatus.Empty)]
    [InlineData("   ", NntpParseStatus.Empty)]
    [InlineData("NOSUCHCMD", NntpParseStatus.UnknownVerb)]
    [InlineData("NEWNEWS", NntpParseStatus.UnknownVerb)]
    [InlineData("NEWGROUPS", NntpParseStatus.UnknownVerb)]
    [InlineData("newnews * 19990624 000000 GMT", NntpParseStatus.UnknownVerb)]
    [InlineData("NEWGROUPS 19990624 000000 GMT", NntpParseStatus.UnknownVerb)]
    [InlineData("ARTICLEX", NntpParseStatus.UnknownVerb)]
    [InlineData("MODE", NntpParseStatus.UnknownQualifier)]
    [InlineData("MODE UNKNOWN", NntpParseStatus.UnknownQualifier)]
    [InlineData("AUTHINFO", NntpParseStatus.UnknownQualifier)]
    [InlineData("AUTHINFO GENERIC x", NntpParseStatus.UnknownQualifier)]
    [InlineData("AUTHINFO USERXYZ", NntpParseStatus.UnknownQualifier)]
    [InlineData("ARTICLE foo", NntpParseStatus.InvalidArgument)]
    [InlineData("ARTICLE 12a", NntpParseStatus.InvalidArgument)]
    [InlineData("ARTICLE -1", NntpParseStatus.InvalidArgument)]
    [InlineData("ARTICLE 10000000000000000", NntpParseStatus.InvalidArgument)]
    [InlineData("ARTICLE <123@example.com", NntpParseStatus.InvalidArgument)]
    [InlineData("ARTICLE 123 extra", NntpParseStatus.ExtraArgument)]
    [InlineData("HEAD not-an-id", NntpParseStatus.InvalidArgument)]
    [InlineData("HEAD 123 extra", NntpParseStatus.ExtraArgument)]
    [InlineData("BODY 12345678901234567", NntpParseStatus.InvalidArgument)]
    [InlineData("BODY <no-close", NntpParseStatus.InvalidArgument)]
    [InlineData("STAT <a@b.c> extra", NntpParseStatus.ExtraArgument)]
    [InlineData("GROUP", NntpParseStatus.MissingArgument)]
    [InlineData("GROUP a b", NntpParseStatus.ExtraArgument)]
    [InlineData("LIST ACTIVE u[ks].*", NntpParseStatus.InvalidArgument)]
    [InlineData("LIST COUNTS u[ks].*", NntpParseStatus.InvalidArgument)]
    [InlineData("LIST ACTIVE foo bar", NntpParseStatus.ExtraArgument)]
    [InlineData("LIST COUNTS foo bar", NntpParseStatus.ExtraArgument)]
    [InlineData("LIST extra", NntpParseStatus.UnknownQualifier)]
    [InlineData("LIST SOMETHING-THAT-DOES-NOT-EXIST", NntpParseStatus.UnknownQualifier)]
    [InlineData("LIST MOTD extra", NntpParseStatus.ExtraArgument)]
    [InlineData("LIST OVERVIEW.FMT extra", NntpParseStatus.ExtraArgument)]
    [InlineData("LIST HEADERS FOO", NntpParseStatus.InvalidArgument)]
    [InlineData("LIST HEADERS MSGID extra", NntpParseStatus.ExtraArgument)]
    [InlineData("LIST ACTIVE.TIMES", NntpParseStatus.UnknownQualifier)]
    [InlineData("CHECK", NntpParseStatus.MissingArgument)]
    [InlineData("CHECK ", NntpParseStatus.MissingArgument)]
    [InlineData("TAKETHIS", NntpParseStatus.MissingArgument)]
    [InlineData("IHAVE", NntpParseStatus.MissingArgument)]
    [InlineData("AUTHINFO USER", NntpParseStatus.MissingArgument)]
    [InlineData("AUTHINFO PASS", NntpParseStatus.MissingArgument)]
    [InlineData("AUTHINFO SASL", NntpParseStatus.MissingArgument)]
    [InlineData("COMPRESS", NntpParseStatus.MissingArgument)]
    [InlineData("CHECK not-an-id", NntpParseStatus.InvalidArgument)]
    [InlineData("CHECK x>", NntpParseStatus.InvalidArgument)]
    [InlineData("CHECK <x", NntpParseStatus.InvalidArgument)]
    [InlineData("CHECK >x<", NntpParseStatus.InvalidArgument)]
    [InlineData("TAKETHIS not-an-id", NntpParseStatus.InvalidArgument)]
    [InlineData("IHAVE not-an-id", NntpParseStatus.InvalidArgument)]
    [InlineData("COMPRESS deflate", NntpParseStatus.InvalidArgument)]
    [InlineData("CHECK <a@b.c> extra", NntpParseStatus.ExtraArgument)]
    [InlineData("TAKETHIS <a@b.c> extra", NntpParseStatus.ExtraArgument)]
    [InlineData("IHAVE <a@b.c> extra", NntpParseStatus.ExtraArgument)]
    [InlineData("QUIT extra", NntpParseStatus.ExtraArgument)]
    [InlineData("HELP MORE", NntpParseStatus.ExtraArgument)]
    [InlineData("COMPRESS DEFLATE EXTRA", NntpParseStatus.ExtraArgument)]
    [InlineData("SPEEDTEST", NntpParseStatus.MissingArgument)]
    [InlineData("SPEEDTEST ", NntpParseStatus.MissingArgument)]
    [InlineData("SPEEDTEST GIGANEWS extra", NntpParseStatus.ExtraArgument)]
    [InlineData("SPEEDTEST Usenet Ninja", NntpParseStatus.ExtraArgument)]
    public void Parse_InvalidCommands_HaveExpectedStatus(string text, NntpParseStatus status)
    {
        var command = NntpCommandTestParse.ParseCommand(text);
        Assert.False(command.IsValid);
        Assert.Equal(status, command.Status);
    }

    /// <summary>
    /// IHAVE without a message-id is parser MissingArgument (501), not the old
    /// stub-handler 500. The parser owns required-parameter syntax.
    /// </summary>
    [Fact]
    public void Ihave_WithoutMessageId_IsMissingArgument_NotOk()
    {
        var command = NntpCommandTestParse.ParseCommand("IHAVE");
        Assert.Equal(NntpVerb.Ihave, command.Verb);
        Assert.Equal(NntpParseStatus.MissingArgument, command.Status);
        Assert.False(command.IsValid);
    }

    [Fact]
    public void Parse_DoesNotAllocate_OnRepresentativeLines()
    {
        string[] lines =
        [
            "CHECK <nntp-feed-20260924.012500.7f3a9c2b@peer.usenet.example>",
            "TAKETHIS <nntp-feed-20260924.012500.7f3a9c2b@peer.usenet.example>",
            "MODE STREAM",
            "MODE READER",
            "AUTHINFO USER foo",
            "AUTHINFO PASS bar",
            "QUIT",
            "CHECK not-an-id",
            "",
        ];

        foreach (var bytes in lines.Select(static text => Encoding.ASCII.GetBytes(text)))
        {
            for (var i = 0; i < 8; i++)
            {
                _ = NntpCommandParser.Parse(bytes);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            var sink = 0;
            for (var i = 0; i < 256; i++)
            {
                var command = NntpCommandParser.Parse(bytes);
                sink += (int)command.Verb + (int)command.Status + command.TokenCount;
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.NotEqual(int.MinValue, sink);
        }
    }

    [Theory]
    [InlineData("NOSUCHCMD", "500 Unknown command")]
    [InlineData("NEWNEWS", "500 Unknown command")]
    [InlineData("NEWGROUPS", "500 Unknown command")]
    [InlineData("NEWNEWS * 19990624 000000 GMT", "500 Unknown command")]
    [InlineData("NEWGROUPS 19990624 000000 GMT", "500 Unknown command")]
    [InlineData("MODE UNKNOWN", "501 Unknown command variant")]
    [InlineData("LIST DISTRIB.PATS", "501 Unknown command variant")]
    [InlineData("ARTICLE foo", "501 Syntax error")]
    [InlineData("HEAD 123 extra", "501 Syntax error")]
    [InlineData("BODY 12345678901234567", "501 Syntax error")]
    [InlineData("STAT <a@b.c> extra", "501 Syntax error")]
    [InlineData("CHECK", "501 Syntax error")]
    [InlineData("CHECK not-an-id", "501 Syntax error")]
    [InlineData("CHECK <a@b.c> extra", "501 Syntax error")]
    [InlineData("IHAVE", "501 Syntax error")]
    [InlineData("IHAVE not-an-id", "501 Syntax error")]
    [InlineData("AUTHINFO USER", "501 AUTHINFO USER requires a username")]
    [InlineData("AUTHINFO PASS", "501 AUTHINFO PASS requires a password")]
    [InlineData("AUTHINFO GENERIC x", "501 Unknown command variant")]
    [InlineData("COMPRESS", "501 COMPRESS requires a single algorithm argument")]
    [InlineData("COMPRESS deflate", "501 Syntactically incorrect compression algorithm")]
    [InlineData("QUIT extra", "501 Syntax error")]
    [InlineData("HELP MORE", "501 Syntax error")]
    [InlineData("SPEEDTEST", "501 Syntax error")]
    [InlineData("SPEEDTEST GIGANEWS extra", "501 Syntax error")]
    public async Task InvalidCommand_IsRejectedWithProtocolError_AndDoesNotReachHandler(string commandLine, string response)
    {
        await using var duplex = await ParserDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: true,
            postingPermitted: true,
            streamingPermitted: true));
        var dispatcher = new NntpCommandDispatcher();
        var writer = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, writer, commandLine);
        Assert.Equal(response, await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task OverlongCommandLine_Is501_AndTheNextCommandStillRuns()
    {
        await using var duplex = await ParserDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher();
        var writer = new NntpResponseWriter(duplex.ServerOutput);

        await dispatcher.DispatchAsync(
            session,
            NntpCommand.OverlongLine,
            ReadOnlyMemory<byte>.Empty,
            writer,
            CancellationToken.None);
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, writer, "QUIT");
        Assert.Equal("205 Connection closing", await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task InvalidCheck_DoesNotInvokeHandler_EvenWhenTransitAuthorized()
    {
        await using var duplex = await ParserDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: false,
            authorizedTransit: true,
            postingPermitted: false,
            streamingPermitted: true));
        var dispatcher = new NntpCommandDispatcher();
        var writer = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, writer, "CHECK not-an-id");
        var line = await duplex.ReadClientLineAsync();
        Assert.Equal("501 Syntax error", line);
        Assert.DoesNotContain("238", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NOSUCHCMD")]
    [InlineData("MODE")]
    [InlineData("AUTHINFO GENERIC x")]
    [InlineData("CHECK")]
    [InlineData("CHECK not-an-id")]
    [InlineData("IHAVE")]
    [InlineData("TAKETHIS not-an-id")]
    [InlineData("QUIT extra")]
    public async Task InvalidCommand_CannotConstructHandlerContext(string commandLine)
    {
        await using var duplex = await ParserDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var invalid = NntpCommandTestParse.ParseCommand(commandLine);
        Assert.False(invalid.IsValid);
        var writer = new NntpResponseWriter(duplex.ServerOutput);
        Assert.Throws<ArgumentException>(() =>
            new NntpCommandContext(session, invalid, ReadOnlyMemory<byte>.Empty, writer));
    }

    [Fact]
    public async Task Ihave_ValidMessageId_ParsesValid_AndReaderRoleGateAnswers502()
    {
        await using var duplex = await ParserDuplex.CreateAsync();
        var session = duplex.CreateSession(new ArticleIngestionQueue(new ArticleIngestionOptions()));
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: false,
            authorizedTransit: true,
            postingPermitted: false,
            streamingPermitted: true));
        var dispatcher = new NntpCommandDispatcher();
        var writer = new NntpResponseWriter(duplex.ServerOutput);

        var parsed = NntpCommandTestParse.ParseCommand("IHAVE <x@example>");
        Assert.True(parsed.IsValid);
        var dispatch = NntpCommandTestParse.DispatchAsync(dispatcher, session, writer, "IHAVE <x@example>").AsTask();

        // A valid IHAVE is parsed, but the Reader role gate answers before the handler (no 335, no article read).
        Assert.Contains(
            "502 Command not available on this server",
            await duplex.ReadClientLineAsync(),
            StringComparison.Ordinal);
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void MessageId_CommandArgument_UsesCommonValidator_AndFieldLimit()
    {
        Assert.True(NntpCommandParser.Parse("CHECK <x@example>"u8).IsValid);
        Assert.False(NntpCommandParser.Parse("CHECK <x>"u8).IsValid);
        Assert.False(NntpCommandParser.Parse("CHECK <@>"u8).IsValid);
        Assert.True(NntpCommandParser.Parse("ARTICLE <x@example>"u8).IsValid);

        var exactField = BuildMessageId(NntpMessageIdValidation.MaxMessageIdLength);
        Assert.True(NntpMessageIdValidation.IsValidMessageId(exactField));
        Assert.True(NntpCommandParser.Parse(BuildCommand("CHECK ", exactField)).IsValid);

        var overField = BuildMessageId(NntpMessageIdValidation.MaxMessageIdLength + 1);
        Assert.False(NntpMessageIdValidation.IsValidMessageId(overField));
        Assert.Equal(NntpParseStatus.InvalidArgument, NntpCommandParser.Parse(BuildCommand("CHECK ", overField)).Status);

        Assert.Equal(NntpParseStatus.InvalidArgument, NntpCommandParser.Parse("CHECK <>"u8).Status);
        Assert.Equal(NntpParseStatus.InvalidArgument, NntpCommandParser.Parse("CHECK <x>"u8).Status);
        Assert.Equal(NntpParseStatus.InvalidArgument, NntpCommandParser.Parse("CHECK <x@"u8).Status);
        Assert.Equal(NntpParseStatus.InvalidArgument, NntpCommandParser.Parse("CHECK not-an-id"u8).Status);
        Assert.Equal(NntpParseStatus.ExtraArgument, NntpCommandParser.Parse("CHECK <a b@c>"u8).Status);
        Assert.Equal(NntpParseStatus.InvalidArgument, NntpCommandParser.Parse("CHECK <a>b@c>"u8).Status);
    }

    private static byte[] BuildMessageId(int totalLength)
    {
        var bytes = new byte[totalLength];
        bytes[0] = (byte)'<';
        bytes[1] = (byte)'a';
        bytes[2] = (byte)'@';
        bytes[^1] = (byte)'>';
        if (totalLength > 4)
        {
            bytes.AsSpan(3, totalLength - 4).Fill((byte)'b');
        }

        return bytes;
    }

    private static byte[] BuildCommand(string prefix, ReadOnlySpan<byte> argument)
    {
        var prefixBytes = Encoding.ASCII.GetBytes(prefix);
        var line = new byte[prefixBytes.Length + argument.Length];
        prefixBytes.CopyTo(line);
        argument.CopyTo(line.AsSpan(prefixBytes.Length));
        return line;
    }

    private sealed class ParserDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public static Task<ParserDuplex> CreateAsync() => Task.FromResult(new ParserDuplex());

        public NntpSession CreateSession(IArticleIngestionQueue? queue = null)
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)));
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: queue);
        }

        public async Task WriteClientAsync(string payload)
        {
            await _clientToServer.Writer.WriteAsync(Encoding.ASCII.GetBytes(payload));
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
            Assert.NotNull(line);
            return line!;
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
            ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
