using VectorNNTPD.ReaderService.Hosting.Systemd;

namespace VectorNNTPD.ReaderService.Tests.Hosting.Systemd;

internal sealed class FakeSystemdNotifyBridge : ISystemdNotifyBridge
{
    private readonly object _sync = new();
    private readonly List<string> _notifications = [];
    private int _readyCount;
    private int _stoppingCount;
    private int _watchdogCount;

    public bool IsEnabled { get; set; } = true;

    public Exception? ThrowOnNotify { get; set; }

    public IReadOnlyList<string> Notifications
    {
        get
        {
            lock (_sync)
            {
                return _notifications.ToArray();
            }
        }
    }

    public int ReadyCount => Volatile.Read(ref _readyCount);

    public int StoppingCount => Volatile.Read(ref _stoppingCount);

    public int WatchdogCount => Volatile.Read(ref _watchdogCount);

    public TaskCompletionSource ReadySent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource StoppingSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource WatchdogSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void NotifyReady()
    {
        Record("READY=1", ref _readyCount);
        ReadySent.TrySetResult();
    }

    public void NotifyStopping()
    {
        Record("STOPPING=1", ref _stoppingCount);
        StoppingSent.TrySetResult();
    }

    public void NotifyStatus(string status)
    {
        if (ThrowOnNotify is not null)
        {
            throw ThrowOnNotify;
        }

        if (!IsEnabled)
        {
            return;
        }

        lock (_sync)
        {
            _notifications.Add("STATUS=" + status);
        }
    }

    public void NotifyWatchdog()
    {
        Record("WATCHDOG=1", ref _watchdogCount);
        WatchdogSent.TrySetResult();
    }

    private void Record(string value, ref int counter)
    {
        if (ThrowOnNotify is not null)
        {
            throw ThrowOnNotify;
        }

        if (!IsEnabled)
        {
            return;
        }

        lock (_sync)
        {
            _notifications.Add(value);
        }

        Interlocked.Increment(ref counter);
    }
}

internal sealed class FakeSystemdRuntime : ISystemdRuntime
{
    public bool IsLinux { get; set; }

    public bool IsSystemdService { get; set; }

    public bool IsNotifyEnabled { get; set; }

    public TimeSpan? WatchdogTimeout { get; set; }

    public bool IsWatchdogConfigured => WatchdogTimeout is not null;
}

internal sealed class FakeApplicationHealth : IApplicationHealth
{
    public bool IsHealthyForWatchdog { get; set; }
}

internal sealed class RecordingSystemdRuntimeFactory
{
    public static FakeSystemdRuntime LinuxUnderSystemd(TimeSpan? watchdog = null) => new()
    {
        IsLinux = true,
        IsSystemdService = true,
        IsNotifyEnabled = true,
        WatchdogTimeout = watchdog,
    };

    public static FakeSystemdRuntime LinuxConsole() => new()
    {
        IsLinux = true,
        IsSystemdService = false,
        IsNotifyEnabled = false,
        WatchdogTimeout = null,
    };

    public static FakeSystemdRuntime NonLinux() => new()
    {
        IsLinux = false,
        IsSystemdService = false,
        IsNotifyEnabled = false,
        WatchdogTimeout = null,
    };
}
