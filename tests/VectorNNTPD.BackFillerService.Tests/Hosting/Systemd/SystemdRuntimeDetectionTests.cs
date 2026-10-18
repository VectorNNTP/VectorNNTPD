using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Hosting.Systemd;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Hosting.Systemd
{
    /// <summary>SystemdRuntime watchdog parsing and safe behavior when env configuration is missing or invalid.</summary>
    public sealed class SystemdRuntimeDetectionTests
    {
        /// <summary>On non-Linux, watchdog env vars do not enable IsWatchdogConfigured (Linux CI skips the early return path).</summary>
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
        /// <summary>Absent WATCHDOG_USEC yields no timeout and TryParseWatchdogTimeout reports the documented reason.</summary>

        [Fact]
        public void MissingWatchdogConfiguration_IsSafe()
        {
            using var scope = new EnvironmentVariableScope(
                (SystemdRuntime.WatchdogUsecVariable, null),
                (SystemdRuntime.WatchdogPidVariable, null));

            var runtime = new SystemdRuntime([], NullLogger<SystemdRuntime>.Instance);
            Assert.False(runtime.IsWatchdogConfigured);
            Assert.Null(runtime.WatchdogTimeout);

            var parsed = SystemdRuntime.TryParseWatchdogTimeout(
                null,
                null,
                Environment.ProcessId,
                NullLogger.Instance,
                out var reason);
            Assert.Null(parsed);
            Assert.Equal("WATCHDOG_USEC not set", reason);
        }
        /// <summary>Invalid WATCHDOG_USEC is ignored with parse reason and warning log regardless of OS detection path.</summary>

        [Theory]
        [InlineData("not-a-number")]
        [InlineData("0")]
        [InlineData("-1")]
        public void InvalidWatchdogUsec_IsIgnored_OnLinuxPath(string usec)
        {
            using var scope = new EnvironmentVariableScope(
                (SystemdRuntime.WatchdogUsecVariable, usec),
                (SystemdRuntime.WatchdogPidVariable, null));

            var runtime = new SystemdRuntime([], NullLogger<SystemdRuntime>.Instance);
            Assert.False(runtime.IsWatchdogConfigured);
            Assert.Null(runtime.WatchdogTimeout);

            var logs = new CollectingLogger<SystemdRuntime>();
            var parsed = SystemdRuntime.TryParseWatchdogTimeout(
                usec,
                null,
                Environment.ProcessId,
                logs,
                out var reason);
            Assert.Null(parsed);
            Assert.Equal("WATCHDOG_USEC missing or invalid", reason);
            Assert.Contains(
                logs.Messages,
                message => message.Contains("WATCHDOG_USEC is not a positive integer", StringComparison.Ordinal));
        }
        /// <summary>Positive WATCHDOG_USEC microseconds map to the expected TimeSpan in TryParseWatchdogTimeout.</summary>

        [Fact]
        public void ValidWatchdogUsec_IsAccepted_ByTheLinuxParser()
        {
            var parsed = SystemdRuntime.TryParseWatchdogTimeout(
                "5000000",
                null,
                Environment.ProcessId,
                NullLogger.Instance,
                out var reason);
            Assert.Equal(TimeSpan.FromSeconds(5), parsed);
            Assert.Equal("WATCHDOG_USEC accepted", reason);
        }
        /// <summary>Fake runtime factory profiles distinguish systemd service, Linux console, and non-Linux hosts.</summary>

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

        /// <summary>Temporarily sets process environment variables and restores prior values on dispose.</summary>
        private sealed class EnvironmentVariableScope : IDisposable
        {
            /// <summary>Captured values to restore in <see cref="Dispose"/>.</summary>
            private readonly (string Name, string? Previous)[] _previous;

            /// <summary>Applies <paramref name="values"/> for the scope lifetime.</summary>
            /// <param name="values">Variables to set; previous values are saved.</param>
            internal EnvironmentVariableScope(params (string Name, string? Value)[] values)
            {
                _previous = values
                    .Select(v => (v.Name, Environment.GetEnvironmentVariable(v.Name)))
                    .ToArray();

                foreach (var (name, value) in values)
                {
                    Environment.SetEnvironmentVariable(name, value);
                }
            }

            /// <inheritdoc />
            public void Dispose()
            {
                foreach (var (name, previous) in _previous)
                {
                    Environment.SetEnvironmentVariable(name, previous);
                }
            }
        }
    }
}
