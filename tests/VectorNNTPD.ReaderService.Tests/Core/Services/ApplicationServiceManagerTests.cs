using System.Diagnostics;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.ReaderService.Tests.Core.Services;

public sealed class ApplicationServiceManagerTests
{
    [Fact]
    public async Task StartAsync_StartsServicesInRegistrationOrder()
    {
        var order = new List<string>();
        var a = new FakeApplicationService("a") { SharedStartOrder = order };
        var b = new FakeApplicationService("b") { SharedStartOrder = order };
        var c = new FakeApplicationService("c") { SharedStartOrder = order };
        var manager = TestHostFactory.CreateServiceManager([a, b, c]);

        await manager.StartAsync(CancellationToken.None);

        Assert.Equal(["a", "b", "c"], order);
        Assert.Equal(3, manager.StartedServices.Count);
    }

    [Fact]
    public async Task StopAsync_StopsServicesInReverseOrder()
    {
        var startOrder = new List<string>();
        var stopOrder = new List<string>();
        var a = new FakeApplicationService("a") { SharedStartOrder = startOrder, SharedStopOrder = stopOrder };
        var b = new FakeApplicationService("b") { SharedStartOrder = startOrder, SharedStopOrder = stopOrder };
        var c = new FakeApplicationService("c") { SharedStartOrder = startOrder, SharedStopOrder = stopOrder };
        var manager = TestHostFactory.CreateServiceManager([a, b, c]);

        await manager.StartAsync(CancellationToken.None);
        await manager.StopAsync(CancellationToken.None);

        Assert.Equal(["a", "b", "c"], startOrder);
        Assert.Equal(["c", "b", "a"], stopOrder);
        Assert.Empty(manager.StartedServices);
    }

    [Fact]
    public async Task StartupFailure_RollsBackStartedServices()
    {
        var stopOrder = new List<string>();
        var a = new FakeApplicationService("a") { SharedStopOrder = stopOrder };
        var b = new FakeApplicationService(
            "b",
            onStart: _ => throw new InvalidOperationException("fail-b"))
        {
            SharedStopOrder = stopOrder,
        };
        var c = new FakeApplicationService("c") { SharedStopOrder = stopOrder };
        var manager = TestHostFactory.CreateServiceManager([a, b, c]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.StartAsync(CancellationToken.None));

        Assert.Equal("fail-b", ex.Message);
        Assert.Equal(1, a.StartCount);
        Assert.Equal(1, a.StopCount);
        Assert.Equal(0, b.StartCount);
        Assert.Equal(0, c.StartCount);
        Assert.Equal(["a"], stopOrder);
        Assert.Empty(manager.StartedServices);
    }

    [Fact]
    public async Task ShutdownFailure_IsAggregatedAndSurfaced()
    {
        var a = new FakeApplicationService(
            "a",
            onStop: _ => throw new InvalidOperationException("stop-a"));
        var b = new FakeApplicationService(
            "b",
            onStop: _ => throw new InvalidOperationException("stop-b"));
        var manager = TestHostFactory.CreateServiceManager([a, b]);

        await manager.StartAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => manager.StopAsync(CancellationToken.None));

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Contains(ex.InnerExceptions, e => e.Message == "stop-b");
        Assert.Contains(ex.InnerExceptions, e => e.Message == "stop-a");
    }

    [Fact]
    public async Task CancellationDuringStartup_RollsBackAndThrows()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new FakeApplicationService("a");
        var b = new FakeApplicationService(
            "b",
            onStart: async ct =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            });
        var manager = TestHostFactory.CreateServiceManager([a, b]);
        using var cts = new CancellationTokenSource();

        var start = manager.StartAsync(cts.Token);
        await entered.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(1, a.StartCount);
        Assert.Equal(1, a.StopCount);
        Assert.Empty(manager.StartedServices);
    }

    [Fact]
    public async Task CancellationDuringShutdown_Throws()
    {
        var enteredStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new FakeApplicationService("a");
        var b = new FakeApplicationService(
            "b",
            onStop: async ct =>
            {
                enteredStop.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            });
        var manager = TestHostFactory.CreateServiceManager([a, b]);

        await manager.StartAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var stop = manager.StopAsync(cts.Token);
        await enteredStop.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);

        // Reverse order: b canceled first; a must still receive a best-effort abort and leave tracking.
        Assert.Empty(manager.StartedServices);
        Assert.Equal(1, b.StopAttemptCount);
        Assert.Equal(1, a.StopAttemptCount);
    }

    [Fact]
    public async Task GracefulShutdownTimeout_ThrowsTimeoutException()
    {
        var service = new FakeApplicationService("slow")
        {
            HoldStopUntilReleased = true,
        };
        var options = TestHostFactory.CreateOptions(gracefulShutdownTimeout: TimeSpan.FromMilliseconds(50));
        var manager = TestHostFactory.CreateServiceManager([service], options);

        await manager.StartAsync(CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(() => manager.StopAsync(CancellationToken.None));
        Assert.Empty(manager.StartedServices);
    }

    [Fact]
    public async Task GracefulShutdownTimeout_ClearsTracking_AndStillAttemptsEarlierServices()
    {
        // Reverse stop order: slow (started second) consumes the overall budget first; cooperative
        // (started first) must still be offered a stop attempt and leave StartedServices empty.
        var cooperative = new FakeApplicationService("cooperative");
        var slow = new FakeApplicationService("slow")
        {
            HoldStopUntilReleased = true,
        };
        var options = TestHostFactory.CreateOptions(gracefulShutdownTimeout: TimeSpan.FromMilliseconds(50));
        var manager = TestHostFactory.CreateServiceManager([cooperative, slow], options);

        await manager.StartAsync(CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(() => manager.StopAsync(CancellationToken.None));

        Assert.Empty(manager.StartedServices);
        Assert.Equal(1, slow.StopAttemptCount);
        Assert.Equal(1, cooperative.StopAttemptCount);
    }

    [Fact]
    public async Task GracefulShutdownTimeout_IsOverallBudget_NotPerService()
    {
        // Two non-cooperative stops must not receive a refreshed full timeout each.
        var first = new FakeApplicationService("first") { HoldStopUntilReleased = true };
        var second = new FakeApplicationService("second") { HoldStopUntilReleased = true };
        var timeout = TimeSpan.FromMilliseconds(80);
        var options = TestHostFactory.CreateOptions(gracefulShutdownTimeout: timeout);
        var manager = TestHostFactory.CreateServiceManager([first, second], options);

        await manager.StartAsync(CancellationToken.None);

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => manager.StopAsync(CancellationToken.None));
        sw.Stop();

        Assert.Empty(manager.StartedServices);
        // Per-service refresh would take ~2× timeout; overall budget should stay near one window.
        Assert.True(
            sw.Elapsed < timeout + TimeSpan.FromMilliseconds(250),
            $"Shutdown elapsed {sw.Elapsed} exceeded overall budget expectation for timeout {timeout}.");
        Assert.Equal(1, second.StopAttemptCount);
        Assert.Equal(1, first.StopAttemptCount);
    }

    [Fact]
    public async Task ShutdownException_RemovesServiceFromTracking()
    {
        var a = new FakeApplicationService(
            "a",
            onStop: _ => throw new InvalidOperationException("stop-a"));
        var manager = TestHostFactory.CreateServiceManager([a]);

        await manager.StartAsync(CancellationToken.None);

        await Assert.ThrowsAsync<AggregateException>(() => manager.StopAsync(CancellationToken.None));
        Assert.Empty(manager.StartedServices);
    }

    [Fact]
    public async Task DuplicateStart_ThrowsInvalidOperation()
    {
        var manager = TestHostFactory.CreateServiceManager([new FakeApplicationService("a")]);

        await manager.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentLifecycleOperations_AreRejected()
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
        var manager = TestHostFactory.CreateServiceManager([service]);

        var start = manager.StartAsync(CancellationToken.None);
        await entered.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.StopAsync(CancellationToken.None));

        release.TrySetResult();
        await start;
    }

    [Fact]
    public async Task UnexpectedExecutionFault_RaisesEvent()
    {
        var service = new FakeApplicationService("a", withExecution: true);
        var manager = TestHostFactory.CreateServiceManager([service]);
        var observed = new TaskCompletionSource<UnexpectedServiceTerminationEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        manager.UnexpectedServiceTermination += (_, args) => observed.TrySetResult(args);

        await manager.StartAsync(CancellationToken.None);
        service.CompleteExecution(new InvalidOperationException("crash"));

        var args = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("a", args.ServiceName);
        Assert.False(args.CompletedNormally);
        Assert.IsType<InvalidOperationException>(args.Exception);
    }

    [Fact]
    public async Task ConcurrentStopAsync_IsRejected()
    {
        var service = new FakeApplicationService("a")
        {
            HoldStopUntilReleased = true,
        };
        var manager = TestHostFactory.CreateServiceManager(
            [service],
            TestHostFactory.CreateOptions(gracefulShutdownTimeout: TimeSpan.FromMinutes(1)));

        await manager.StartAsync(CancellationToken.None);

        var stop1 = manager.StopAsync(CancellationToken.None);
        await service.StopEntered.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.StopAsync(CancellationToken.None));

        service.StopGate.TrySetResult();
        await stop1;

        Assert.Empty(manager.StartedServices);
        Assert.Equal(1, service.StopAttemptCount);
        Assert.Equal(1, service.StopCount);
    }

    [Fact]
    public async Task GracefulShutdownTimeout_NonCooperativeStop_KeepsAwaitingUntilReturn_ThenTimesOut()
    {
        var service = new FakeApplicationService("stubborn")
        {
            HoldStopUntilReleased = true,
            IgnoreStopCancellation = true,
        };
        var timeout = TimeSpan.FromMilliseconds(80);
        var manager = TestHostFactory.CreateServiceManager(
            [service],
            TestHostFactory.CreateOptions(gracefulShutdownTimeout: timeout));

        await manager.StartAsync(CancellationToken.None);

        var stop = manager.StopAsync(CancellationToken.None);
        await service.StopEntered.Task;

        // Manager must not abandon the in-flight StopAsync when the budget elapses.
        await Assert.ThrowsAsync<TimeoutException>(() => stop.WaitAsync(timeout + TimeSpan.FromMilliseconds(200)));
        Assert.Single(manager.StartedServices);

        service.StopGate.TrySetResult();

        await Assert.ThrowsAsync<TimeoutException>(() => stop);
        Assert.Empty(manager.StartedServices);
        Assert.Equal(1, service.StopAttemptCount);
        Assert.Equal(1, service.StopCount);

        // Repeated stop must not re-invoke a service already removed from tracking.
        await manager.StopAsync(CancellationToken.None);
        Assert.Equal(1, service.StopAttemptCount);
    }

    [Fact]
    public async Task GracefulShutdownTimeout_DoesNotDoubleStop_AfterTimeoutClearsTracking()
    {
        var service = new FakeApplicationService("slow")
        {
            HoldStopUntilReleased = true,
        };
        var manager = TestHostFactory.CreateServiceManager(
            [service],
            TestHostFactory.CreateOptions(gracefulShutdownTimeout: TimeSpan.FromMilliseconds(50)));

        await manager.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<TimeoutException>(() => manager.StopAsync(CancellationToken.None));
        Assert.Empty(manager.StartedServices);
        Assert.Equal(1, service.StopAttemptCount);

        await manager.StopAsync(CancellationToken.None);
        Assert.Equal(1, service.StopAttemptCount);
    }

    [Fact]
    public async Task ServicesAreNotStartedTwice_AfterSuccessfulStartAndStop()
    {
        var service = new FakeApplicationService("a");
        var manager = TestHostFactory.CreateServiceManager([service]);

        await manager.StartAsync(CancellationToken.None);
        await manager.StopAsync(CancellationToken.None);

        // After full stop, started list is empty but duplicate start is blocked by design
        // until we allow restart. Phase 0 treats restart as unsupported on the same manager
        // only when started list is non-empty; empty allows start again.
        await manager.StartAsync(CancellationToken.None);
        await manager.StopAsync(CancellationToken.None);

        Assert.Equal(2, service.StartCount);
        Assert.Equal(2, service.StopCount);
    }
}
