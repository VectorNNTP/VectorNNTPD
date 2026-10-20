using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Hosting.Systemd;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Hosting.Systemd
{
    /// <summary>
    /// SystemdLifecycleNotifier must emit READY only after ApplicationStarted, retry when health or notify
    /// recovers, keep READY once-only after success, and emit STOPPING on stopping; notify failures are non-fatal.
    /// </summary>
    public sealed class SystemdLifecycleNotifierTests
    {
        /// <summary>READY follows ApplicationStarted once; STOPPING follows ApplicationStopping; health marks running then not.</summary>
        [Fact]
        public async Task Readiness_FollowsApplicationStarted_AndIsNotDuplicated()
        {
            var notify = new FakeSystemdNotifyBridge();
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            using var notifier = CreateNotifier(lifetime, health, notify);

            await notifier.StartAsync(CancellationToken.None);
            Assert.Equal(0, notify.ReadyCount);
            Assert.Contains(notify.Notifications, n => n.Contains("Starting", StringComparison.Ordinal));

            lifetime.SignalStarted();
            Assert.Equal(1, notify.ReadyCount);
            Assert.True(health.IsHealthyForWatchdog);
            Assert.Contains(notify.Notifications, n => n.Contains("Running", StringComparison.Ordinal));

            lifetime.SignalStopping();
            Assert.Equal(1, notify.StoppingCount);
            Assert.False(health.IsHealthyForWatchdog);

            await notifier.StopAsync(CancellationToken.None);
            Assert.Equal(1, notify.ReadyCount);
            Assert.Equal(1, notify.StoppingCount);
        }

        /// <summary>Notifier start alone must not emit READY or mark watchdog health running.</summary>
        [Fact]
        public async Task Readiness_IsNotSentBeforeApplicationStarted()
        {
            var notify = new FakeSystemdNotifyBridge();
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            using var notifier = CreateNotifier(lifetime, health, notify);

            await notifier.StartAsync(CancellationToken.None);

            Assert.Equal(0, notify.ReadyCount);
            Assert.False(health.IsHealthyForWatchdog);
        }

        /// <summary>With notify disabled, started and stopping signals produce no notifications.</summary>
        [Fact]
        public async Task Readiness_IsNotSentWhenNotifyDisabled()
        {
            var notify = new FakeSystemdNotifyBridge { IsEnabled = false };
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            using var notifier = CreateNotifier(lifetime, health, notify);

            await notifier.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();
            lifetime.SignalStopping();
            await notifier.StopAsync(CancellationToken.None);

            Assert.Empty(notify.Notifications);
            Assert.Equal(0, notify.ReadyCount);
            Assert.Equal(0, notify.StoppingCount);
        }

        /// <summary>After stopping is signaled, a late ApplicationStarted must not emit READY.</summary>
        [Fact]
        public async Task Shutdown_PreventsSubsequentReadinessReporting()
        {
            var notify = new FakeSystemdNotifyBridge();
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            using var notifier = CreateNotifier(lifetime, health, notify);

            await notifier.StartAsync(CancellationToken.None);
            lifetime.SignalStopping();
            Assert.Equal(1, notify.StoppingCount);

            lifetime.SignalStarted();
            Assert.Equal(0, notify.ReadyCount);
            Assert.False(health.IsHealthyForWatchdog);

            await notifier.StopAsync(CancellationToken.None);
            Assert.Equal(1, notify.StoppingCount);
        }

        /// <summary>Second StopAsync does not duplicate READY or STOPPING counts.</summary>
        [Fact]
        public async Task RepeatedStop_IsIdempotent()
        {
            var notify = new FakeSystemdNotifyBridge();
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            using var notifier = CreateNotifier(lifetime, health, notify);

            await notifier.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();
            lifetime.SignalStopping();

            await notifier.StopAsync(CancellationToken.None);
            await notifier.StopAsync(CancellationToken.None);

            Assert.Equal(1, notify.ReadyCount);
            Assert.Equal(1, notify.StoppingCount);
        }

        /// <summary>Notify exceptions are swallowed so notifier start/stop completes without duplicate READY/STOPPING.</summary>
        [Fact]
        public async Task NotificationFailure_DoesNotCrashNotifier()
        {
            var notify = new FakeSystemdNotifyBridge
            {
                ThrowOnNotify = new InvalidOperationException("notify-failed"),
            };
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            using var notifier = CreateNotifier(lifetime, health, notify);

            await notifier.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();
            lifetime.SignalStopping();
            await notifier.StopAsync(CancellationToken.None);

            Assert.Equal(0, notify.ReadyCount);
            Assert.Equal(0, notify.StoppingCount);
        }

        /// <summary>
        /// When certificate health blocks READY at ApplicationStarted, clearing the block later sends READY once.
        /// </summary>
        [Fact]
        public async Task Readiness_RetriesWhenHealthBecomesReadyAfterApplicationStarted()
        {
            var notify = new FakeSystemdNotifyBridge();
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            health.MarkCertificateUnavailable();
            using var notifier = CreateNotifier(lifetime, health, notify);

            await notifier.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();

            Assert.Equal(0, notify.ReadyCount);
            Assert.False(health.IsHealthyForWatchdog);

            health.MarkCertificateAvailable();

            Assert.True(health.IsHealthyForWatchdog);
            Assert.Equal(1, notify.ReadyCount);

            health.MarkCertificateUnavailable();
            health.MarkCertificateAvailable();
            Assert.Equal(1, notify.ReadyCount);
        }

        /// <summary>A handled transient NotifyReady failure is retried on the readiness timer and then succeeds once.</summary>
        [Fact]
        public async Task Readiness_RetriesAfterTransientNotifyFailure()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-20T00:00:00Z"));
            var notify = new FakeSystemdNotifyBridge { ReadyFailuresRemaining = 1 };
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            using var notifier = CreateNotifier(lifetime, health, notify, time: time);

            await notifier.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();

            Assert.Equal(0, notify.ReadyCount);
            Assert.Equal(1, notify.ReadyAttemptCount);

            time.Advance(SystemdLifecycleNotifier.ReadyRetryInterval);

            Assert.Equal(1, notify.ReadyCount);
            Assert.Equal(2, notify.ReadyAttemptCount);

            time.Advance(SystemdLifecycleNotifier.ReadyRetryInterval);
            Assert.Equal(1, notify.ReadyCount);
            Assert.Equal(2, notify.ReadyAttemptCount);
        }

        /// <summary>Multiple consecutive notify failures eventually recover on a later successful attempt.</summary>
        [Fact]
        public async Task Readiness_RecoversAfterMultipleConsecutiveNotifyFailures()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-20T00:00:00Z"));
            var notify = new FakeSystemdNotifyBridge { ReadyFailuresRemaining = 3 };
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            using var notifier = CreateNotifier(lifetime, health, notify, time: time);

            await notifier.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();

            Assert.Equal(0, notify.ReadyCount);
            Assert.Equal(1, notify.ReadyAttemptCount);

            time.Advance(SystemdLifecycleNotifier.ReadyRetryInterval);
            Assert.Equal(0, notify.ReadyCount);
            Assert.Equal(2, notify.ReadyAttemptCount);

            time.Advance(SystemdLifecycleNotifier.ReadyRetryInterval);
            Assert.Equal(0, notify.ReadyCount);
            Assert.Equal(3, notify.ReadyAttemptCount);

            time.Advance(SystemdLifecycleNotifier.ReadyRetryInterval);
            Assert.Equal(1, notify.ReadyCount);
            Assert.Equal(4, notify.ReadyAttemptCount);

            time.Advance(SystemdLifecycleNotifier.ReadyRetryInterval);
            Assert.Equal(1, notify.ReadyCount);
            Assert.Equal(4, notify.ReadyAttemptCount);
        }

        /// <summary>Repeated health-improved callbacks after a successful READY do not send again.</summary>
        [Fact]
        public async Task Readiness_RepeatedHealthCallbacks_DoNotDuplicateReady()
        {
            var notify = new FakeSystemdNotifyBridge();
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            using var notifier = CreateNotifier(lifetime, health, notify);

            await notifier.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();
            Assert.Equal(1, notify.ReadyCount);

            for (var i = 0; i < 8; i++)
            {
                health.MarkCertificateUnavailable();
                health.MarkCertificateAvailable();
            }

            Assert.Equal(1, notify.ReadyCount);
        }

        /// <summary>Concurrent readiness triggers share the send gate and produce one successful READY.</summary>
        [Fact]
        public async Task Readiness_ConcurrentTriggers_DoNotDuplicateReady()
        {
            var notify = new FakeSystemdNotifyBridge();
            var gate = new ManualResetEventSlim(false);
            notify.BlockReadyUntil = gate;
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            health.MarkCertificateUnavailable();
            using var notifier = CreateNotifier(lifetime, health, notify);

            await notifier.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();
            Assert.Equal(0, notify.ReadyCount);

            var start = new ManualResetEventSlim(false);
            var workers = Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    health.MarkCertificateAvailable();
                }))
                .ToArray();

            start.Set();
            await notify.ReadyAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, notify.ReadyAttemptCount);
            Assert.Equal(0, notify.ReadyCount);

            gate.Set();
            await Task.WhenAll(workers);
            await notify.ReadySent.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, notify.ReadyCount);
            Assert.True(notify.ReadyAttemptCount >= 1);
        }

        /// <summary>Disposal cancels retries and prevents a later health recovery from emitting READY.</summary>
        [Fact]
        public async Task Disposal_CancelsRetries_AndPreventsLaterReady()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-20T00:00:00Z"));
            var notify = new FakeSystemdNotifyBridge { ReadyFailuresRemaining = 5 };
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            var notifier = CreateNotifier(lifetime, health, notify, time: time);

            await notifier.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();
            Assert.Equal(0, notify.ReadyCount);
            Assert.Equal(1, notify.ReadyAttemptCount);

            notifier.Dispose();
            notify.ReadyFailuresRemaining = 0;
            time.Advance(SystemdLifecycleNotifier.ReadyRetryInterval * 3);
            health.MarkCertificateUnavailable();
            health.MarkCertificateAvailable();

            Assert.Equal(0, notify.ReadyCount);
            Assert.Equal(1, notify.ReadyAttemptCount);
        }

        /// <summary>Readiness stays blocked while any required health condition remains false.</summary>
        [Fact]
        public async Task Readiness_RemainsBlockedWhileHealthIsUnhealthy()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-20T00:00:00Z"));
            var notify = new FakeSystemdNotifyBridge();
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            health.MarkCertificateUnavailable();
            using var notifier = CreateNotifier(lifetime, health, notify, time: time);

            await notifier.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();

            time.Advance(SystemdLifecycleNotifier.ReadyRetryInterval * 3);
            Assert.Equal(0, notify.ReadyCount);
            Assert.Equal(0, notify.ReadyAttemptCount);
            Assert.False(health.IsHealthyForWatchdog);
        }

        /// <summary>Shutdown while a readiness retry is armed prevents READY after the notify endpoint recovers.</summary>
        [Fact]
        public async Task Shutdown_WhileRetryArmed_PreventsLaterReady()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-20T00:00:00Z"));
            var notify = new FakeSystemdNotifyBridge { ReadyFailuresRemaining = 2 };
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            using var notifier = CreateNotifier(lifetime, health, notify, time: time);

            await notifier.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();
            Assert.Equal(0, notify.ReadyCount);

            lifetime.SignalStopping();
            notify.ReadyFailuresRemaining = 0;
            time.Advance(SystemdLifecycleNotifier.ReadyRetryInterval * 3);
            health.MarkCertificateAvailable();

            Assert.Equal(0, notify.ReadyCount);
            Assert.Equal(1, notify.StoppingCount);
        }

        /// <summary>BackFillerApplicationHealth reflects started, stopping, and unexpected termination for watchdog gating.</summary>
        [Fact]
        public void ApplicationHealth_TracksStartedStoppingAndUnexpectedTermination()
        {
            var health = new BackFillerApplicationHealth();
            Assert.False(health.IsHealthyForWatchdog);

            health.MarkStarted();
            Assert.True(health.IsHealthyForWatchdog);

            health.MarkUnexpectedTermination();
            Assert.False(health.IsHealthyForWatchdog);

            var health2 = new BackFillerApplicationHealth();
            health2.MarkStarted();
            health2.MarkStopping();
            Assert.False(health2.IsHealthyForWatchdog);
        }

        /// <summary>MarkCertificateAvailable raises the improved-health event used for deferred READY retries.</summary>
        [Fact]
        public void ApplicationHealth_CertificateAvailable_RaisesImprovedEvent()
        {
            var health = new BackFillerApplicationHealth();
            var raised = 0;
            health.WatchdogHealthMayHaveImproved += () => raised++;

            health.MarkCertificateUnavailable();
            Assert.Equal(0, raised);

            health.MarkCertificateAvailable();
            Assert.Equal(1, raised);
        }

        /// <summary>Constructs a lifecycle notifier with Linux-under-systemd runtime fakes.</summary>
        private static SystemdLifecycleNotifier CreateNotifier(
            IHostApplicationLifetime lifetime,
            BackFillerApplicationHealth health,
            FakeSystemdNotifyBridge notify,
            Action<BackFillerOptions>? configure = null,
            TimeProvider? time = null)
        {
            var options = BackFillerTestOptions.CreateValid();
            configure?.Invoke(options);

            return new SystemdLifecycleNotifier(
                lifetime,
                health,
                notify,
                RecordingSystemdRuntimeFactory.LinuxUnderSystemd(),
                Options.Create(options),
                NullLogger<SystemdLifecycleNotifier>.Instance,
                time ?? new ManualTimeProvider(DateTimeOffset.Parse("2026-10-20T00:00:00Z")));
        }
    }
}
