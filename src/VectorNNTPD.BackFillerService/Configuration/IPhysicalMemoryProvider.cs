namespace VectorNNTPD.BackFillerService.Configuration
{
    /// <summary>
    /// Supplies total physical RAM for host startup prerequisites and article-retention capacity validation.
    /// </summary>
    /// <remarks>
    /// Implementations must return total physical system memory, not available memory,
    /// process memory, GC heap limits, or container/cgroup limits.
    /// <see cref="BackFillerOptionsValidator"/> requires at least
    /// <see cref="BackFillerArticleRetentionOptions.MinimumPhysicalMemoryBytes"/> and rejects startup when
    /// this probe fails.
    /// </remarks>
    internal interface IPhysicalMemoryProvider
    {
        /// <summary>
        /// Returns total physical memory in bytes.
        /// </summary>
        /// <returns>Total physical memory in bytes.</returns>
        /// <exception cref="InvalidOperationException">Thrown when physical memory cannot be determined.</exception>
        /// <exception cref="PlatformNotSupportedException">Thrown when the current OS has no supported physical-memory source.</exception>
        long GetTotalPhysicalMemoryBytes();
    }
}
