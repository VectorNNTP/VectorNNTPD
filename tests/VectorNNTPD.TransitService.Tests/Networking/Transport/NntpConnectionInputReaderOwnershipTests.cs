using System.IO.Pipelines;
using System.Net.Sockets;
using VectorNNTPD.Common.Networking.Transport;

namespace VectorNNTPD.TransitService.Tests.Networking.Transport;

/// <summary>
/// Deterministic <c>Input.Reader</c> ownership: the application consumer examines/advances;
/// <c>CompleteAsync</c> (including ObservePump after receive EOF) must not complete the reader
/// while <c>ReadAsync</c> can still run.
/// </summary>
[Collection(nameof(TransportTestHostCollection))]
public sealed class NntpConnectionInputReaderOwnershipTests
{
    [Fact]
    public async Task CompleteAsync_WhileInputReadAsyncPending_DoesNotThrowReaderCompleted()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());

        var inboundRead = server.Input.ReadAsync().AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var complete = server.CompleteAsync();
        ReadResult result;
        try
        {
            result = await inboundRead.WaitAsync(timeout.Token);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Fail(
                "CompleteAsync completed Input.Reader while ReadAsync was outstanding: " + ex.Message);
            return;
        }

        Assert.True(result.IsCompleted);
        server.Input.AdvanceTo(result.Buffer.End);
        await complete.WaitAsync(timeout.Token);

        Assert.False(server.InputReaderCompletedForTests);
        Assert.True(server.ConnectionClosed.IsCancellationRequested);
        Assert.True(server.IsCompleted);
    }

    [Fact]
    public async Task ReceiveEof_WhileInputReadAsyncPending_DoesNotCompleteReader()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());

        var inboundRead = server.Input.ReadAsync().AsTask();
        client.Shutdown(SocketShutdown.Send);
        client.Dispose();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ReadResult result;
        try
        {
            result = await inboundRead.WaitAsync(timeout.Token);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Fail(
                "Receive EOF completed Input.Reader while ReadAsync was outstanding: " + ex.Message);
            return;
        }

        Assert.True(result.IsCompleted);
        server.Input.AdvanceTo(result.Buffer.End);
        Assert.False(server.InputReaderCompletedForTests);
    }

    [Fact]
    public async Task CompleteInputReader_AfterConsumerStops_IsOnceAndAllowsNoFurtherRead()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());

        server.AttachInputReaderConsumer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.CompleteAsync().WaitAsync(timeout.Token);
        Assert.False(server.InputReaderCompletedForTests);

        await server.CompleteInputReaderAsync();
        Assert.True(server.InputReaderCompletedForTests);
        await server.CompleteInputReaderAsync();
        Assert.True(server.InputReaderCompletedForTests);

        var completed = false;
        try
        {
            _ = await server.Input.ReadAsync(timeout.Token);
        }
        catch (InvalidOperationException ex)
        {
            completed = ex.Message.Contains(
                "Reading is not allowed after reader was completed",
                StringComparison.Ordinal);
        }

        Assert.True(completed);
    }

    [Fact]
    public async Task ConcurrentCompleteAsync_DoesNotCompleteInputReaderWhileConsumerAttached()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());

        server.AttachInputReaderConsumer();
        var inboundRead = server.Input.ReadAsync().AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var first = server.CompleteAsync();
        var second = server.CompleteAsync();
        await second.WaitAsync(timeout.Token);
        await first.WaitAsync(timeout.Token);

        ReadResult result;
        try
        {
            result = await inboundRead.WaitAsync(timeout.Token);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Fail(
                "Concurrent CompleteAsync completed Input.Reader while a consumer was attached: "
                + ex.Message);
            return;
        }

        Assert.True(result.IsCompleted);
        server.Input.AdvanceTo(result.Buffer.End);
        Assert.False(server.InputReaderCompletedForTests);

        await server.CompleteInputReaderAsync();
        Assert.True(server.InputReaderCompletedForTests);
    }
}
