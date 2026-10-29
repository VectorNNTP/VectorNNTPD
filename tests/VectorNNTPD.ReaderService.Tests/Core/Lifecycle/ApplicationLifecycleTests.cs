using VectorNNTPD.Common.Core;

namespace VectorNNTPD.ReaderService.Tests.Core.Lifecycle;

public sealed class ApplicationLifecycleTests
{
    [Fact]
    public async Task StartAsync_TransitionsCreatedToRunning()
    {
        var service = new FakeApplicationService("a");
        await using var lifecycle = TestHostFactory.CreateLifecycle([service]);

        Assert.Equal(ApplicationState.Created, lifecycle.State);

        await lifecycle.StartAsync(CancellationToken.None);

        Assert.Equal(ApplicationState.Running, lifecycle.State);
        Assert.Equal(1, service.StartCount);
    }

    [Fact]
    public async Task StopAsync_TransitionsRunningToStopped()
    {
        var service = new FakeApplicationService("a");
        await using var lifecycle = TestHostFactory.CreateLifecycle([service]);

        await lifecycle.StartAsync(CancellationToken.None);
        await lifecycle.StopAsync(CancellationToken.None);

        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Equal(1, service.StopCount);
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyRunning_ThrowsInvalidOperation()
    {
        var service = new FakeApplicationService("a");
        await using var lifecycle = TestHostFactory.CreateLifecycle([service]);

        await lifecycle.StartAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifecycle.StartAsync(CancellationToken.None));

        Assert.Contains("Invalid application lifecycle transition", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ApplicationState.Running, lifecycle.State);
    }

    [Fact]
    public async Task ConcurrentStart_SecondCallerFailsPredictably()
    {
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var service = new FakeApplicationService(
            "a",
            onStart: async ct =>
            {
                enteredStart.TrySetResult();
                await releaseStart.Task.WaitAsync(ct);
            });

        await using var lifecycle = TestHostFactory.CreateLifecycle([service]);

        var first = lifecycle.StartAsync(CancellationToken.None);
        await enteredStart.Task;

        var second = lifecycle.StartAsync(CancellationToken.None);

        releaseStart.TrySetResult();
        await first;

        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal(ApplicationState.Running, lifecycle.State);
    }

    [Fact]
    public async Task RepeatedStop_IsIdempotent()
    {
        var service = new FakeApplicationService("a");
        await using var lifecycle = TestHostFactory.CreateLifecycle([service]);

        await lifecycle.StartAsync(CancellationToken.None);

        var stop1 = lifecycle.StopAsync(CancellationToken.None);
        var stop2 = lifecycle.StopAsync(CancellationToken.None);
        var stop3 = lifecycle.StopAsync(CancellationToken.None);

        await Task.WhenAll(stop1, stop2, stop3);

        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Equal(1, service.StopCount);
    }

    [Fact]
    public async Task StopAsync_BeforeStart_TransitionsToStopped()
    {
        await using var lifecycle = TestHostFactory.CreateLifecycle([]);

        await lifecycle.StopAsync(CancellationToken.None);

        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
    }

    [Fact]
    public async Task StartupFailure_EndsInStopped_AndRethrows()
    {
        var ok = new FakeApplicationService("ok");
        var bad = new FakeApplicationService(
            "bad",
            onStart: _ => throw new InvalidOperationException("boom"));

        await using var lifecycle = TestHostFactory.CreateLifecycle([ok, bad]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifecycle.StartAsync(CancellationToken.None));

        Assert.Equal("boom", ex.Message);
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Equal(1, ok.StartCount);
        Assert.Equal(1, ok.StopCount);
        Assert.Equal(0, bad.StartCount);
    }

    [Fact]
    public async Task StartupCancellation_PropagatesAndStopsCleanly()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeApplicationService(
            "a",
            onStart: async ct =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            });

        await using var lifecycle = TestHostFactory.CreateLifecycle([service]);
        using var cts = new CancellationTokenSource();

        var start = lifecycle.StartAsync(cts.Token);
        await entered.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
    }

    [Fact]
    public async Task State_IsVisibleAcrossThreads()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeApplicationService(
            "a",
            onStart: async ct =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            });

        await using var lifecycle = TestHostFactory.CreateLifecycle([service]);

        var start = lifecycle.StartAsync(CancellationToken.None);
        await entered.Task;

        var observed = await Task.Run(() => lifecycle.State);
        Assert.Equal(ApplicationState.Starting, observed);

        release.TrySetResult();
        await start;
        Assert.Equal(ApplicationState.Running, lifecycle.State);
    }

    [Fact]
    public async Task UnexpectedServiceTermination_IsObservable()
    {
        var service = new FakeApplicationService("a", withExecution: true);
        await using var lifecycle = TestHostFactory.CreateLifecycle([service]);

        await lifecycle.StartAsync(CancellationToken.None);
        Assert.Equal(ApplicationState.Running, lifecycle.State);

        service.CompleteExecution(new InvalidOperationException("service crashed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifecycle.WaitAsync(CancellationToken.None));
        Assert.True(lifecycle.UnexpectedTermination.IsCompletedSuccessfully);
    }
}
