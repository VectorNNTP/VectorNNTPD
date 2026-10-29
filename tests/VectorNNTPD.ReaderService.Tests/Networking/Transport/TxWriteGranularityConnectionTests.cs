using System.IO.Pipelines;
using System.Net.Sockets;
using VectorNNTPD.Common.Networking.Transport;

namespace VectorNNTPD.ReaderService.Tests.Networking.Transport;

/// <summary>
/// Live <see cref="NntpConnection.SendAsync"/> contracts for diagnostic write coalescing.
/// Uses per-transport <see cref="TxWriteGranularityExperiment.SetFor"/> so other tests keep
/// the production one-segment-one-write path.
/// </summary>
public sealed class TxWriteGranularityConnectionTests
{
    [Fact]
    public async Task Disabled_WritesEachSegmentImmediately()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());
        var transport = server.ByteTransportForTests;
        Assert.NotNull(transport);
        Assert.False(TxWriteGranularityExperiment.TryResolveTarget(transport, out _));

        var writes = 0;
        transport.BeforeStreamWriteProbe = () =>
        {
            Interlocked.Increment(ref writes);
            return ValueTask.CompletedTask;
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitIdleAsync(server, timeout.Token);

        var first = Pattern(64 * 1024, 1);
        var second = Pattern(64 * 1024, 2);
        await WriteSegmentAsync(server.Output, first, timeout.Token);
        await WriteSegmentAsync(server.Output, second, timeout.Token);

        var received = await ReadExactAsync(client, first.Length + second.Length, timeout.Token);
        Assert.Equal(first.Concat(second).ToArray(), received);
        Assert.Equal(2, Volatile.Read(ref writes));
    }

    [Fact]
    public async Task Disabled_SmallFlush_HitsTransportImmediately()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());
        var transport = server.ByteTransportForTests;
        Assert.NotNull(transport);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeStreamWriteProbe = () =>
        {
            entered.TrySetResult();
            return ValueTask.CompletedTask;
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitIdleAsync(server, timeout.Token);
        await server.Output.WriteAsync(new byte[] { 0x01, 0x02, 0x03 }, timeout.Token);
        await server.Output.FlushAsync(timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, await ReadExactAsync(client, 3, timeout.Token));
    }

    [Theory]
    [InlineData(64 * 1024, 1)]
    [InlineData(128 * 1024, 2)]
    [InlineData(256 * 1024, 4)]
    [InlineData(512 * 1024, 8)]
    [InlineData(1024 * 1024, 16)]
    [InlineData(4 * 1024 * 1024, 64)]
    public async Task Coalesce_ExactTarget_OneWrite_PreservesBytes(int target, int chunks)
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());
        var transport = server.ByteTransportForTests;
        Assert.NotNull(transport);

        var writes = 0;
        var writeBytes = 0;
        transport.BeforeStreamWriteProbe = () =>
        {
            Interlocked.Increment(ref writes);
            return ValueTask.CompletedTask;
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await WaitIdleAsync(server, timeout.Token);
        TxWriteGranularityExperiment.SetFor(transport, target);
        try
        {
            var expected = new byte[target];
            for (var i = 0; i < chunks; i++)
            {
                var chunk = Pattern(64 * 1024, (byte)(i + 1));
                chunk.CopyTo(expected.AsSpan(i * 64 * 1024));
                var span = server.Output.GetSpan(chunk.Length);
                chunk.CopyTo(span);
                server.Output.Advance(chunk.Length);
                writeBytes += chunk.Length;
            }

            var read = ReadExactAsync(client, target, timeout.Token);
            await server.Output.FlushAsync(timeout.Token);

            var received = await read;
            Assert.Equal(expected, received);
            Assert.Equal(target, writeBytes);
            Assert.Equal(1, Volatile.Read(ref writes));
        }
        finally
        {
            TxWriteGranularityExperiment.ClearFor(transport);
        }
    }

    [Fact]
    public async Task Coalesce_PartialFinal_FlushesWithoutWaitingForMore()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());
        var transport = server.ByteTransportForTests;
        Assert.NotNull(transport);

        var writes = 0;
        transport.BeforeStreamWriteProbe = () =>
        {
            Interlocked.Increment(ref writes);
            return ValueTask.CompletedTask;
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitIdleAsync(server, timeout.Token);
        TxWriteGranularityExperiment.SetFor(transport, 256 * 1024);
        try
        {
            var chunk = Pattern(64 * 1024, 41);
            await WriteSegmentAsync(server.Output, chunk, timeout.Token);
            var received = await ReadExactAsync(client, chunk.Length, timeout.Token);
            Assert.Equal(chunk, received);
            Assert.Equal(1, Volatile.Read(ref writes));
        }
        finally
        {
            TxWriteGranularityExperiment.ClearFor(transport);
        }
    }

    [Fact]
    public async Task Coalesce_TransportFailure_CompletesConnection()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());
        var transport = server.ByteTransportForTests;
        Assert.NotNull(transport);

        transport.BeforeStreamWriteProbe = () => throw new IOException("simulated coalesce write failure");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitIdleAsync(server, timeout.Token);
        TxWriteGranularityExperiment.SetFor(transport, 64 * 1024);
        try
        {
            try
            {
                await WriteSegmentAsync(server.Output, Pattern(64 * 1024, 8), timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
            }

            await WaitUntilAsync(
                static s => s.ConnectionClosed.IsCancellationRequested,
                server,
                timeout.Token);
            Assert.True(server.ConnectionClosed.IsCancellationRequested);
        }
        finally
        {
            TxWriteGranularityExperiment.ClearFor(transport);
        }
    }

    [Fact]
    public async Task Coalesce_Cancellation_DoesNotHang()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());
        var transport = server.ByteTransportForTests;
        Assert.NotNull(transport);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitIdleAsync(server, timeout.Token);
        TxWriteGranularityExperiment.SetFor(transport, 256 * 1024);
        try
        {
            await WriteSegmentAsync(server.Output, Pattern(64 * 1024, 12), timeout.Token);
            await server.CompleteAsync().WaitAsync(timeout.Token);
            Assert.True(server.ConnectionClosed.IsCancellationRequested);
            Assert.True(server.OutputReaderCompletedForTests);
        }
        finally
        {
            TxWriteGranularityExperiment.ClearFor(transport);
        }
    }

    [Fact]
    public async Task Coalesce_PeerDisconnect_TerminatesConnection()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());
        var transport = server.ByteTransportForTests;
        Assert.NotNull(transport);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitIdleAsync(server, timeout.Token);
        TxWriteGranularityExperiment.SetFor(transport, 64 * 1024);
        try
        {
            client.LingerState = new LingerOption(enable: true, seconds: 0);
            client.Dispose();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(server.ConnectionClosed, timeout.Token);
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    await WriteSegmentAsync(server.Output, Pattern(64 * 1024, 15), linked.Token);
                }
            }
            catch (OperationCanceledException ex) when (server.ConnectionClosed.IsCancellationRequested)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
            }
            catch (Exception ex) when (
                server.ConnectionClosed.IsCancellationRequested
                && ex is InvalidOperationException or IOException or SocketException)
            {
                _ = ex; // observed; expected during test teardown/cleanup
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

            Assert.True(server.ConnectionClosed.IsCancellationRequested);
        }
        finally
        {
            TxWriteGranularityExperiment.ClearFor(transport);
        }
    }

    private static async Task WriteSegmentAsync(PipeWriter writer, ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        var span = writer.GetSpan(chunk.Length);
        chunk.Span.CopyTo(span);
        writer.Advance(chunk.Length);
        await writer.FlushAsync(cancellationToken);
    }

    private static byte[] Pattern(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(seed + (i * 31));
        }

        return bytes;
    }

    private static async Task<byte[]> ReadExactAsync(Socket client, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await client.ReceiveAsync(buffer.AsMemory(read), cancellationToken);
            if (n == 0)
            {
                throw new InvalidOperationException($"Peer closed after {read} of {count} bytes.");
            }

            read += n;
        }

        return buffer;
    }

    private static async Task WaitIdleAsync(NntpConnection server, CancellationToken cancellationToken) =>
        await WaitUntilAsync(static s => s.SendPumpAwaitingOutputForTests == 1, server, cancellationToken);

    private static async Task WaitUntilAsync(
        Func<NntpConnection, bool> predicate,
        NntpConnection server,
        CancellationToken cancellationToken)
    {
        while (!predicate(server))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
