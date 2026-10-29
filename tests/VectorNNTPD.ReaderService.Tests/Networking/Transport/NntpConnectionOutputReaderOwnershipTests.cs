using VectorNNTPD.ReaderService.Networking.Transport;

namespace VectorNNTPD.ReaderService.Tests.Networking.Transport;

/// <summary>
/// Deterministic <c>Output.Reader</c> ownership: <c>SendAsync</c> examines/advances/completes;
/// <c>CompleteAsync</c> must not complete the reader while the send pump holds a <c>ReadAsync</c> result.
/// </summary>
public sealed class NntpConnectionOutputReaderOwnershipTests
{
    [Fact]
    public async Task CompleteAsync_WhileSendBlockedInWrite_DoesNotCompleteReaderBeforeAdvanceTo()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());

        var enteredWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = server.ByteTransportForTests;
        Assert.NotNull(transport);
        transport.BeforeStreamWriteProbe = async () =>
        {
            enteredWrite.TrySetResult();
            await releaseWrite.Task.ConfigureAwait(false);
        };

        await server.Output.WriteAsync(new byte[] { 0x01, 0x02, 0x03 });
        await server.Output.FlushAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await enteredWrite.Task.WaitAsync(timeout.Token);

        var complete = server.CompleteAsync();
        await WaitUntilAsync(static s => s.OutputWriterCompletedForTests, server, timeout.Token);

        Assert.True(server.OutputWriterCompletedForTests);
        Assert.False(complete.IsCompleted);
        Assert.False(server.SendPumpTaskForTests!.IsCompleted);
        Assert.False(server.OutputReaderCompletedForTests);

        releaseWrite.TrySetResult();
        await complete.WaitAsync(timeout.Token);

        AssertNoAdvanceToAfterReaderCompleted(server.SendPumpTaskForTests);
        Assert.True(server.OutputReaderCompletedForTests);
        Assert.True(server.ConnectionClosed.IsCancellationRequested);
    }

    [Fact]
    public async Task CompleteAsync_WhileSendIdleInReadAsync_CompletesCleanly()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(static s => s.SendPumpAwaitingOutputForTests == 1, server, timeout.Token);

        await server.CompleteAsync().WaitAsync(timeout.Token);

        AssertNoAdvanceToAfterReaderCompleted(server.SendPumpTaskForTests);
        Assert.True(server.OutputReaderCompletedForTests);
        Assert.True(server.ConnectionClosed.IsCancellationRequested);
        Assert.True(server.IsCompleted);
    }

    [Fact]
    public async Task ConcurrentCompleteAsync_IsSingleFlight_AndDoesNotRaceOutputReader()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());

        var enteredWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = server.ByteTransportForTests;
        Assert.NotNull(transport);
        transport.BeforeStreamWriteProbe = async () =>
        {
            enteredWrite.TrySetResult();
            await releaseWrite.Task.ConfigureAwait(false);
        };

        await server.Output.WriteAsync(new byte[] { 0x11 });
        await server.Output.FlushAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await enteredWrite.Task.WaitAsync(timeout.Token);

        var first = server.CompleteAsync();
        await WaitUntilAsync(static s => s.IsCompleted, server, timeout.Token);

        var second = server.CompleteAsync();
        await second.WaitAsync(timeout.Token);
        Assert.False(first.IsCompleted);

        releaseWrite.TrySetResult();
        await first.WaitAsync(timeout.Token);

        AssertNoAdvanceToAfterReaderCompleted(server.SendPumpTaskForTests);
        Assert.True(server.OutputReaderCompletedForTests);
    }

    private static void AssertNoAdvanceToAfterReaderCompleted(Task? send)
    {
        Assert.NotNull(send);
        if (!send.IsFaulted)
        {
            return;
        }

        foreach (var ex in send.Exception!.Flatten().InnerExceptions)
        {
            Assert.False(
                ex is InvalidOperationException
                && ex.Message.Contains("Reading is not allowed after reader was completed", StringComparison.Ordinal),
                "Send pump must not fault because Output.Reader was completed before AdvanceTo.");
        }
    }

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
