using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.TransitService.Hosting.Systemd;

namespace VectorNNTPD.TransitService.Tests.Hosting.Systemd;

public sealed class SystemdLifecycleNotifierTests
{
    [Fact]
    public async Task Readiness_FollowsSuccessfulInitialization_AndIsNotDuplicated()
    {
        var notify = new FakeSystemdNotifyBridge();
        var service = new FakeApplicationService("svc");
        await using var lifecycle = TestHostFactory.CreateLifecycle([service]);
        using var notifier = CreateNotifier(lifecycle, notify);

        await notifier.StartAsync(CancellationToken.None);
        Assert.Equal(0, notify.ReadyCount);

        await lifecycle.StartAsync(CancellationToken.None);

        Assert.Equal(1, notify.ReadyCount);
        Assert.Contains(notify.Notifications, n => n.StartsWith("STATUS=", StringComparison.Ordinal));

        // Force another Running notification attempt via duplicate event is not possible;
        // stopping and ensuring ready count stays at 1 after shutdown start.
        await lifecycle.StopAsync(CancellationToken.None);
        Assert.Equal(1, notify.ReadyCount);
        Assert.Equal(1, notify.StoppingCount);
    }

    [Fact]
    public async Task Readiness_IsNotSentDuringStartup()
    {
        var notify = new FakeSystemdNotifyBridge();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeApplicationService(
            "slow",
            onStart: async ct =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            });

        await using var lifecycle = TestHostFactory.CreateLifecycle([service]);
        using var notifier = CreateNotifier(lifecycle, notify);
        await notifier.StartAsync(CancellationToken.None);

        var start = lifecycle.StartAsync(CancellationToken.None);
        await entered.Task;

        Assert.Equal(ApplicationState.Starting, lifecycle.State);
        Assert.Equal(0, notify.ReadyCount);
        Assert.Contains(notify.Notifications, n => n.Contains("Starting", StringComparison.Ordinal));

        release.TrySetResult();
        await start;

        Assert.Equal(1, notify.ReadyCount);
    }

    [Fact]
    public async Task Readiness_IsNotSentAfterFailedStartup()
    {
        var notify = new FakeSystemdNotifyBridge();
        var service = new FakeApplicationService(
            "bad",
            onStart: _ => throw new InvalidOperationException("boom"));

        await using var lifecycle = TestHostFactory.CreateLifecycle([service]);
        using var notifier = CreateNotifier(lifecycle, notify);
        await notifier.StartAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartAsync(CancellationToken.None));

        Assert.Equal(0, notify.ReadyCount);
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Equal(1, notify.StoppingCount);
    }

    [Fact]
    public async Task Shutdown_PreventsSubsequentReadinessReporting()
    {
        var notify = new FakeSystemdNotifyBridge();
        await using var lifecycle = TestHostFactory.CreateLifecycle([new FakeApplicationService("svc")]);
        using var notifier = CreateNotifier(lifecycle, notify);
        await notifier.StartAsync(CancellationToken.None);

        await lifecycle.StartAsync(CancellationToken.None);
        Assert.Equal(1, notify.ReadyCount);

        await lifecycle.StopAsync(CancellationToken.None);
        Assert.Equal(1, notify.StoppingCount);
        Assert.True(lifecycle.ShutdownRequested);

        // A second stop is idempotent and must not emit another readiness notification.
        await lifecycle.StopAsync(CancellationToken.None);
        Assert.Equal(1, notify.ReadyCount);
        Assert.Equal(1, notify.StoppingCount);
    }

    [Fact]
    public async Task Notifications_AreNoOpsWhenBridgeDisabled()
    {
        var notify = new FakeSystemdNotifyBridge { IsEnabled = false };
        await using var lifecycle = TestHostFactory.CreateLifecycle([new FakeApplicationService("svc")]);
        using var notifier = CreateNotifier(lifecycle, notify);
        await notifier.StartAsync(CancellationToken.None);

        await lifecycle.StartAsync(CancellationToken.None);
        await lifecycle.StopAsync(CancellationToken.None);

        Assert.Empty(notify.Notifications);
    }

    private static SystemdLifecycleNotifier CreateNotifier(
        ApplicationLifecycle lifecycle,
        FakeSystemdNotifyBridge notify,
        Action<NntpdOptions>? configure = null)
    {
        var options = TestHostFactory.CreateOptions();
        configure?.Invoke(options);

        return new SystemdLifecycleNotifier(
            lifecycle,
            notify,
            RecordingSystemdRuntimeFactory.LinuxUnderSystemd(),
            Options.Create(options),
            NullLogger<SystemdLifecycleNotifier>.Instance);
    }
}
