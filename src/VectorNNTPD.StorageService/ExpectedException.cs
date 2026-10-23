namespace VectorNNTPD.StorageService
{

    /// <summary>
    /// Marks an exception as inspected and intentionally not propagated.
    /// </summary>
    /// <remarks>
    /// Call sites use this instead of an empty <c>catch</c> so shutdown races and best-effort cleanup
    /// remain explicit for readers and static analysis, without logging high-frequency expected faults.
    /// </remarks>
    internal static class ExpectedException
    {
        /// <summary>
        /// Discards <paramref name="exception"/> after the caller has judged it safe to ignore.
        /// </summary>
        /// <param name="exception">The exception that will not be rethrown or logged here.</param>
        internal static void Ignore(Exception exception) => _ = exception;
    }
}
