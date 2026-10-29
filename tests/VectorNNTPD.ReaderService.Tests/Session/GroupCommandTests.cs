using System.Buffers;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Newsgroups;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.ReaderService.Tests.Session;

public sealed class GroupCommandTests
{
    private static readonly NntpAuthorization Reader = new(
        isAuthenticated: true,
        authorizedReader: true,
        authorizedTransit: false,
        postingPermitted: false,
        streamingPermitted: false);

    [Fact]
    public async Task Group_Existing_WritesPrecomputed211_AndSelectsGroup()
    {
        var snapshot = SampleSnapshot();
        Assert.True(snapshot.TryGet("misc.test"u8, out var group));
        await using var duplex = await GroupDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(snapshot));
        await AssertExactAsync(duplex, session, "GROUP misc.test", group.GroupSelectedLine);
        Assert.True(session.HasSelectedGroup);
        Assert.True(session.SelectedGroupName.SequenceEqual("misc.test"u8));
    }

    [Fact]
    public async Task Group_IsCaseInsensitive_PreservesOriginalNameOnWire()
    {
        var snapshot = SampleSnapshot();
        Assert.True(snapshot.TryGet("misc.test"u8, out var group));
        await using var duplex = await GroupDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(snapshot));
        await AssertExactAsync(duplex, session, "GROUP MISC.TEST", group.GroupSelectedLine);
        Assert.True(group.GroupSelectedLine.Span.SequenceEqual("211 2089 3000234 3002322 misc.test\r\n"u8));
    }

    [Fact]
    public async Task Group_XAndJ_RemainDistinctFromProhibited()
    {
        var snapshot = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("group.closed", "", 100, 1, NewsgroupPostingStatus.NoPostingOrPeerArticles),
            new NewsgroupDefinition("group.junk", "", 50, 2, NewsgroupPostingStatus.PeerOnly),
            new NewsgroupDefinition("group.n", "", 100, 1, NewsgroupPostingStatus.Prohibited),
        ]);
        Assert.True(snapshot.TryGet("group.closed"u8, out var closed));
        Assert.True(snapshot.TryGet("group.junk"u8, out var junk));
        Assert.True(snapshot.TryGet("group.n"u8, out var prohibited));
        Assert.Equal(NewsgroupPostingStatus.NoPostingOrPeerArticles, closed.PostingStatus);
        Assert.Equal(NewsgroupPostingStatus.PeerOnly, junk.PostingStatus);
        Assert.Equal(NewsgroupPostingStatus.Prohibited, prohibited.PostingStatus);
        Assert.NotEqual(closed.PostingStatus, prohibited.PostingStatus);
        Assert.NotEqual(junk.PostingStatus, prohibited.PostingStatus);
        Assert.True(closed.ListActiveLine.Span.SequenceEqual("group.closed 100 1 x\r\n"u8));
        Assert.True(junk.ListActiveLine.Span.SequenceEqual("group.junk 50 2 j\r\n"u8));
        Assert.True(prohibited.ListActiveLine.Span.SequenceEqual("group.n 100 1 n\r\n"u8));

        await using var duplex = await GroupDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(snapshot));
        await AssertExactAsync(duplex, session, "GROUP group.closed", closed.GroupSelectedLine);
        Assert.True(closed.GroupSelectedLine.Span.SequenceEqual("211 100 1 100 group.closed\r\n"u8));
        await AssertExactAsync(duplex, session, "GROUP group.junk", junk.GroupSelectedLine);
        Assert.True(junk.GroupSelectedLine.Span.SequenceEqual("211 49 2 50 group.junk\r\n"u8));
    }

    [Fact]
    public async Task Group_Missing_Returns411_AndDoesNotChangeSelection()
    {
        await using var duplex = await GroupDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(SampleSnapshot()));
        await AssertLineAsync(duplex, session, "GROUP no.such.group", "411 No such newsgroup");
        Assert.False(session.HasSelectedGroup);
    }

    [Fact]
    public async Task ListGroup_IsRegisteredPlaceholder_Returns500()
    {
        await using var duplex = await GroupDuplex.CreateAsync();
        var session = duplex.CreateSession(new StaticNewsgroupCatalogue(SampleSnapshot()));
        await AssertLineAsync(duplex, session, "LISTGROUP misc.test", "500 Command not implemented");
        Assert.False(session.HasSelectedGroup);
    }

    [Fact]
    public async Task Group_DoesNotQueryMySql()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            Newsgroups = [new NntpGroupRow("misc.test", "Testing", 10, 2, (byte)'y')],
        };
        var db = new NntpDbService(
            factory,
            Options.Create(new NntpDbOptions
            {
                ConnectionString = TestHostFactory.TestNntpDbConnectionString,
                StartupTimeout = TimeSpan.FromSeconds(15),
            }),
            NullLogger<NntpDbService>.Instance);
        await db.StartAsync(CancellationToken.None);
        await using var catalogue = new NewsgroupCatalogueService(
            db,
            NullLogger<NewsgroupCatalogueService>.Instance,
            TimeProvider.System,
            TimeSpan.FromHours(1));
        await catalogue.StartAsync(CancellationToken.None);
        var queriesAfterLoad = factory.QueryNewsgroupsCount;

        await using var duplex = await GroupDuplex.CreateAsync();
        var session = duplex.CreateSession(catalogue);
        await AssertLinePrefixAsync(duplex, session, "GROUP misc.test", "211 ");
        Assert.Equal(queriesAfterLoad, factory.QueryNewsgroupsCount);
        await catalogue.StopAsync(CancellationToken.None);
    }

    private static NewsgroupSnapshot SampleSnapshot() =>
        NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("misc.test", "General testing", 3002322, 3000234, NewsgroupPostingStatus.Allowed),
        ]);

    private static async Task AssertExactAsync(
        GroupDuplex duplex,
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

    private static async Task AssertLineAsync(GroupDuplex duplex, NntpSession session, string command, string expected)
    {
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, command);
        Assert.Equal(expected, await duplex.ReadClientLineAsync());
    }

    private static async Task AssertLinePrefixAsync(GroupDuplex duplex, NntpSession session, string command, string prefix)
    {
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, command);
        Assert.StartsWith(prefix, await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    private sealed class GroupDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public static Task<GroupDuplex> CreateAsync() => Task.FromResult(new GroupDuplex());

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
