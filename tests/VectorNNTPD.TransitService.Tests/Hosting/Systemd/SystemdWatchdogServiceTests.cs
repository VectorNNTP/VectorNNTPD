using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Hosting.Systemd;

namespace VectorNNTPD.TransitService.Tests.Hosting.Systemd;

public sealed class SystemdWatchdogServiceTests
{
    [Fact]
    public async Task Watchdog_DisabledWhenNotConfigured()
    {
        var notify = new FakeSystemdNotifyBridge();
        using var service = CreateWatchdog(
            notify,
            runtime: RecordingSystemdRuntimeFactory.LinuxUnderSystemd(watchdog: null),
            health: new FakeApplicationHealth { IsHealthyForWatchdog = true });

        await service.StartAsync(CancellationToken.None);
        Assert.False(service.IsActive);
        Assert.Equal(0, notify.WatchdogCount);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Watchdog_DisabledWhenOptionFalse()
    {
        var notify = new FakeSystemdNotifyBridge();
        using var service = CreateWatchdog(
            notify,
            runtime: RecordingSystemdRuntimeFactory.LinuxUnderSystemd(TimeSpan.FromSeconds(1)),
            health: new FakeApplicationHealth { IsHealthyForWatchdog = true },
            configure: o => o.Systemd.EnableWatchdog = false);

        await service.StartAsync(CancellationToken.None);
        Assert.False(service.IsActive);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Watchdog_DisabledOnNonLinux()
    {
        var notify = new FakeSystemdNotifyBridge();
        using var service = CreateWatchdog(
            notify,
            runtime: RecordingSystemdRuntimeFactory.NonLinux(),
            health: new FakeApplicationHealth { IsHealthyForWatchdog = true });

        await service.StartAsync(CancellationToken.None);
        Assert.False(service.IsActive);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Watchdog_SendsHeartbeatsWhileHealthy_AndStopsOnShutdown()
    {
        var notify = new FakeSystemdNotifyBridge();
        var health = new FakeApplicationHealth { IsHealthyForWatchdog = true };
        var lifetime = new TestHostApplicationLifetime();

        using var service = CreateWatchdog(
            notify,
            runtime: RecordingSystemdRuntimeFactory.LinuxUnderSystemd(TimeSpan.FromMilliseconds(100)),
            health: health,
            hostLifetime: lifetime,
            configure: o => o.Systemd.WatchdogIntervalFraction = 0.5);

        await service.StartAsync(CancellationToken.None);
        Assert.True(service.IsActive);
        Assert.True(service.HeartbeatInterval > TimeSpan.Zero);
        Assert.True(service.HeartbeatInterval < TimeSpan.FromMilliseconds(100));

        await notify.WatchdogSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(notify.WatchdogCount >= 1);

        await service.StopAsync(CancellationToken.None);
        var countAfterStop = notify.WatchdogCount;
        Assert.Equal(countAfterStop, notify.WatchdogCount);
        Assert.False(service.IsActive);
    }

    [Fact]
    public async Task Watchdog_DoesNotReportHealthyDuringStartupOrUnhealthyPeriods()
    {
        var notify = new FakeSystemdNotifyBridge();
        var health = new FakeApplicationHealth { IsHealthyForWatchdog = false };
        using var service = CreateWatchdog(
            notify,
            runtime: RecordingSystemdRuntimeFactory.LinuxUnderSystemd(TimeSpan.FromMilliseconds(80)),
            health: health,
            configure: o => o.Systemd.WatchdogIntervalFraction = 0.5);

        await service.StartAsync(CancellationToken.None);
        Assert.True(service.IsActive);

        var unhealthyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            await Task.Delay(service.HeartbeatInterval + TimeSpan.FromMilliseconds(30));
            unhealthyGate.TrySetResult();
        });
        await unhealthyGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, notify.WatchdogCount);

        health.IsHealthyForWatchdog = true;
        await notify.WatchdogSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(notify.WatchdogCount >= 1);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Watchdog_StopsAfterFatalSupervisedServiceFailure()
    {
        var appService = new FakeApplicationService("fragile", withExecution: true);
        await using var lifecycle = TestHostFactory.CreateLifecycle([appService]);
        await lifecycle.StartAsync(CancellationToken.None);

        var health = new ApplicationHealth(lifecycle);
        Assert.True(health.IsHealthyForWatchdog);

        var notify = new FakeSystemdNotifyBridge();
        using var watchdog = CreateWatchdog(
            notify,
            runtime: RecordingSystemdRuntimeFactory.LinuxUnderSystemd(TimeSpan.FromMilliseconds(80)),
            health: health,
            configure: o => o.Systemd.WatchdogIntervalFraction = 0.5);

        await watchdog.StartAsync(CancellationToken.None);
        await notify.WatchdogSent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        appService.CompleteExecution(new InvalidOperationException("fatal"));
        await lifecycle.UnexpectedTermination.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(health.IsHealthyForWatchdog);

        var count = notify.WatchdogCount;
        var settle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            await Task.Delay(watchdog.HeartbeatInterval + TimeSpan.FromMilliseconds(40));
            settle.TrySetResult();
        });
        await settle.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(count, notify.WatchdogCount);

        await watchdog.StopAsync(CancellationToken.None);
        await lifecycle.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Watchdog_NotificationFailure_IsSurfacedAndStopsHost()
    {
        var notify = new FakeSystemdNotifyBridge
        {
            ThrowOnNotify = new InvalidOperationException("notify-failed"),
        };
        var health = new FakeApplicationHealth { IsHealthyForWatchdog = true };
        var lifetime = new TestHostApplicationLifetime();

        using var service = CreateWatchdog(
            notify,
            runtime: RecordingSystemdRuntimeFactory.LinuxUnderSystemd(TimeSpan.FromMilliseconds(60)),
            health: health,
            hostLifetime: lifetime,
            configure: o => o.Systemd.WatchdogIntervalFraction = 0.5);

        await service.StartAsync(CancellationToken.None);
        await lifetime.StoppingRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(lifetime.StopRequested);

        await service.StopAsync(CancellationToken.None);
        Assert.False(service.IsActive);
    }

    [Fact]
    public async Task ApplicationHealth_TracksLifecycleAndUnexpectedTermination()
    {
        var appService = new FakeApplicationService("svc", withExecution: true);
        await using var lifecycle = TestHostFactory.CreateLifecycle([appService]);
        var health = new ApplicationHealth(lifecycle);

        Assert.False(health.IsHealthyForWatchdog);

        await lifecycle.StartAsync(CancellationToken.None);
        Assert.True(health.IsHealthyForWatchdog);

        appService.CompleteExecution(new InvalidOperationException("fatal"));
        await lifecycle.UnexpectedTermination.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(health.IsHealthyForWatchdog);

        await lifecycle.StopAsync(CancellationToken.None);
        Assert.False(health.IsHealthyForWatchdog);
        Assert.True(lifecycle.ShutdownRequested);
    }

    private static SystemdWatchdogService CreateWatchdog(
        FakeSystemdNotifyBridge notify,
        ISystemdRuntime runtime,
        IApplicationHealth health,
        IHostApplicationLifetime? hostLifetime = null,
        Action<NntpdOptions>? configure = null)
    {
        var options = TestHostFactory.CreateOptions();
        configure?.Invoke(options);

        return new SystemdWatchdogService(
            runtime,
            notify,
            health,
            hostLifetime ?? new TestHostApplicationLifetime(),
            Options.Create(options),
            NullLogger<SystemdWatchdogService>.Instance);
    }
}

internal sealed class TestHostApplicationLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _started = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _stopped = new();

    public CancellationToken ApplicationStarted => _started.Token;

    public CancellationToken ApplicationStopping => _stopping.Token;

    public CancellationToken ApplicationStopped => _stopped.Token;

    public bool StopRequested { get; private set; }

    public TaskCompletionSource StoppingRequested { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void StopApplication()
    {
        StopRequested = true;
        StoppingRequested.TrySetResult();
        _stopping.Cancel();
    }
}
