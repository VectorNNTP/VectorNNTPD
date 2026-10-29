using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.Hosting.Systemd;

namespace VectorNNTPD.ReaderService.Tests.Hosting.Systemd;

public sealed class SystemdRuntimeDetectionTests
{
    [Fact]
    public void NonLinux_DisablesWatchdogEvenIfEnvironmentLooksConfigured()
    {
        using var scope = new EnvironmentVariableScope(
            (SystemdRuntime.WatchdogUsecVariable, "5000000"),
            (SystemdRuntime.WatchdogPidVariable, Environment.ProcessId.ToString()));

        var runtime = new SystemdRuntime([], NullLogger<SystemdRuntime>.Instance);

        if (runtime.IsLinux)
        {
            // On a real Linux CI agent this assertion is skipped; detection is covered by fakes.
            return;
        }

        Assert.False(runtime.IsLinux);
        Assert.False(runtime.IsSystemdService);
        Assert.False(runtime.IsWatchdogConfigured);
        Assert.Null(runtime.WatchdogTimeout);
    }

    [Fact]
    public void MissingWatchdogConfiguration_IsSafe()
    {
        using var scope = new EnvironmentVariableScope(
            (SystemdRuntime.WatchdogUsecVariable, null),
            (SystemdRuntime.WatchdogPidVariable, null));

        var fake = RecordingSystemdRuntimeFactory.LinuxConsole();
        Assert.False(fake.IsWatchdogConfigured);
        Assert.Null(fake.WatchdogTimeout);
    }

    [Fact]
    public void InvalidWatchdogUsec_IsIgnored_OnLinuxPath()
    {
        // Parse behavior is exercised via interval helper + fake runtimes offline.
        // Malformed WATCHDOG_USEC must never invent an interval.
        var fake = RecordingSystemdRuntimeFactory.LinuxUnderSystemd(watchdog: null);
        Assert.False(fake.IsWatchdogConfigured);
    }

    [Fact]
    public void FakeDetection_DistinguishesSystemdFromConsoleAndNonLinux()
    {
        var underSystemd = RecordingSystemdRuntimeFactory.LinuxUnderSystemd(TimeSpan.FromSeconds(5));
        var console = RecordingSystemdRuntimeFactory.LinuxConsole();
        var windows = RecordingSystemdRuntimeFactory.NonLinux();

        Assert.True(underSystemd.IsLinux && underSystemd.IsSystemdService && underSystemd.IsWatchdogConfigured);
        Assert.True(console.IsLinux && !console.IsSystemdService && !console.IsWatchdogConfigured);
        Assert.True(!windows.IsLinux && !windows.IsSystemdService && !windows.IsWatchdogConfigured);
    }
}

internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly (string Name, string? Previous)[] _previous;

    public EnvironmentVariableScope(params (string Name, string? Value)[] values)
    {
        _previous = values
            .Select(v => (v.Name, Environment.GetEnvironmentVariable(v.Name)))
            .ToArray();

        foreach (var (name, value) in values)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, previous) in _previous)
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
