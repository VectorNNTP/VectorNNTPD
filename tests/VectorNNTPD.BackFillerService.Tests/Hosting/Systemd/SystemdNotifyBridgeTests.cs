using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Logging;
using VectorNNTPD.BackFillerService.Hosting.Systemd;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Hosting.Systemd
{
    /// <summary>SystemdNotifyBridge contains notifier and logging failures without propagating to callers.</summary>
    public sealed class SystemdNotifyBridgeTests
    {
        /// <summary>Socket errors from systemd notifier are logged at event 5105 and do not propagate from NotifyWatchdog.</summary>
        [Fact]
        public void SocketException_is_logged_and_does_not_escape()
        {
            var logger = new CollectingLogger<SystemdNotifyBridge>();
            var bridge = new SystemdNotifyBridge(
                [new ThrowingNotifier(new System.Net.Sockets.SocketException(10054))],
                logger);

            bridge.NotifyWatchdog();

            Assert.Contains(logger.Entries, static entry => entry.EventId.Id == 5105);
            Assert.Contains(logger.Messages, static message => message.Contains("WATCHDOG=1", StringComparison.Ordinal));
        }
        /// <summary>Non-socket notifier failures are logged and do not escape NotifyReady.</summary>

        [Fact]
        public void Unexpected_notifier_exception_is_logged_and_does_not_escape()
        {
            var logger = new CollectingLogger<SystemdNotifyBridge>();
            var bridge = new SystemdNotifyBridge(
                [new ThrowingNotifier(new NotSupportedException("notify socket"))],
                logger);

            bridge.NotifyReady();

            Assert.Contains(
                logger.Entries,
                entry => entry.EventId.Id == 5105 && entry.Message.Contains("notify socket", StringComparison.Ordinal));
        }
        /// <summary>When failure logging throws, Serilog self-log records it and the notify call still does not escape.</summary>

        [Fact]
        public void Notification_log_failure_does_not_escape()
        {
            var bridge = new SystemdNotifyBridge(
                [new ThrowingNotifier(new NotSupportedException("notify socket"))],
                new ThrowingLogger());

            var diagnostic = TestSelfLog.Capture(bridge.NotifyStopping);

            Assert.Contains("notification failure log failed", diagnostic, StringComparison.Ordinal);
        }
        /// <summary>Disabled ISystemdNotifier is not invoked and produces no bridge log entries.</summary>

        [Fact]
        public void Disabled_notifier_does_not_send()
        {
            var logger = new CollectingLogger<SystemdNotifyBridge>();
            var notifier = new ThrowingNotifier(new InvalidOperationException("should not send"))
            {
                Enabled = false,
            };
            var bridge = new SystemdNotifyBridge([notifier], logger);

            bridge.NotifyStatus("ready");

            Assert.Empty(logger.Entries);
            Assert.Equal(0, notifier.Calls);
        }

        /// <summary>ISystemdNotifier that throws on notify to exercise bridge fault isolation.</summary>
        private sealed class ThrowingNotifier(Exception exception) : ISystemdNotifier
        {
            /// <summary>When false, the notifier behaves as disabled.</summary>
            public bool Enabled { get; init; } = true;

            /// <summary>Notify invocations observed before the configured throw.</summary>
            public int Calls { get; private set; }

            /// <inheritdoc />
            public bool IsEnabled => Enabled;

            /// <inheritdoc />
            public void Notify(ServiceState state)
            {
                Calls++;
                throw exception;
            }
        }

        /// <summary>ILogger that always throws from Log to verify notify paths survive logging faults.</summary>
        private sealed class ThrowingLogger : ILogger<SystemdNotifyBridge>
        {
            /// <inheritdoc />
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => NullScope.Instance;

            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel) => true;

            /// <inheritdoc />
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                throw new InvalidOperationException("log failed");

            /// <summary>No-op logging scope for the throwing logger double.</summary>
            private sealed class NullScope : IDisposable
            {
                /// <summary>Shared no-op scope.</summary>
                public static NullScope Instance { get; } = new();

                /// <inheritdoc />
                public void Dispose()
                {
                }
            }
        }
    }
}
