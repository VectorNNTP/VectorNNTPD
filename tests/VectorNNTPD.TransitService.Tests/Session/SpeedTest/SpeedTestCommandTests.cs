using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.History;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Session.SpeedTest;
using VectorNNTPD.TransitService.Tests.Transit;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Tests.Session.SpeedTest;

/// <summary>
/// SPEEDTEST parser-adjacent dispatch, Transit binding, limits, and measurement contracts.
/// </summary>
public sealed class SpeedTestCommandTests
{
    [Fact]
    public void Parser_RejectsControlBytesAndOverlongPeer()
    {
        var tooLong = "SPEEDTEST " + new string('A', TransitPeersOptionsValidator.MaxPeerIdentifierLength + 1);
        Assert.Equal(NntpParseStatus.InvalidArgument, NntpCommandTestParse.ParseCommand(tooLong).Status);

        var control = "SPEEDTEST " + ((char)0x01) + "peer";
        Assert.Equal(NntpParseStatus.InvalidArgument, NntpCommandTestParse.ParseCommand(control).Status);
    }

    [Fact]
    public async Task Unauthenticated_Returns480()
    {
        await using var duplex = await SpeedTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        await DispatchAsync(session, duplex, "SPEEDTEST GIGANEWS");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task AuthenticatedWithoutTransit_Returns502()
    {
        await using var duplex = await SpeedTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: false,
            postingPermitted: false,
            streamingPermitted: false));
        await DispatchAsync(session, duplex, "SPEEDTEST GIGANEWS");
        Assert.Equal("502 Permission denied", await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task UnknownPeer_AndArbitraryHostOrIp_Return502()
    {
        await using var duplex = await SpeedTestDuplex.CreateAsync();
        var session = duplex.CreateAuthorizedSession();

        await DispatchAsync(session, duplex, "SPEEDTEST NOSUCH");
        Assert.Equal("502 UNKNOWN SPEEDTEST PEER", await duplex.ReadClientLineAsync());

        await DispatchAsync(session, duplex, "SPEEDTEST news.example.com");
        Assert.Equal("502 UNKNOWN SPEEDTEST PEER", await duplex.ReadClientLineAsync());

        await DispatchAsync(session, duplex, "SPEEDTEST 192.0.2.1");
        Assert.Equal("502 UNKNOWN SPEEDTEST PEER", await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task IdentifierUsenetNinja_Succeeds_AndReportsPeerName()
    {
        await using var duplex = await SpeedTestDuplex.CreateAsync();
        duplex.Store.Replace(
            TransitTestPeers.Snapshot(
                "usenet-ninja",
                TransitTestPeers.Peer(allowFrom: ["127.0.0.1"], peerName: "Usenet Ninja")));
        var session = duplex.CreateAuthorizedSession("usenet-ninja");

        var read = duplex.ReadSpeedTestAsync();
        await DispatchAsync(session, duplex, "SPEEDTEST usenet-ninja");
        var result = await read;

        Assert.Equal("290 SPEEDTEST usenet-ninja TX", result.Ready);
        Assert.Equal("291 SPEEDTEST COMPLETE", result.Complete);
        Assert.Contains("PEER=usenet-ninja", result.Fields);
        Assert.Contains("PEERNAME=Usenet Ninja", result.Fields);
        Assert.DoesNotContain("PEER=Usenet Ninja", result.Fields);
    }

    [Fact]
    public void DisplayNameTwoTokens_IsExtraArgument()
    {
        var parsed = NntpCommandTestParse.ParseCommand("SPEEDTEST Usenet Ninja");
        Assert.Equal(NntpParseStatus.ExtraArgument, parsed.Status);
        Assert.False(parsed.IsValid);
    }

    [Fact]
    public async Task PeerNameIsNotACommandArgument()
    {
        await using var duplex = await SpeedTestDuplex.CreateAsync();
        duplex.Store.Replace(
            TransitTestPeers.Snapshot(
                "usenet-ninja",
                TransitTestPeers.Peer(allowFrom: ["127.0.0.1"], peerName: "Usenet Ninja")));
        var session = duplex.CreateAuthorizedSession("usenet-ninja");
        await DispatchAsync(session, duplex, "SPEEDTEST Usenet");
        Assert.Equal("502 UNKNOWN SPEEDTEST PEER", await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task SessionPeerMismatch_Returns502()
    {
        await using var duplex = await SpeedTestDuplex.CreateAsync();
        var store = duplex.Store;
        store.Replace(
            TransitConfigurationSnapshot.Create(
                new TransitPeersOptions
                {
                    ["GIGANEWS"] = TransitTestPeers.Peer(allowFrom: ["127.0.0.1"]),
                    ["INVISION"] = TransitTestPeers.Peer(allowFrom: ["127.0.0.2"]),
                }));
        var session = duplex.CreateAuthorizedSession("GIGANEWS");
        await DispatchAsync(session, duplex, "SPEEDTEST INVISION");
        Assert.Equal("502 SPEEDTEST PEER MISMATCH", await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task CapabilitiesAndHelp_AdvertiseSpeedTest()
    {
        await using var duplex = await SpeedTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var caps = duplex.ReadMultilineAsync();
        await DispatchAsync(session, duplex, "CAPABILITIES");
        var capBody = await caps;
        Assert.Contains("SPEEDTEST", capBody);

        var help = duplex.ReadMultilineAsync();
        await DispatchAsync(session, duplex, "HELP");
        var helpBody = await help;
        Assert.Contains("SPEEDTEST <peer>", helpBody);
    }

    [Fact]
    public async Task MissingCoordinator_Returns503()
    {
        await using var duplex = await SpeedTestDuplex.CreateAsync();
        var session = duplex.CreateAuthorizedSession(includeCoordinator: false);
        await DispatchAsync(session, duplex, "SPEEDTEST GIGANEWS");
        Assert.Equal("503 SPEEDTEST NOT SUPPORTED", await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task SuccessfulTx_ReportsMeasuredFields_AndDoesNotTouchHistoryOrIngestion()
    {
        await using var duplex = await SpeedTestDuplex.CreateAsync();
        var history = new CountingHistoryDb();
        var queue = new CountingIngestionQueue();
        var session = duplex.CreateAuthorizedSession(historyDb: history, ingestion: queue);

        var read = duplex.ReadSpeedTestAsync();
        await DispatchAsync(session, duplex, "SPEEDTEST GIGANEWS");
        var result = await read;

        Assert.Equal("290 SPEEDTEST GIGANEWS TX", result.Ready);
        Assert.Equal(4096, result.PayloadBytes);
        Assert.True(result.PayloadBytes % SpeedTestPayload.LineBytes == 0);
        Assert.Equal('#', (char)result.Payload[0]);
        Assert.Equal("291 SPEEDTEST COMPLETE", result.Complete);
        Assert.Contains("PEER=GIGANEWS", result.Fields);
        Assert.Contains("PEERNAME=Giganews, Inc.", result.Fields);
        Assert.Contains("IMPLEMENTATION=VectorNNTP", result.Fields);
        Assert.Contains("DIRECTION=TX", result.Fields);
        Assert.Contains("BYTES=4096", result.Fields);
        Assert.Contains("RTT_US=NOT-MEASURED", result.Fields);
        Assert.Contains("RX=NOT-MEASURED", result.Fields);
        Assert.Contains("OUTBOUND=NOT-AVAILABLE", result.Fields);
        Assert.Contains("COMPLETE", result.Fields);
        Assert.Contains("PATH=DIRECT-PIPE", result.Fields);
        Assert.Contains(result.Fields, static f => f.StartsWith("DIRECT_PIPE_FLUSHES=", StringComparison.Ordinal));
        Assert.DoesNotContain("THROUGHPUT_MBPS=NOT-MEASURED", result.Fields);
        Assert.DoesNotContain("THROUGHPUT_GBIT=NOT-MEASURED", result.Fields);
        Assert.Equal(0, history.Calls);
        Assert.Equal(0, queue.Calls);
        Assert.Equal(0, duplex.Coordinator.GlobalInFlight);
    }

    [Fact]
    public async Task ConcurrencyLimit_Returns400_ThenSucceedsAfterRelease()
    {
        await using var duplex = await SpeedTestDuplex.CreateAsync();
        Assert.True(duplex.Coordinator.TryAcquire("GIGANEWS", out var held));
        var session = duplex.CreateAuthorizedSession();
        await DispatchAsync(session, duplex, "SPEEDTEST GIGANEWS");
        Assert.Equal("400 SPEEDTEST BUSY", await duplex.ReadClientLineAsync());

        held!.Dispose();
        var read = duplex.ReadSpeedTestAsync();
        await DispatchAsync(session, duplex, "SPEEDTEST GIGANEWS");
        var result = await read;
        Assert.StartsWith("290 SPEEDTEST GIGANEWS TX", result.Ready, StringComparison.Ordinal);
        Assert.Equal(0, duplex.Coordinator.GlobalInFlight);
    }

    [Fact]
    public async Task Cancellation_DoesNotReportComplete_AndReleasesLease()
    {
        await using var duplex = await SpeedTestDuplex.CreateAsync(maxBytes: SpeedTestPayload.ChunkBytes * 8);
        var session = duplex.CreateAuthorizedSession();
        using var cts = new CancellationTokenSource();

        var dispatch = NntpCommandTestParse.DispatchAsync(
            new NntpCommandDispatcher(),
            session,
            new NntpResponseWriter(duplex.ServerOutput),
            "SPEEDTEST GIGANEWS",
            cts.Token).AsTask();

        var ready = await duplex.ReadClientLineAsync();
        Assert.Equal("290 SPEEDTEST GIGANEWS TX", ready);
        cts.Cancel();

        await dispatch;
        Assert.Equal(0, duplex.Coordinator.GlobalInFlight);
        Assert.False(await duplex.TryReadCompleteAsync());
    }

    [Fact]
    public async Task WritePayloadAsync_StopsOnDurationBeforeByteCeiling()
    {
        var pipe = new Pipe(NntpPipeOptions.Create());
        await using var writer = new NntpResponseWriter(pipe.Writer);
        var drain = Task.Run(async () =>
        {
            while (true)
            {
                var result = await pipe.Reader.ReadAsync().ConfigureAwait(false);
                pipe.Reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
        });

        var limits = new SpeedTestLimits(TimeSpan.FromMilliseconds(20), 1_000_000_000, 1, 1);
        var beforeEnqueue = writer.ChannelEnqueueCount;
        var (bytes, elapsed, flushes) = await VectorNNTPD.TransitService.Session.Commands.SpeedTest
            .WritePayloadAsync(writer, limits, CancellationToken.None);
        Assert.Equal(beforeEnqueue, writer.ChannelEnqueueCount);
        Assert.True(flushes > 0);
        Assert.False(writer.DirectPipeExclusive);
        await pipe.Writer.CompleteAsync();
        await drain;

        Assert.True(bytes < 1_000_000_000);
        Assert.True(elapsed < TimeSpan.FromSeconds(1));
        Assert.True(elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public void FormatResult_UsesNotMeasured_WhenDurationOrBytesMissing()
    {
        var text = Encoding.UTF8.GetString(
            VectorNNTPD.TransitService.Session.Commands.SpeedTest.FormatResult("GIGANEWS", "Giganews, Inc.", 0, TimeSpan.FromSeconds(1)));
        Assert.Contains("PEER=GIGANEWS", text, StringComparison.Ordinal);
        Assert.Contains("PEERNAME=Giganews, Inc.", text, StringComparison.Ordinal);
        Assert.Contains("THROUGHPUT_MBPS=NOT-MEASURED", text, StringComparison.Ordinal);
        Assert.Contains("THROUGHPUT_GBIT=NOT-MEASURED", text, StringComparison.Ordinal);
        Assert.Contains("BYTES=0", text, StringComparison.Ordinal);

        var measured = Encoding.UTF8.GetString(
            VectorNNTPD.TransitService.Session.Commands.SpeedTest.FormatResult("GIGANEWS", "Giganews, Inc.", 1_000_000, TimeSpan.FromSeconds(1)));
        Assert.Contains("THROUGHPUT_MBPS=8.000", measured, StringComparison.Ordinal);
        Assert.Contains("THROUGHPUT_GBIT=0.008000", measured, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatResult_ThroughputFormula_IsBitsOverSeconds()
    {
        var text = Encoding.UTF8.GetString(
            VectorNNTPD.TransitService.Session.Commands.SpeedTest.FormatResult("GIGANEWS", "Giganews, Inc.", 125_000_000, TimeSpan.FromSeconds(1)));
        Assert.Contains("THROUGHPUT_MBPS=1000.000", text, StringComparison.Ordinal);
        Assert.Contains("THROUGHPUT_GBIT=1.000000", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectPayload_UsesExistingSessionOutput_AndDoesNotEnqueueChunks()
    {
        await using var duplex = await SpeedTestDuplex.CreateAsync();
        var writer = new NntpResponseWriter(duplex.ServerOutput);
        var session = duplex.CreateAuthorizedSession();
        Assert.Same(duplex.ServerOutput, session.Connection.Output);
        Assert.Same(session.Connection.Output, writer.UnderlyingOutput);

        var before = writer.ChannelEnqueueCount;
        var drain = duplex.ReadClientLineAsync();
        await using (var lease = await writer.AcquireDirectTxPipeAsync())
        {
            Assert.True(writer.DirectPipeExclusive);
            Assert.Same(session.Connection.Output, lease.PipeWriter);
            var slice = SpeedTestPayload.Take(SpeedTestPayload.LineBytes);
            await lease.WriteAndFlushAsync(slice);
            Assert.Equal(before, writer.ChannelEnqueueCount);
            Assert.True(lease.FlushCount > 0);
        }

        Assert.StartsWith("#", await drain, StringComparison.Ordinal);
        Assert.False(writer.DirectPipeExclusive);
    }

    [Fact]
    public async Task NormalWriteLine_StillUsesResponseWriterChannel()
    {
        var pipe = new Pipe(NntpPipeOptions.Create());
        await using var writer = new NntpResponseWriter(pipe.Writer);
        var drain = Task.Run(async () =>
        {
            while (true)
            {
                var result = await pipe.Reader.ReadAsync().ConfigureAwait(false);
                pipe.Reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
        });
        var before = writer.ChannelEnqueueCount;
        await writer.WriteLineAsync(200, "Hello");
        Assert.Equal(before + 1, writer.ChannelEnqueueCount);
        Assert.False(writer.DirectPipeExclusive);
        Assert.Equal(0, writer.DirectPipeFlushCount);
        await pipe.Writer.CompleteAsync();
        await drain;
    }

    [Fact]
    public async Task ExclusiveLease_RejectsConcurrentChannelWrite_AndSecondAcquire()
    {
        var pipe = new Pipe(NntpPipeOptions.Create());
        await using var writer = new NntpResponseWriter(pipe.Writer);
        var drain = Task.Run(async () =>
        {
            while (true)
            {
                var result = await pipe.Reader.ReadAsync().ConfigureAwait(false);
                pipe.Reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
        });
        await using var lease = await writer.AcquireDirectTxPipeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteLineAsync(200, "nope").AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.AcquireDirectTxPipeAsync());
        await lease.DisposeAsync();
        await writer.WriteLineAsync(200, "ok");
        await pipe.Writer.CompleteAsync();
        await drain;
    }

    [Fact]
    public async Task DirectCommand_ChannelEnqueueIsStatusOnly_SamePipe()
    {
        await using var duplex = await SpeedTestDuplex.CreateAsync(maxBytes: SpeedTestPayload.ChunkBytes);
        var writer = new NntpResponseWriter(duplex.ServerOutput);
        var session = duplex.CreateAuthorizedSession();
        var before = writer.ChannelEnqueueCount;
        var read = duplex.ReadSpeedTestAsync();
        await NntpCommandTestParse.DispatchAsync(
            new NntpCommandDispatcher(),
            session,
            writer,
            "SPEEDTEST GIGANEWS");
        var result = await read;
        Assert.Equal("290 SPEEDTEST GIGANEWS TX", result.Ready);
        Assert.Equal(SpeedTestPayload.ChunkBytes, result.PayloadBytes);
        Assert.Equal("291 SPEEDTEST COMPLETE", result.Complete);
        Assert.Contains("PATH=DIRECT-PIPE", result.Fields);
        Assert.Equal(before + 2, writer.ChannelEnqueueCount);
        Assert.True(writer.DirectPipeFlushCount > 0);
        Assert.False(writer.DirectPipeExclusive);
        Assert.Same(session.Connection.Output, writer.UnderlyingOutput);
    }

    private static ValueTask DispatchAsync(NntpSession session, SpeedTestDuplex duplex, string command) =>
        NntpCommandTestParse.DispatchAsync(new NntpCommandDispatcher(), session, new NntpResponseWriter(duplex.ServerOutput), command);

    private sealed class SpeedTestDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;
        public TransitConfigurationStore Store { get; } = new();
        public SpeedTestCoordinator Coordinator { get; private set; } = null!;

        public static Task<SpeedTestDuplex> CreateAsync(long maxBytes = 4096, int maxDurationSeconds = 10)
        {
            var duplex = new SpeedTestDuplex();
            duplex.Store.Replace(
                TransitTestPeers.Snapshot(
                    "GIGANEWS",
                    TransitTestPeers.Peer(allowFrom: ["127.0.0.1"], peerName: "Giganews, Inc.")));
            duplex.Coordinator = SpeedTestCoordinator.Create(
                duplex.Store,
                new NntpdOptions
                {
                    SpeedTest = new SpeedTestOptions
                    {
                        MaxBytes = maxBytes,
                        MaxDurationSeconds = maxDurationSeconds,
                        MaxConcurrent = 2,
                        MaxConcurrentPerPeer = 1,
                    },
                });
            return Task.FromResult(duplex);
        }

        public NntpSession CreateSession(
            ISpeedTestCoordinator? coordinator = null,
            IHistoryDb? historyDb = null,
            IArticleIngestionQueue? ingestion = null) =>
            new(
                new PipeNntpConnection(
                    _clientToServer.Reader,
                    _serverToClient.Writer,
                    ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119))),
                NullLogger<NntpSession>.Instance,
                historyDb: historyDb,
                articleIngestion: ingestion,
                speedTest: coordinator);

        public NntpSession CreateAuthorizedSession(
            string peerName = "GIGANEWS",
            bool includeCoordinator = true,
            IHistoryDb? historyDb = null,
            IArticleIngestionQueue? ingestion = null)
        {
            var session = CreateSession(
                includeCoordinator ? Coordinator : null,
                historyDb,
                ingestion);
            Assert.True(Coordinator.TryResolvePeer(Encoding.ASCII.GetBytes(peerName), out var peer));
            session.SetAuthorization(NntpAuthorization.ForTransitPeer(peer!));
            return session;
        }

        public async Task<List<string>> ReadMultilineAsync()
        {
            var status = await ReadClientLineAsync().ConfigureAwait(false);
            var lines = new List<string> { status };
            while (true)
            {
                var line = await ReadClientLineAsync().ConfigureAwait(false);
                if (line == ".")
                {
                    break;
                }

                lines.Add(line);
            }

            return lines;
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
            Assert.NotNull(line);
            return line!;
        }

        public async Task<bool> TryReadCompleteAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            try
            {
                while (true)
                {
                    var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
                    if (line is null)
                    {
                        return false;
                    }

                    if (line.StartsWith("291 ", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public async Task<SpeedTestRead> ReadSpeedTestAsync()
        {
            var ready = await ReadClientLineAsync().ConfigureAwait(false);
            using var payload = new MemoryStream();
            while (true)
            {
                var line = await ReadClientLineAsync().ConfigureAwait(false);
                if (line == ".")
                {
                    break;
                }

                var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
                payload.Write(bytes);
            }

            var complete = await ReadClientLineAsync().ConfigureAwait(false);
            var fields = new List<string>();
            while (true)
            {
                var line = await ReadClientLineAsync().ConfigureAwait(false);
                if (line == ".")
                {
                    break;
                }

                fields.Add(line);
            }

            return new SpeedTestRead(ready, payload.ToArray(), complete, fields);
        }

        public async ValueTask DisposeAsync()
        {
            await _clientToServer.Writer.CompleteAsync();
            await _clientToServer.Reader.CompleteAsync();
            await _serverToClient.Writer.CompleteAsync();
            await _serverToClient.Reader.CompleteAsync();
        }
    }

    private sealed record SpeedTestRead(
        string Ready,
        byte[] Payload,
        string Complete,
        List<string> Fields)
    {
        public int PayloadBytes => Payload.Length;
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
        public EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
        public EndPoint? LocalEndPoint => null;
        public ConnectionClientIdentity ClientIdentity { get; }
        public bool IsTls => false;
        public bool IsCompressed => false;
        public CancellationToken ConnectionClosed => _cts.Token;
        public bool IsCompleted => _cts.IsCancellationRequested;
        public long OutboundIdleVersion => 0;

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
        {
            tlsVersion = string.Empty;
            cipher = string.Empty;
            return false;
        }

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

    private sealed class CountingHistoryDb : IHistoryDb
    {
        public int Calls;

        public ValueTask<HistoryLookupResult> LookupAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return new ValueTask<HistoryLookupResult>(HistoryLookupResult.Unseen);
        }

        public ValueTask<HistoryLookupResult> PeekAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return new ValueTask<HistoryLookupResult>(HistoryLookupResult.Unseen);
        }

        public void Remember(ReadOnlyMemory<byte> messageId) => Interlocked.Increment(ref Calls);

        public bool ContainsLocal(in HistoryDigest digest)
        {
            Interlocked.Increment(ref Calls);
            return false;
        }
    }

    private sealed class CountingIngestionQueue : IArticleIngestionQueue
    {
        public int Calls;
        public long MemoryLimitBytes => 1;
        public long QueuedBytes => 0;
        public long PeakQueuedBytes => 0;
        public int MaxArticleBytes => 1024;
        public int Count => 0;
        public int PeakCount => 0;
        public bool IsAccepting => true;

        public ValueTask<ArticleEnqueueResult> EnqueueAsync(
            InboundArticle article,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return new ValueTask<ArticleEnqueueResult>(ArticleEnqueueResult.Accepted);
        }

        public bool TryProbeCapacity()
        {
            Interlocked.Increment(ref Calls);
            return true;
        }

        public ArticleEnqueueResult TryAdmit(InboundArticle article)
        {
            Interlocked.Increment(ref Calls);
            return ArticleEnqueueResult.Accepted;
        }

        public bool TryEnqueue(InboundArticle article)
        {
            Interlocked.Increment(ref Calls);
            return true;
        }

        public void Complete() => Interlocked.Increment(ref Calls);

        public ValueTask<InboundArticle?> DequeueAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult<InboundArticle?>(null);
        }
    }
}
