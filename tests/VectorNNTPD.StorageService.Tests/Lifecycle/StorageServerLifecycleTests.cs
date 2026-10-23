using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Hosting.Systemd;
using VectorNNTPD.StorageService.Tests.Fixtures;
using VectorNNTPD.StorageService.Tests.TestDoubles;

namespace VectorNNTPD.StorageService.Tests.Lifecycle
{

    public sealed class StorageServerLifecycleTests
    {
        [Fact]
        public async Task ApplicationServiceManager_starts_in_registration_order_and_rolls_back()
        {
            var started = new List<string>();
            var stopped = new List<string>();
            var cloudflare = new FakeApplicationService(
                "CloudflareDns",
                onStart: _ =>
                {
                    started.Add("CloudflareDns");
                    return Task.CompletedTask;
                },
                onStop: _ =>
                {
                    stopped.Add("CloudflareDns");
                    return Task.CompletedTask;
                });
            var acme = new FakeApplicationService(
                "AcmeCertificate",
                onStart: _ =>
                {
                    started.Add("AcmeCertificate");
                    return Task.CompletedTask;
                },
                onStop: _ =>
                {
                    stopped.Add("AcmeCertificate");
                    return Task.CompletedTask;
                });
            var listener = new FakeApplicationService(
                "StorageNntpListener",
                onStart: _ => throw new InvalidOperationException("listener-fail"),
                onStop: _ =>
                {
                    stopped.Add("StorageNntpListener");
                    return Task.CompletedTask;
                });

            var options = StorageServerTestOptions.CreateValid();
            var manager = new ApplicationServiceManager(
                [cloudflare, acme, listener],
                options,
                NullLogger<ApplicationServiceManager>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(CancellationToken.None));
            Assert.Equal(["CloudflareDns", "AcmeCertificate"], started);
            Assert.Equal(["AcmeCertificate", "CloudflareDns"], stopped);
        }

        [Fact]
        public async Task Graceful_shutdown_stops_in_reverse_order()
        {
            var stopped = new List<string>();
            var services = new IApplicationService[]
            {
                new FakeApplicationService("CloudflareDns", onStop: _ => { stopped.Add("CloudflareDns"); return Task.CompletedTask; }),
                new FakeApplicationService("AcmeCertificate", onStop: _ => { stopped.Add("AcmeCertificate"); return Task.CompletedTask; }),
                new FakeApplicationService("StorageNntpListener", onStop: _ => { stopped.Add("StorageNntpListener"); return Task.CompletedTask; }),
            };
            var options = StorageServerTestOptions.CreateValid();
            var manager = new ApplicationServiceManager(services, options, NullLogger<ApplicationServiceManager>.Instance);
            var lifecycle = new ApplicationLifecycle(manager, options, NullLogger<ApplicationLifecycle>.Instance);

            await lifecycle.StartAsync(CancellationToken.None);
            Assert.Equal(ApplicationState.Running, lifecycle.State);
            await lifecycle.StopAsync(CancellationToken.None);
            Assert.Equal(ApplicationState.Stopped, lifecycle.State);
            Assert.Equal(["StorageNntpListener", "AcmeCertificate", "CloudflareDns"], stopped);
        }

        [Fact]
        public async Task Systemd_READY_only_after_Running()
        {
            var notify = new FakeSystemdNotifyBridge { IsEnabled = true };
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new FakeApplicationService(
                "slow",
                onStart: async ct =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                });

            var options = StorageServerTestOptions.CreateValid();
            var manager = new ApplicationServiceManager([service], options, NullLogger<ApplicationServiceManager>.Instance);
            await using var lifecycle = new ApplicationLifecycle(manager, options, NullLogger<ApplicationLifecycle>.Instance);
            using var notifier = new SystemdLifecycleNotifier(
                lifecycle,
                notify,
                new FakeSystemdRuntime { IsLinux = true, IsSystemdService = true, IsNotifyEnabled = true },
                Options.Create(options),
                NullLogger<SystemdLifecycleNotifier>.Instance);

            await notifier.StartAsync(CancellationToken.None);
            var start = lifecycle.StartAsync(CancellationToken.None);
            await entered.Task;
            Assert.Equal(ApplicationState.Starting, lifecycle.State);
            Assert.Equal(0, notify.ReadyCount);

            release.TrySetResult();
            await start;
            Assert.Equal(ApplicationState.Running, lifecycle.State);
            Assert.Equal(1, notify.ReadyCount);
        }
    }

    internal sealed class FakeSystemdNotifyBridge : VectorNNTPD.StorageService.Hosting.Systemd.ISystemdNotifyBridge
    {
        private readonly List<string> _notifications = [];
        private int _readyCount;
        private int _stoppingCount;

        public bool IsEnabled { get; set; } = true;

        public int ReadyCount => Volatile.Read(ref _readyCount);

        public int StoppingCount => Volatile.Read(ref _stoppingCount);

        public IReadOnlyList<string> Notifications => _notifications.ToArray();

        public void NotifyReady()
        {
            Interlocked.Increment(ref _readyCount);
            _notifications.Add("READY=1");
        }

        public void NotifyStopping()
        {
            Interlocked.Increment(ref _stoppingCount);
            _notifications.Add("STOPPING=1");
        }

        public void NotifyStatus(string status) => _notifications.Add("STATUS=" + status);

        public void NotifyWatchdog() => _notifications.Add("WATCHDOG=1");
    }

    internal sealed class FakeSystemdRuntime : VectorNNTPD.StorageService.Hosting.Systemd.ISystemdRuntime
    {
        public bool IsLinux { get; set; }

        public bool IsSystemdService { get; set; }

        public bool IsNotifyEnabled { get; set; }

        public TimeSpan? WatchdogTimeout { get; set; }

        public bool IsWatchdogConfigured { get; set; }
    }
}
