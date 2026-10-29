using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Commands;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Tests.Session;

public sealed class NntpCommandInventoryTests
{
    [Fact]
    public void Catalog_RegistersCompleteInventory()
    {
        var keys = DefaultNntpCommandCatalog.GetRegisteredKeys();

        // Production inventory remains complete; BENCHIT is an internal extra (not in InventoryKeys).
        foreach (var key in DefaultNntpCommandCatalog.InventoryKeys)
        {
            Assert.Contains(key, keys);
        }

        Assert.Contains("BENCHIT", keys);
        Assert.DoesNotContain("BENCHIT", DefaultNntpCommandCatalog.InventoryKeys);
        Assert.DoesNotContain("NEWNEWS", DefaultNntpCommandCatalog.InventoryKeys);
        Assert.DoesNotContain("NEWGROUPS", DefaultNntpCommandCatalog.InventoryKeys);
        Assert.DoesNotContain("NEWNEWS", keys);
        Assert.DoesNotContain("NEWGROUPS", keys);
        Assert.Equal(DefaultNntpCommandCatalog.InventoryKeys.Count + 1, keys.Count);
    }

    [Theory]
    [InlineData("CAPABILITIES")]
    [InlineData("DATE")]
    [InlineData("HELP")]
    [InlineData("QUIT")]
    [InlineData("STARTTLS")]
    [InlineData("LIST")]
    [InlineData("LIST ACTIVE")]
    [InlineData("LIST COUNTS")]
    [InlineData("LIST COUNTS misc.*")]
    [InlineData("LIST HEADERS")]
    [InlineData("LIST HEADERS MSGID")]
    [InlineData("LIST HEADERS RANGE")]
    [InlineData("LIST MOTD")]
    [InlineData("LIST NEWSGROUPS")]
    [InlineData("LIST OVERVIEW.FMT")]
    [InlineData("GROUP misc.test")]
    [InlineData("LISTGROUP")]
    [InlineData("LISTGROUP misc.test")]
    [InlineData("ARTICLE")]
    [InlineData("COMPRESS DEFLATE")]
    [InlineData("MODE READER")]
    [InlineData("MODE STREAM")]
    [InlineData("AUTHINFO USER x")]
    [InlineData("AUTHINFO PASS y")]
    [InlineData("AUTHINFO SASL PLAIN")]
    [InlineData("IHAVE <msg@example.com>")]
    [InlineData("CHECK <msg@example.com>")]
    [InlineData("TAKETHIS <msg@example.com>")]
    [InlineData("SPEEDTEST GIGANEWS")]
    public void Parser_AcceptsInventoryCommands(string commandLine)
    {
        var parsed = NntpCommandTestParse.ParseCommand(commandLine);
        Assert.True(parsed.IsValid);
        Assert.NotEqual(NntpVerb.Unknown, parsed.Verb);
        Assert.NotEqual(NntpVerb.None, parsed.Verb);
    }

    [Theory]
    [InlineData("LIST ACTIVE.TIMES")]
    [InlineData("LIST DISTRIB.PATS")]
    [InlineData("LIST DISTRIBUTIONS")]
    [InlineData("LIST MODERATORS")]
    [InlineData("LIST SUBSCRIPTIONS")]
    [InlineData("LIST SUBSCRIPTIONS *")]
    public void Parser_RejectsUnsupportedListVariants(string commandLine)
    {
        var parsed = NntpCommandTestParse.ParseCommand(commandLine);
        Assert.Equal(NntpVerb.List, parsed.Verb);
        Assert.Equal(NntpParseStatus.UnknownQualifier, parsed.Status);
        Assert.False(parsed.IsValid);
    }

    [Fact]
    public void Inventory_ExcludesUnsupportedListVariants()
    {
        var keys = DefaultNntpCommandCatalog.InventoryKeys;
        Assert.Contains("LIST", keys);
        Assert.Contains("LIST ACTIVE", keys);
        Assert.Contains("LIST COUNTS", keys);
        Assert.DoesNotContain("LIST ACTIVE.TIMES", keys);
        Assert.DoesNotContain("LIST DISTRIB.PATS", keys);
        Assert.DoesNotContain("LIST DISTRIBUTIONS", keys);
        Assert.DoesNotContain("LIST MODERATORS", keys);
        Assert.DoesNotContain("LIST SUBSCRIPTIONS", keys);

        var registered = DefaultNntpCommandCatalog.GetRegisteredKeys();
        Assert.DoesNotContain("LIST ACTIVE.TIMES", registered);
        Assert.Contains("LIST COUNTS", registered);
        Assert.DoesNotContain("LIST DISTRIB.PATS", registered);
        Assert.DoesNotContain("LIST DISTRIBUTIONS", registered);
        Assert.DoesNotContain("LIST MODERATORS", registered);
        Assert.DoesNotContain("LIST SUBSCRIPTIONS", registered);
    }

    [Fact]
    public void Registry_ExactMatch_RejectsPrefixConfusion()
    {
        var articleX = NntpCommandTestParse.ParseCommand("ARTICLEX");
        Assert.Equal(NntpVerb.Unknown, articleX.Verb);
        Assert.Equal(NntpParseStatus.UnknownVerb, articleX.Status);

        var userXyz = NntpCommandTestParse.ParseCommand("AUTHINFO USERXYZ");
        Assert.Equal(NntpVerb.AuthInfo, userXyz.Verb);
        Assert.Equal(NntpParseStatus.UnknownQualifier, userXyz.Status);
    }

    [Fact]
    public void Parser_Date_IsValidPublicCommand()
    {
        var parsed = NntpCommandTestParse.ParseCommand("DATE");
        Assert.True(parsed.IsValid);
        Assert.Equal(NntpVerb.Date, parsed.Verb);
        Assert.Equal(0, parsed.TokenCount);
        Assert.Equal("DATE", DefaultNntpCommandCatalog.DisplayName(parsed.Verb, parsed.Qualifier));
        Assert.Equal(NntpCommandAccess.Public, DefaultNntpCommandCatalog.GetAccess(parsed.Verb, parsed.Qualifier));
    }

    [Fact]
    public async Task List_WithoutCatalogue_Returns500AfterAuthz()
    {
        await using var duplex = await InventoryDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: false,
            postingPermitted: false,
            streamingPermitted: false));

        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "LIST");
        Assert.Contains("500 Command not implemented", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Placeholder_List_WithoutAuth_StillReturns480()
    {
        await using var duplex = await InventoryDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "LIST");
        Assert.Contains("480 Authentication required", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Unknown LIST keywords stay UnknownQualifier / 501 Unknown command variant,
    /// matching the old registry (not a new parser invention).
    /// </summary>
    [Theory]
    [InlineData("LIST ACTIVE.TIMES")]
    [InlineData("LIST DISTRIB.PATS")]
    [InlineData("LIST DISTRIBUTIONS")]
    [InlineData("LIST MODERATORS")]
    [InlineData("LIST SUBSCRIPTIONS")]
    [InlineData("LIST SUBSCRIPTIONS *")]
    public async Task UnsupportedListVariants_Return501UnknownCommandVariant(string commandLine)
    {
        await using var duplex = await InventoryDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, commandLine);
        Assert.Equal("501 Unknown command variant", await duplex.ReadClientLineAsync());
    }

    [Theory]
    [InlineData("LIST HEADERS")]
    [InlineData("LIST OVERVIEW.FMT")]
    public async Task ImplementedStaticListKeywords_Return215AfterAuthzWithoutCatalogue(string commandLine)
    {
        await using var duplex = await InventoryDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: false,
            postingPermitted: false,
            streamingPermitted: false));

        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, commandLine);
        Assert.StartsWith("215 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListMotd_RemainsRecognizedWithoutStoredData_Return503AfterAuthz()
    {
        await using var duplex = await InventoryDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: false,
            postingPermitted: false,
            streamingPermitted: false));

        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "LIST MOTD");
        Assert.Equal("503 Data item not stored", await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task CompressDeflate_IsRegisteredPublic_Returns206()
    {
        await using var duplex = await InventoryDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "COMPRESS DEFLATE");
        Assert.Contains("206 Compression active", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.True(session.Connection.IsCompressed);
    }

    private sealed class InventoryDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public static Task<InventoryDuplex> CreateAsync() => Task.FromResult(new InventoryDuplex());

        public NntpSession CreateSession()
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)));
            return new NntpSession(connection, NullLogger<NntpSession>.Instance);
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

        public bool IsCompressed => Volatile.Read(ref _compressed) == 1;
        public CancellationToken ConnectionClosed => _cts.Token;
        public bool IsCompleted => _cts.IsCancellationRequested;
        public long OutboundIdleVersion => 0;

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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

        private int _compressed;
    }
}
