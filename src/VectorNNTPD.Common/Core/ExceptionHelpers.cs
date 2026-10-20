namespace VectorNNTPD.Common.Core
{
    /// <summary>
    /// Shared classification of process-integrity failures that must not be contained by best-effort boundaries.
    /// </summary>
    public static class ExceptionHelpers
    {
        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="exception"/> must tear down the process rather than be logged and continued.
        /// </summary>
        /// <param name="exception">Observed failure.</param>
        /// <returns>
        /// <see langword="true"/> for <see cref="OutOfMemoryException"/>, <see cref="StackOverflowException"/>,
        /// <see cref="AccessViolationException"/>, <see cref="AppDomainUnloadedException"/>,
        /// <see cref="BadImageFormatException"/>, and <see cref="CannotUnloadAppDomainException"/>.
        /// </returns>
        /// <remarks>
        /// Subscriber, logger, and similar open-set failure boundaries contain every other exception.
        /// These six are the process-integrity failures that must propagate.
        /// <see cref="StackOverflowException"/> is not reliably catchable; the check documents the policy when a runtime surfaces it.
        /// </remarks>
        public static bool IsFatal(Exception exception) =>
            exception is OutOfMemoryException
                or StackOverflowException
                or AccessViolationException
                or AppDomainUnloadedException
                or BadImageFormatException
                or CannotUnloadAppDomainException;
    }
}
