using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.Newsgroups;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Session;

public sealed class ListCommandTests
{
    private static readonly NntpAuthorization Reader = new(
        isAuthenticated: true,
        authorizedReader: true,
        authorizedTransit: false,
        postingPermitted: false,
        streamingPermitted: false);

    [Fact]
    public async Task List_AndListActive_WritePrecomputedBytes()
    {
        var snapshot = SampleSnapshot();
        var catalogue = new StaticNewsgroupCatalogue(snapshot);
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(catalogue);

        await AssertExactAsync(duplex, session, "LIST", snapshot.ListActiveComplete);
        await AssertExactAsync(duplex, session, "LIST ACTIVE", snapshot.ListActiveComplete);
        Assert.Equal(2, catalogue.CurrentReadCount);
    }

    [Fact]
    public async Task ListActive_Wildmat_MatchesGroupNameOnly()
    {
        var snapshot = SampleSnapshot();
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(snapshot));
        var expected = Concat(
            NntpResponses.ListOfNewsgroupsFollows,
            "alt.rfc-writers.recovery 4 1 y\r\n"u8.ToArray(),
            "tx.natives.recovery 89 56 n\r\n"u8.ToArray(),
            NntpResponses.MultilineTerminator);

        await AssertExactAsync(duplex, session, "LIST ACTIVE *.recovery", expected);
    }

    [Fact]
    public async Task ListActive_EmitsExactXAndJStatusBytes()
    {
        var snapshot = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("group.closed", "", 100, 1, NewsgroupPostingStatus.NoPostingOrPeerArticles),
            new NewsgroupDefinition("group.junk", "", 100, 1, NewsgroupPostingStatus.PeerOnly),
        ]);
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(snapshot));
        var expected = Concat(
            NntpResponses.ListOfNewsgroupsFollows,
            "group.closed 100 1 x\r\n"u8.ToArray(),
            "group.junk 100 1 j\r\n"u8.ToArray(),
            NntpResponses.MultilineTerminator);

        await AssertExactAsync(duplex, session, "LIST ACTIVE", expected);
        Assert.DoesNotContain("=", Encoding.ASCII.GetString(expected));
    }

    [Fact]
    public async Task ListActive_Wildmat_PreservesXAndJStatusBytes()
    {
        var snapshot = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("alt.closed", "", 10, 1, NewsgroupPostingStatus.NoPostingOrPeerArticles),
            new NewsgroupDefinition("alt.junk", "", 20, 2, NewsgroupPostingStatus.PeerOnly),
            new NewsgroupDefinition("misc.test", "", 3, 1, NewsgroupPostingStatus.Allowed),
        ]);
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(snapshot));
        var expected = Concat(
            NntpResponses.ListOfNewsgroupsFollows,
            "alt.closed 10 1 x\r\n"u8.ToArray(),
            "alt.junk 20 2 j\r\n"u8.ToArray(),
            NntpResponses.MultilineTerminator);

        await AssertExactAsync(duplex, session, "LIST ACTIVE alt.*", expected);
    }

    [Fact]
    public async Task ListActive_CommaWildmat_UsesRightmostMatch()
    {
        var snapshot = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("aaa", "", 3, 1, NewsgroupPostingStatus.Allowed),
            new NewsgroupDefinition("abb", "", 4, 1, NewsgroupPostingStatus.Prohibited),
            new NewsgroupDefinition("ccb", "", 5, 1, NewsgroupPostingStatus.Moderated),
        ]);
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(snapshot));
        var expected = Concat(
            NntpResponses.ListOfNewsgroupsFollows,
            "aaa 3 1 y\r\n"u8.ToArray(),
            "ccb 5 1 m\r\n"u8.ToArray(),
            NntpResponses.MultilineTerminator);

        await AssertExactAsync(duplex, session, "LIST ACTIVE a*,!*b,*c*", expected);
    }

    [Fact]
    public async Task ListCounts_WritesPrecomputedFiveFieldLines()
    {
        var snapshot = SampleSnapshot();
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(snapshot));
        await AssertExactAsync(duplex, session, "LIST COUNTS", snapshot.ListCountsComplete);

        var wire = Encoding.ASCII.GetString(snapshot.ListCountsComplete.Span);
        Assert.StartsWith("215 list of newsgroups follows\r\n", wire, StringComparison.Ordinal);
        Assert.EndsWith(".\r\n", wire, StringComparison.Ordinal);
        Assert.Contains("misc.test 3002322 3000234 2089 y\r\n", wire, StringComparison.Ordinal);
        Assert.DoesNotContain('\t', wire);
    }

    [Fact]
    public async Task ListCounts_Estimate_HighGreaterThanLow()
    {
        var snapshot = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("misc.test", "", 3002322, 3000234, NewsgroupPostingStatus.Allowed),
        ]);
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(snapshot));
        await AssertExactAsync(
            duplex,
            session,
            "LIST COUNTS",
            Concat(
                NntpResponses.ListOfNewsgroupsFollows,
                "misc.test 3002322 3000234 2089 y\r\n"u8.ToArray(),
                NntpResponses.MultilineTerminator));
        Assert.Equal(2089UL, Assert.Single(snapshot.Groups).EstimatedCount);
    }

    [Fact]
    public async Task ListCounts_Estimate_HighEqualsLow_UsesGroupSemantics()
    {
        var emptyZero = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("local.empty-zero", "", 0, 0, NewsgroupPostingStatus.Allowed),
        ]);
        var singleArticle = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("local.one", "", 5, 5, NewsgroupPostingStatus.Allowed),
        ]);
        Assert.Equal(0UL, Assert.Single(emptyZero.Groups).EstimatedCount);
        Assert.Equal(1UL, Assert.Single(singleArticle.Groups).EstimatedCount);

        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(emptyZero));
        await AssertExactAsync(
            duplex,
            session,
            "LIST COUNTS",
            Concat(
                NntpResponses.ListOfNewsgroupsFollows,
                "local.empty-zero 0 0 0 y\r\n"u8.ToArray(),
                NntpResponses.MultilineTerminator));
        session = duplex.CreateSession(new StaticNewsgroupCatalogue(singleArticle));
        await AssertExactAsync(
            duplex,
            session,
            "LIST COUNTS",
            Concat(
                NntpResponses.ListOfNewsgroupsFollows,
                "local.one 5 5 1 y\r\n"u8.ToArray(),
                NntpResponses.MultilineTerminator));
    }

    [Fact]
    public async Task ListCounts_Estimate_HighLessThanLow_IsZero()
    {
        var snapshot = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("local.empty", "", 7, 8, NewsgroupPostingStatus.Allowed),
        ]);
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(snapshot));
        await AssertExactAsync(
            duplex,
            session,
            "LIST COUNTS",
            Concat(
                NntpResponses.ListOfNewsgroupsFollows,
                "local.empty 7 8 0 y\r\n"u8.ToArray(),
                NntpResponses.MultilineTerminator));
        Assert.Equal(0UL, Assert.Single(snapshot.Groups).EstimatedCount);
    }

    [Fact]
    public async Task ListCounts_EmitsExactStatusOctets()
    {
        var snapshot = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("group.y", "", 10, 1, NewsgroupPostingStatus.Allowed),
            new NewsgroupDefinition("group.n", "", 10, 1, NewsgroupPostingStatus.Prohibited),
            new NewsgroupDefinition("group.m", "", 10, 1, NewsgroupPostingStatus.Moderated),
            new NewsgroupDefinition("group.x", "", 10, 1, NewsgroupPostingStatus.NoPostingOrPeerArticles),
            new NewsgroupDefinition("group.j", "", 10, 1, NewsgroupPostingStatus.PeerOnly),
        ]);
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(snapshot));
        await AssertExactAsync(
            duplex,
            session,
            "LIST COUNTS",
            Concat(
                NntpResponses.ListOfNewsgroupsFollows,
                "group.j 10 1 10 j\r\n"u8.ToArray(),
                "group.m 10 1 10 m\r\n"u8.ToArray(),
                "group.n 10 1 10 n\r\n"u8.ToArray(),
                "group.x 10 1 10 x\r\n"u8.ToArray(),
                "group.y 10 1 10 y\r\n"u8.ToArray(),
                NntpResponses.MultilineTerminator));
    }

    [Fact]
    public async Task ListCounts_Wildmat_MatchesGroupNameOnly()
    {
        var snapshot = SampleSnapshot();
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(snapshot));
        var expected = Concat(
            NntpResponses.ListOfNewsgroupsFollows,
            "alt.rfc-writers.recovery 4 1 4 y\r\n"u8.ToArray(),
            "tx.natives.recovery 89 56 34 n\r\n"u8.ToArray(),
            NntpResponses.MultilineTerminator);

        await AssertExactAsync(duplex, session, "LIST COUNTS *.recovery", expected);
    }

    [Fact]
    public async Task ListCounts_CommaWildmat_UsesRightmostMatch()
    {
        var snapshot = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("aaa", "", 3, 1, NewsgroupPostingStatus.Allowed),
            new NewsgroupDefinition("abb", "", 4, 1, NewsgroupPostingStatus.Prohibited),
            new NewsgroupDefinition("ccb", "", 5, 1, NewsgroupPostingStatus.Moderated),
        ]);
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(snapshot));
        var expected = Concat(
            NntpResponses.ListOfNewsgroupsFollows,
            "aaa 3 1 3 y\r\n"u8.ToArray(),
            "ccb 5 1 5 m\r\n"u8.ToArray(),
            NntpResponses.MultilineTerminator);

        await AssertExactAsync(duplex, session, "LIST COUNTS a*,!*b,*c*", expected);
    }

    [Fact]
    public async Task ListCounts_NoMatch_ReturnsEmptyMultiline215()
    {
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(SampleSnapshot()));
        var expected = Concat(NntpResponses.ListOfNewsgroupsFollows, NntpResponses.MultilineTerminator);
        await AssertExactAsync(duplex, session, "LIST COUNTS no.such.*", expected);
    }

    [Fact]
    public async Task ListCounts_CapturesExactlyOneSnapshot()
    {
        var first = SampleSnapshot();
        var second = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("only.new", "n", 2, 1, NewsgroupPostingStatus.Allowed),
        ]);
        var catalogue = new SwapOnSecondReadCatalogue(first, second);
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(catalogue);

        await AssertExactAsync(duplex, session, "LIST COUNTS", first.ListCountsComplete);
        Assert.Equal(1, catalogue.Reads);
    }

    [Fact]
    public async Task ListActive_MalformedWildmat_Returns501()
    {
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(SampleSnapshot()));
        await AssertLineAsync(duplex, session, "LIST ACTIVE u[ks].*", "501 Syntax error");
        await AssertLineAsync(duplex, session, "LIST COUNTS u[ks].*", "501 Syntax error");
    }

    [Fact]
    public async Task ListActive_NoMatch_ReturnsEmptyMultiline215()
    {
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(SampleSnapshot()));
        var expected = Concat(NntpResponses.ListOfNewsgroupsFollows, NntpResponses.MultilineTerminator);
        await AssertExactAsync(duplex, session, "LIST ACTIVE no.such.*", expected);
    }

    [Fact]
    public async Task ListNewsgroups_WritesPrecomputedUtf8Lines()
    {
        var snapshot = SampleSnapshot();
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(snapshot));
        await AssertExactAsync(duplex, session, "LIST NEWSGROUPS", snapshot.ListNewsgroupsComplete);
    }

    [Fact]
    public async Task ListNewsgroups_Wildmat_MatchesGroupName_WritesPrecomputedLine()
    {
        var snapshot = SampleSnapshot();
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(snapshot));
        var expected = Concat(
            NntpResponses.ListOfNewsgroupsFollows,
            "misc.test General testing\r\n"u8.ToArray(),
            NntpResponses.MultilineTerminator);

        await AssertExactAsync(duplex, session, "LIST NEWSGROUPS misc.*", expected);
    }

    [Fact]
    public async Task ListNewsgroups_Wildmat_DoesNotMatchDescription()
    {
        var snapshot = SampleSnapshot();
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(snapshot));
        var expected = Concat(NntpResponses.ListOfNewsgroupsFollows, NntpResponses.MultilineTerminator);
        await AssertExactAsync(duplex, session, "LIST NEWSGROUPS *Testing*", expected);
    }

    [Fact]
    public async Task ListNewsgroups_NoMatch_ReturnsEmptyMultiline215()
    {
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(SampleSnapshot()));
        var expected = Concat(NntpResponses.ListOfNewsgroupsFollows, NntpResponses.MultilineTerminator);
        await AssertExactAsync(duplex, session, "LIST NEWSGROUPS zzz.*", expected);
    }

    [Fact]
    public async Task List_CapturesExactlyOneSnapshot()
    {
        var first = SampleSnapshot();
        var second = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("only.new", "n", 2, 1, NewsgroupPostingStatus.Allowed),
        ]);
        var catalogue = new SwapOnSecondReadCatalogue(first, second);
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(catalogue);

        await AssertExactAsync(duplex, session, "LIST", first.ListActiveComplete);
        Assert.Equal(1, catalogue.Reads);
    }

    [Fact]
    public async Task List_AfterRefresh_NewCommandsSeeNewSnapshot()
    {
        var first = SampleSnapshot();
        var second = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("only.new", "n", 2, 1, NewsgroupPostingStatus.Allowed),
        ]);
        var catalogue = new StaticNewsgroupCatalogue(first);
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(catalogue);
        await AssertExactAsync(duplex, session, "LIST", first.ListActiveComplete);
        catalogue.Publish(second);
        await AssertExactAsync(duplex, session, "LIST NEWSGROUPS", second.ListNewsgroupsComplete);
    }

    [Fact]
    public async Task ListOverviewFmt_WritesPrecomputedStandardFields()
    {
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(SampleSnapshot()));
        await AssertExactAsync(duplex, session, "LIST OVERVIEW.FMT", NntpResponses.ListOverviewFmtComplete);

        var wire = Encoding.ASCII.GetString(NntpResponses.ListOverviewFmtComplete.Span);
        Assert.Equal(
            "215 Order of fields in overview database.\r\n" +
            "Subject:\r\nFrom:\r\nDate:\r\nMessage-ID:\r\nReferences:\r\nBytes:\r\nLines:\r\n.\r\n",
            wire);
        Assert.Contains("Bytes:\r\n", wire, StringComparison.Ordinal);
        Assert.Contains("Lines:\r\n", wire, StringComparison.Ordinal);
        Assert.DoesNotContain(":bytes", wire, StringComparison.Ordinal);
        Assert.DoesNotContain(":lines", wire, StringComparison.Ordinal);
        Assert.True(NntpResponses.ListOverviewFmtComplete.Span.EndsWith(".\r\n"u8));
    }

    [Theory]
    [InlineData("LIST HEADERS")]
    [InlineData("LIST HEADERS MSGID")]
    [InlineData("LIST HEADERS RANGE")]
    [InlineData("list headers msgid")]
    public async Task ListHeaders_WritesSamePrecomputedFieldList(string command)
    {
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(SampleSnapshot()));
        await AssertExactAsync(duplex, session, command, NntpResponses.ListHeadersComplete);

        var wire = Encoding.ASCII.GetString(NntpResponses.ListHeadersComplete.Span);
        Assert.Equal(
            "215 headers and metadata items supported:\r\n" +
            "Subject\r\nFrom\r\nDate\r\nMessage-ID\r\nReferences\r\n:bytes\r\n:lines\r\n.\r\n",
            wire);
        Assert.DoesNotContain("Subject:", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("Bytes:", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("Lines:", wire, StringComparison.Ordinal);
        Assert.True(NntpResponses.ListHeadersComplete.Span.EndsWith(".\r\n"u8));
    }

    [Fact]
    public async Task ListMotd_RemainsRecognizedWithoutStoredData()
    {
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(SampleSnapshot()));
        await AssertLineAsync(duplex, session, "LIST MOTD", "503 Data item not stored");
    }

    [Theory]
    [InlineData("LIST ACTIVE.TIMES")]
    [InlineData("LIST DISTRIBUTIONS")]
    [InlineData("LIST DISTRIB.PATS")]
    [InlineData("LIST MODERATORS")]
    [InlineData("LIST SUBSCRIPTIONS")]
    [InlineData("LIST SOMETHING-THAT-DOES-NOT-EXIST")]
    public async Task UnknownListKeyword_Returns501UnknownCommandVariant(string command)
    {
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(SampleSnapshot()));
        await AssertLineAsync(duplex, session, command, "501 Unknown command variant");
    }

    [Theory]
    [InlineData("LIST ACTIVE foo bar")]
    [InlineData("LIST COUNTS foo bar")]
    [InlineData("LIST MOTD extra")]
    [InlineData("LIST OVERVIEW.FMT extra")]
    [InlineData("LIST HEADERS MSGID extra")]
    public async Task MalformedList_Returns501SyntaxError(string command)
    {
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(SampleSnapshot()));
        await AssertLineAsync(duplex, session, command, "501 Syntax error");
    }

    [Fact]
    public async Task ListHeaders_InvalidArgument_Returns501SyntaxError()
    {
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(SampleSnapshot()));
        await AssertLineAsync(duplex, session, "LIST HEADERS FOO", "501 Syntax error");
    }

    [Fact]
    public async Task Capabilities_AdvertisesOnlyImplementedListKeywords()
    {
        await using var duplex = await ListDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(SampleSnapshot()));
        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "CAPABILITIES");
        var body = await duplex.ReadUntilTerminatorAsync();
        var lines = body.Split('\n');
        Assert.Equal("101 Capability list:", lines[0]);
        Assert.Equal("VERSION 2", lines[1]);
        Assert.Contains("LIST ACTIVE COUNTS HEADERS NEWSGROUPS OVERVIEW.FMT", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ACTIVE.TIMES", body, StringComparison.Ordinal);
        Assert.DoesNotContain("MOTD", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DISTRIB", body, StringComparison.Ordinal);
        Assert.DoesNotContain("MODERATORS", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SUBSCRIPTIONS", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\nOVER\n", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\nHDR\n", body, StringComparison.Ordinal);
        Assert.DoesNotContain("NEWNEWS", body, StringComparison.Ordinal);
        Assert.DoesNotContain("NEWGROUPS", body, StringComparison.Ordinal);
        Assert.Equal(".", lines[^1]);
    }

    private static NewsgroupSnapshot SampleSnapshot() =>
        NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("alt.rfc-writers.recovery", "RFC Writers Recovery", 4, 1, NewsgroupPostingStatus.Allowed),
            new NewsgroupDefinition("misc.test", "General testing", 3002322, 3000234, NewsgroupPostingStatus.Allowed),
            new NewsgroupDefinition("tx.natives.recovery", "Texas Natives Recovery", 89, 56, NewsgroupPostingStatus.Prohibited),
        ]);

    private static async Task AssertExactAsync(
        ListDuplex duplex,
        NntpSession session,
        string command,
        ReadOnlyMemory<byte> expected)
    {
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();
        var read = duplex.ReadExactAsync(expected.Length);
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, command);
        Assert.True((await read).AsSpan().SequenceEqual(expected.Span));
    }

    private static async Task AssertLineAsync(ListDuplex duplex, NntpSession session, string command, string expected)
    {
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, command);
        Assert.Equal(expected, await duplex.ReadClientLineAsync());
    }

    private static byte[] Concat(params ReadOnlyMemory<byte>[] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.Span.CopyTo(result.AsSpan(offset));
            offset += part.Length;
        }

        return result;
    }

    private sealed class SwapOnSecondReadCatalogue : INewsgroupCatalogue
    {
        private readonly NewsgroupSnapshot _first;
        private readonly NewsgroupSnapshot _second;

        public SwapOnSecondReadCatalogue(NewsgroupSnapshot first, NewsgroupSnapshot second)
        {
            _first = first;
            _second = second;
        }

        public int Reads { get; private set; }

        public NewsgroupSnapshot Current
        {
            get
            {
                Reads++;
                return Reads == 1 ? _first : _second;
            }
        }
    }

    private sealed class ListDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public static Task<ListDuplex> CreateAsync() => Task.FromResult(new ListDuplex());

        public NntpSession CreateSession(INewsgroupCatalogue catalogue)
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)));
            var session = new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                newsgroupCatalogue: catalogue);
            session.SetAuthorization(Reader);
            return session;
        }

        public async Task<byte[]> ReadExactAsync(int length)
        {
            var buffer = new byte[length];
            var copied = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (copied < length)
            {
                var result = await _serverToClient.Reader.ReadAsync(timeout.Token);
                var unread = result.Buffer;
                var take = (int)Math.Min(unread.Length, length - copied);
                unread.Slice(0, take).CopyTo(buffer.AsSpan(copied, take));
                copied += take;
                _serverToClient.Reader.AdvanceTo(unread.GetPosition(take));
                if (result.IsCompleted && copied < length)
                {
                    throw new InvalidOperationException($"Pipe completed after {copied} of {length} bytes.");
                }
            }

            return buffer;
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
            Assert.NotNull(line);
            return line!;
        }

        public async Task<string> ReadUntilTerminatorAsync()
        {
            var lines = new List<string>();
            string line;
            do
            {
                line = await ReadClientLineAsync();
                lines.Add(line);
            }
            while (line != ".");

            return string.Join('\n', lines);
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
        public Task WaitForOutboundDeliveryAsync(long outboundIdleVersionBeforeFlush, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WaitForOutboundDeliveryAndPauseReadsAsync(long outboundIdleVersionBeforeFlush, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAsync(Exception? exception = null)
        {
            _cts.Cancel();
            return Task.CompletedTask;
        }

        public Task UpgradeToTlsAsync(VectorNNTPD.Common.Networking.Certificates.ITlsCertificateContextProvider certificateProvider, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
