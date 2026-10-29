using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.History;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Tests.Networking.Transport;
using VectorNNTPD.TransitService.Tests.TestDoubles;

namespace VectorNNTPD.TransitService.Tests.Session;

/// <summary>
/// CHECK over the production accept/transport path (<see cref="SocketAcceptListener"/> +
/// <see cref="NntpConnection.StartPlain"/>), not <c>PipelineTestConnection</c>.
/// </summary>
[Collection(nameof(TransportTestHostCollection))]
public sealed class CheckPipelineTcpTests
{
    [Fact]
    public async Task Tcp_PipelinedAndSegmentedChecks_PreserveOrderAndCrlf()
    {
        var redis = new FakeRedisService();
        var history = new HistoryDb(
            redis,
            TimeSpan.FromHours(2),
            NullLogger<HistoryDb>.Instance,
            TimeProvider.System,
            new HistoryWriteQueue());
        _ = await history.LookupAsync("<tcp-b@example.com>"u8.ToArray());
        redis.Database.KeyExistsCount = 0;

        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();
        var session = new NntpSession(
            server,
            NullLogger<NntpSession>.Instance,
            historyDb: history);
        session.SetAuthorization(NntpAuthorization.TrustedTransitPeer);
        var run = session.RunAsync();
        var rx = new SocketLineBuffer();

        var greeting = await rx.ReadLineAsync(client);
        Assert.StartsWith("201 ", greeting, StringComparison.Ordinal);

        await client.SendAsync("CHECK <tcp-a@"u8.ToArray());
        await Task.Yield();
        await client.SendAsync("example.com>\r\nCHECK <tcp-b@example.com>\r\nCHECK <tcp-c@example.com>\r\n"u8.ToArray());

        Assert.Equal("238 <tcp-a@example.com> send article to be transferred", await rx.ReadLineAsync(client));
        Assert.Equal("438 <tcp-b@example.com>", await rx.ReadLineAsync(client));
        Assert.Equal("238 <tcp-c@example.com> send article to be transferred", await rx.ReadLineAsync(client));

        await client.SendAsync("QUIT\r\n"u8.ToArray());
        Assert.Equal("205 Connection closing", await rx.ReadLineAsync(client));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        client.Shutdown(SocketShutdown.Both);
        Assert.Equal(0, session.Pipeline!.Occupied);
        Assert.True(session.Pipeline.PeakOccupied <= CheckPipeline.Depth);
    }

    private sealed class SocketLineBuffer
    {
        private readonly List<byte> _bytes = [];

        public async Task<string> ReadLineAsync(Socket socket)
        {
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                if (TryReadLine(out var line))
                {
                    return line;
                }

                var scratch = new byte[512];
                var n = await socket.ReceiveAsync(scratch, safety.Token);
                Assert.True(n > 0, "Socket closed while reading CHECK response.");
                _bytes.AddRange(scratch.AsSpan(0, n));
            }
        }

        private bool TryReadLine(out string line)
        {
            line = string.Empty;
            for (var i = 0; i + 1 < _bytes.Count; i++)
            {
                if (_bytes[i] != (byte)'\r' || _bytes[i + 1] != (byte)'\n')
                {
                    continue;
                }

                line = Encoding.ASCII.GetString(_bytes.GetRange(0, i).ToArray());
                _bytes.RemoveRange(0, i + 2);
                return true;
            }

            return false;
        }
    }
}
