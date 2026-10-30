using System.IO.Pipelines;
using System.Net.Sockets;
using VectorNNTPD.Common.Networking.Transport;

namespace VectorNNTPD.ReaderService.Tests.Networking.Transport;

/// <summary>
/// Documents outbound pipe advancement: unsent bytes must not be marked consumed while a send
/// remains logically in progress; on terminal abort the connection is completed (no retry).
/// </summary>
public sealed class SendPipeAdvancementTests
{
    [Fact]
    public async Task PlainSendPattern_OnThrowAfterPartialSegments_DoesNotConsumeUnsentBytes()
    {
        var pipe = new Pipe();
        // Two segments in one ReadOnlySequence: force non-contiguous via two writes without flush coalesce... 
        // A single contiguous buffer is fine: treat first half as "sent", then throw before marking End.
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        await pipe.Writer.WriteAsync(payload);
        await pipe.Writer.FlushAsync();
        await pipe.Writer.CompleteAsync();

        var result = await pipe.Reader.ReadAsync();
        var buffer = result.Buffer;
        Assert.Equal(6, buffer.Length);

        // Mirror SendSocketAsync: only advance consumed on full success.
        var consumed = buffer.Start;
        var transmitted = 0;
        var failed = false;
        try
        {
            foreach (var segment in buffer)
            {
                await SocketPayloadSender.SendAllAsync(
                    (memory, _, _) =>
                    {
                        // Transmit first three bytes across possibly multiple calls, then fail.
                        if (transmitted >= 3)
                        {
                            throw new IOException("simulated send failure after partial transmit");
                        }

                        var take = Math.Min(2, Math.Min(memory.Length, 3 - transmitted));
                        transmitted += take;
                        return ValueTask.FromResult(take);
                    },
                    segment,
                    state: null,
                    CancellationToken.None);
            }

            consumed = buffer.End;
        }
        catch (IOException)
        {
            failed = true;
        }
        finally
        {
            pipe.Reader.AdvanceTo(consumed);
        }

        Assert.True(failed);
        Assert.Equal(3, transmitted);
        Assert.True(consumed.Equals(buffer.Start));

        // Unsent octets remain readable (would be discarded only when the connection/pipe is completed).
        var again = await pipe.Reader.ReadAsync();
        Assert.Equal(6, again.Buffer.Length);
        pipe.Reader.AdvanceTo(again.Buffer.Start, again.Buffer.End);
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public async Task PlainSendPattern_OnFullSuccess_ConsumesEntireBuffer()
    {
        var pipe = new Pipe();
        var payload = new byte[] { 0xAA, 0xBB, 0xCC };
        await pipe.Writer.WriteAsync(payload);
        await pipe.Writer.FlushAsync();
        await pipe.Writer.CompleteAsync();

        var result = await pipe.Reader.ReadAsync();
        var buffer = result.Buffer;
        var consumed = buffer.Start;
        try
        {
            foreach (var segment in buffer)
            {
                await SocketPayloadSender.SendAllAsync(
                    static (memory, _, _) => ValueTask.FromResult(memory.Length),
                    segment,
                    state: null,
                    CancellationToken.None);
            }

            consumed = buffer.End;
        }
        finally
        {
            pipe.Reader.AdvanceTo(consumed);
        }

        Assert.True(consumed.Equals(buffer.End));
        var drained = await pipe.Reader.ReadAsync();
        Assert.True(drained.Buffer.IsEmpty);
        Assert.True(drained.IsCompleted);
        pipe.Reader.AdvanceTo(drained.Buffer.End);
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public async Task LiveConnection_SendAfterPeerAbort_TerminatesConnection()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        var client = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        // Abortive close so subsequent server sends fail rather than blocking forever.
        client.LingerState = new LingerOption(enable: true, seconds: 0);
        client.Dispose();

        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            server.ConnectionClosed,
            timeout.Token);

        try
        {
            while (!linked.IsCancellationRequested)
            {
                await server.Output.WriteAsync(payload, linked.Token);
                await server.Output.FlushAsync(linked.Token);
            }
        }
        catch (OperationCanceledException ex) when (server.ConnectionClosed.IsCancellationRequested)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
            // Send pump faulted → ObservePump → CompleteAsync cancelled ConnectionClosed.
        }
        catch (Exception ex) when (
            server.ConnectionClosed.IsCancellationRequested
            && ex is InvalidOperationException or IOException or SocketException)
        {
            // Peer abort completes the connection; Write/Flush may surface several transport exceptions.
        }

        if (!server.ConnectionClosed.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            }
            catch (OperationCanceledException ex) when (server.ConnectionClosed.IsCancellationRequested)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
            }
        }

        Assert.True(
            server.ConnectionClosed.IsCancellationRequested,
            "Send failure must permanently abort the connection (ConnectionClosed).");
    }
}