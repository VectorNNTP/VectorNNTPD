using VectorNNTPD.Common.Acme.Protocol;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.Common.Cloudflare;

namespace VectorNNTPD.BackFillerService.Tests.Hosting
{
    /// <summary>Regresses process exit codes and teardown when host failures, logging, or console restore throw.</summary>
    public sealed class BackFillerProcessBoundaryTests
    {
        /// <summary>Operational and unexpected exceptions map to <see cref="BackFillerProcessBoundary.FailureExitCode"/> and are forwarded to the observer.</summary>
        /// <param name="failure">Host or dependency failure surfaced to the process boundary.</param>
        [Theory]
        [MemberData(nameof(OperationalFailures))]
        public void Operational_and_unexpected_host_failures_return_the_failure_exit_code(Exception failure)
        {
            Exception? observed = null;

            var code = BackFillerProcessBoundary.ObserveHostFailure(failure, exception => observed = exception);

            Assert.Equal(BackFillerProcessBoundary.FailureExitCode, code);
            Assert.Same(failure, observed);
        }

        /// <summary>Failure to log a host exception must not change the failure exit code returned to the process.</summary>
        [Fact]
        public void Fatal_log_failure_still_returns_the_failure_exit_code()
        {
            var diagnostic = TestSelfLog.Capture(() =>
            {
                var code = BackFillerProcessBoundary.ObserveHostFailure(
                    new NntpDbAuthenticationException("access denied"),
                    static _ => throw new InvalidOperationException("log sink failed"));

                Assert.Equal(BackFillerProcessBoundary.FailureExitCode, code);
            });

            Assert.Contains("host failure log failed", diagnostic, StringComparison.Ordinal);
        }

        /// <summary>Logger close failure after exit code 0 still yields failure exit code; console restore still runs once.</summary>
        [Fact]
        public async Task Logger_shutdown_failure_after_a_clean_run_returns_the_failure_exit_code()
        {
            var restored = 0;

            var code = await BackFillerProcessBoundary.FinishAsync(
                exitCode: 0,
                () => throw new NotSupportedException("close boom"),
                () => restored++);

            Assert.Equal(BackFillerProcessBoundary.FailureExitCode, code);
            Assert.Equal(1, restored);
        }

        /// <summary>When the process already failed, logger shutdown failure must not downgrade the exit code.</summary>
        [Fact]
        public async Task Logger_shutdown_failure_keeps_an_existing_host_failure_code()
        {
            var restored = 0;

            var diagnostic = await TestSelfLog.CaptureAsync(async () =>
            {
                var code = await BackFillerProcessBoundary.FinishAsync(
                    BackFillerProcessBoundary.FailureExitCode,
                    () => throw new NotSupportedException("close boom"),
                    () => restored++);
                Assert.Equal(BackFillerProcessBoundary.FailureExitCode, code);
            });

            Assert.Equal(1, restored);
            Assert.Contains("logger shutdown failed after a host failure", diagnostic, StringComparison.Ordinal);
            Assert.Contains("close boom", diagnostic, StringComparison.Ordinal);
        }

        /// <summary>Console restore failure after a clean run leaves exit code 0 and records self-log diagnostics.</summary>
        [Fact]
        public async Task Console_restore_failure_does_not_replace_the_exit_code()
        {
            var diagnostic = await TestSelfLog.CaptureAsync(async () =>
            {
                var code = await BackFillerProcessBoundary.FinishAsync(
                    exitCode: 0,
                    () => Task.CompletedTask,
                    () => throw new NotSupportedException("restore boom"));
                Assert.Equal(0, code);
            });

            Assert.Contains("console restore failed", diagnostic, StringComparison.Ordinal);
            Assert.Contains("restore boom", diagnostic, StringComparison.Ordinal);
        }

        /// <summary>Representative host-failure exceptions for exit-code mapping.</summary>
        /// <returns>Theory data of exceptions that must yield the failure exit code.</returns>
        public static TheoryData<Exception> OperationalFailures() =>
        [
            new NntpDbAuthenticationException("access denied"),
            new NntpDbUnavailableException("database unavailable"),
            new AcmeCaException("acme directory failed"),
            new CloudflareDnsException("dns update failed"),
            new InvalidOperationException("host exploded"),
            new OperationCanceledException("startup canceled"),
            new TimeoutException("Graceful shutdown timed out after 00:00:30 while stopping 'listener'."),
        ];
    }
}
