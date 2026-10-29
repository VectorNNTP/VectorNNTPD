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

/// <summary>
/// NEWNEWS and NEWGROUPS are intentionally unregistered. They must take the generic
/// unknown-command path and must not appear as capability tokens.
/// </summary>
public sealed class NewNewsAndNewGroupsRemovedTests
{
    [Theory]
    [InlineData("NEWNEWS")]
    [InlineData("NEWGROUPS")]
    [InlineData("newnews")]
    [InlineData("NewGroups")]
    [InlineData("NEWNEWS * 19990624 000000 GMT")]
    [InlineData("NEWGROUPS 19990624 000000 GMT")]
    public void Parser_TreatsCommandsAsUnknownVerb(string commandLine)
    {
        var parsed = NntpCommandTestParse.ParseCommand(commandLine);
        Assert.False(parsed.IsValid);
        Assert.Equal(NntpVerb.Unknown, parsed.Verb);
        Assert.Equal(NntpParseStatus.UnknownVerb, parsed.Status);
    }

    [Fact]
    public void Catalog_DoesNotRegisterCommands()
    {
        Assert.DoesNotContain("NEWNEWS", DefaultNntpCommandCatalog.InventoryKeys);
        Assert.DoesNotContain("NEWGROUPS", DefaultNntpCommandCatalog.InventoryKeys);
        Assert.DoesNotContain("NEWNEWS", DefaultNntpCommandCatalog.GetRegisteredKeys());
        Assert.DoesNotContain("NEWGROUPS", DefaultNntpCommandCatalog.GetRegisteredKeys());
        Assert.Contains("LIST NEWSGROUPS", DefaultNntpCommandCatalog.InventoryKeys);
    }

    [Fact]
    public void ListNewsgroups_RemainsARegisteredListKeyword()
    {
        var parsed = NntpCommandTestParse.ParseCommand("LIST NEWSGROUPS");
        Assert.True(parsed.IsValid);
        Assert.Equal(NntpVerb.List, parsed.Verb);
        Assert.Equal(NntpVerb.Newsgroups, parsed.Qualifier);
        Assert.Equal("LIST NEWSGROUPS", DefaultNntpCommandCatalog.DisplayName(parsed.Verb, parsed.Qualifier));
    }

    [Theory]
    [InlineData("NEWNEWS")]
    [InlineData("NEWGROUPS")]
    [InlineData("NEWNEWS * 19990624 000000 GMT")]
    [InlineData("NEWGROUPS 19990624 000000 GMT")]
    public async Task Dispatch_Unauthenticated_ReturnsGenericUnknownCommand(string commandLine)
    {
        await using var duplex = await RemovedDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, commandLine);
        var line = await duplex.ReadClientLineAsync();

        Assert.Equal("500 Unknown command", line);
        Assert.DoesNotContain("Command not implemented", line, StringComparison.Ordinal);
        Assert.DoesNotContain("480", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NEWNEWS")]
    [InlineData("NEWGROUPS")]
    public async Task Dispatch_FullyAuthorized_StillReturnsGenericUnknownCommand(string commandLine)
    {
        await using var duplex = await RemovedDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: true,
            postingPermitted: true,
            streamingPermitted: true));
        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, commandLine);
        var line = await duplex.ReadClientLineAsync();

        Assert.Equal("500 Unknown command", line);
        Assert.DoesNotContain("Command not implemented", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capabilities_DoesNotAdvertiseEitherCommand()
    {
        await using var duplex = await RemovedDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        var read = duplex.ReadUntilTerminatorAsync();
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "CAPABILITIES");
        var body = await read;
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("101 Capability list:", lines[0]);
        Assert.Contains("IHAVE", lines, StringComparer.Ordinal);
        Assert.Contains("STREAMING", lines, StringComparer.Ordinal);
        Assert.Contains("SPEEDTEST", lines, StringComparer.Ordinal);
        Assert.DoesNotContain(lines, static l => l.StartsWith("LIST", StringComparison.Ordinal));
        Assert.DoesNotContain("READER", lines, StringComparer.Ordinal);
        Assert.DoesNotContain("NEWNEWS", lines, StringComparer.Ordinal);
        Assert.DoesNotContain("NEWGROUPS", lines, StringComparer.Ordinal);
        Assert.DoesNotContain("NEWNEWS", body, StringComparison.Ordinal);
        Assert.DoesNotContain("NEWGROUPS", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListNewsgroups_OnTransit_ReturnsCommandNotAvailable()
    {
        await using var duplex = await RemovedDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "LIST NEWSGROUPS");
        Assert.Equal("502 Command not available on this server", await duplex.ReadClientLineAsync());
    }

    private sealed class RemovedDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public static Task<RemovedDuplex> CreateAsync() => Task.FromResult(new RemovedDuplex());

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

        public async Task<string> ReadUntilTerminatorAsync()
        {
            var builder = new StringBuilder();
            while (true)
            {
                var line = await ReadClientLineAsync();
                builder.Append(line);
                builder.Append('\n');
                if (line == ".")
                {
                    return builder.ToString();
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
