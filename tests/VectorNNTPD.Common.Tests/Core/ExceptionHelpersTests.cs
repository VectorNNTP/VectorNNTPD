using VectorNNTPD.Common.Core;

namespace VectorNNTPD.Common.Tests.Core
{
    /// <summary>Regresses process-integrity classification used by intentional catch filters.</summary>
    public sealed class ExceptionHelpersTests
    {
        /// <summary>Operational failures are not treated as process-integrity failures.</summary>
        /// <param name="exception">Representative non-fatal exception.</param>
        [Theory]
        [MemberData(nameof(NonFatalExceptions))]
        public void IsFatal_returns_false_for_operational_failures(Exception exception)
        {
            Assert.False(ExceptionHelpers.IsFatal(exception));
        }

        /// <summary>Process-integrity failures must propagate past best-effort containment.</summary>
        /// <param name="exception">Representative fatal exception.</param>
        [Theory]
        [MemberData(nameof(FatalExceptions))]
        public void IsFatal_returns_true_for_process_integrity_failures(Exception exception)
        {
            Assert.True(ExceptionHelpers.IsFatal(exception));
        }

        /// <summary>
        /// A containment boundary that filters with <see cref="ExceptionHelpers.IsFatal"/> absorbs operational
        /// failures and rethrows process-integrity failures.
        /// </summary>
        [Fact]
        public void Containment_filter_absorbs_operational_failures_and_rethrows_fatal_ones()
        {
            Assert.Equal("contained", Contain(new IOException("disk full")));
            Assert.Throws<OutOfMemoryException>(() => Contain(new OutOfMemoryException()));
        }

        /// <summary>Non-fatal samples for classification.</summary>
        public static TheoryData<Exception> NonFatalExceptions() =>
        [
            new IOException("io"),
            new InvalidOperationException("invalid"),
            new TimeoutException("timeout"),
            new OperationCanceledException(),
            new ArgumentException("arg"),
            new Exception("base"),
        ];

        /// <summary>Fatal samples for classification.</summary>
        public static TheoryData<Exception> FatalExceptions() =>
        [
            new OutOfMemoryException(),
            new StackOverflowException(),
            new AccessViolationException(),
            new AppDomainUnloadedException(),
            new BadImageFormatException(),
            new CannotUnloadAppDomainException(),
        ];

        private static string Contain(Exception failure)
        {
            try
            {
                throw failure;
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                return "contained";
            }
        }
    }
}
