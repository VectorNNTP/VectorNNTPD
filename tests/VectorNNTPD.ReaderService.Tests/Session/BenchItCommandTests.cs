using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Commands;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Tests.Session;

/// <summary>Internal BENCHIT benchmark facility contracts (not a production protocol feature).</summary>
public sealed class BenchItCommandTests
{
    [Fact]
    public void StaticPayload_IsExactTargetSize_AndWireIsReusable()
    {
        Assert.Equal(750 * 1024, BenchIt.TargetArticleContentBytes);
        Assert.Equal(BenchIt.TargetArticleContentBytes, BenchIt.ArticleContentBytes);
        Assert.True(BenchIt.WireResponseBytes > BenchIt.ArticleContentBytes);
        Assert.True(BenchIt.WireResponse.Span.StartsWith("220 0 <benchit-static@vectornntp.local>\r\n"u8));
        Assert.True(BenchIt.WireResponse.Span.EndsWith("\r\n.\r\n"u8));
        Assert.True(BenchIt.WireResponse.Span.IndexOf("..DOTSTUFF-CHECK\r\n"u8) >= 0);
    }

    [Fact]
    public void Registry_ResolvesBenchIt_AsPublic()
    {
        var parsed = NntpCommandTestParse.ParseCommand("BENCHIT");
        Assert.True(parsed.IsValid);
        Assert.Equal(NntpVerb.BenchIt, parsed.Verb);
        Assert.Equal(NntpCommandAccess.Public, DefaultNntpCommandCatalog.GetAccess(parsed.Verb, parsed.Qualifier));
        Assert.Equal("BENCHIT", DefaultNntpCommandCatalog.DisplayName(parsed.Verb, parsed.Qualifier));
    }

    [Fact]
    public async Task Handle_WritesPrecomputedWire_WithoutAuth()
    {
        await using var duplex = await BenchDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        // Consume concurrently: production pipe pause threshold would otherwise stall a 750 KiB write.
        var readTask = duplex.ReadExactClientBytesAsync(BenchIt.WireResponseBytes);
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "BENCHIT");
        var received = await readTask;

        Assert.Equal(BenchIt.WireResponse.ToArray(), received);
    }

    [Fact]
    public async Task Capabilities_DoesNotAdvertiseBenchIt()
    {
        await using var duplex = await BenchDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        var readTask = duplex.ReadUntilTerminatorAsync();
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "CAPABILITIES");
        var text = Encoding.ASCII.GetString(await readTask);
        Assert.DoesNotContain("BENCHIT", text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class BenchDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public static Task<BenchDuplex> CreateAsync() => Task.FromResult(new BenchDuplex());

        public NntpSession CreateSession()
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)));
            return new NntpSession(connection, NullLogger<NntpSession>.Instance);
        }

        public async Task<byte[]> ReadExactClientBytesAsync(int count)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var buffer = new byte[count];
            var filled = 0;
            while (filled < count)
            {
                var result = await _serverToClient.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
                var readable = result.Buffer;
                if (readable.Length == 0 && result.IsCompleted)
                {
                    throw new EndOfStreamException("Pipe completed before expected BENCHIT wire bytes arrived.");
                }

                var toCopy = (int)Math.Min(readable.Length, count - filled);
                readable.Slice(0, toCopy).CopyTo(buffer.AsSpan(filled));
                filled += toCopy;
                var consumed = readable.GetPosition(toCopy);
                _serverToClient.Reader.AdvanceTo(consumed, readable.End);
            }

            return buffer;
        }

        public async Task<byte[]> ReadUntilTerminatorAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var ms = new System.IO.MemoryStream();
            while (true)
            {
                var result = await _serverToClient.Reader.ReadAsync(cts.Token);
                foreach (var segment in result.Buffer)
                {
                    ms.Write(segment.Span);
                }

                _serverToClient.Reader.AdvanceTo(result.Buffer.End);
                var bytes = ms.GetBuffer().AsSpan(0, (int)ms.Length);
                if (bytes.IndexOf("\r\n.\r\n"u8) >= 0)
                {
                    return ms.ToArray();
                }

                if (result.IsCompleted)
                {
                    return ms.ToArray();
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
