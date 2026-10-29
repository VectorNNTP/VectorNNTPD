namespace VectorNNTPD.TransitService.Hosting.Systemd
{
    /// <summary>
    /// Application health view used for systemd watchdog keep-alives.
    /// </summary>
    /// <remarks>
    /// Phase 0.1 health is lifecycle-based only. NNTP/data-plane checks are intentionally deferred.
    /// </remarks>
    public interface IApplicationHealth
    {
        /// <summary>
        /// Gets a value indicating whether the application is healthy enough to send watchdog keep-alives.
        /// </summary>
        /// <remarks>
        /// Healthy means: lifecycle state is <c>Running</c>, required services have initialized,
        /// no fatal supervised service failure has been observed, and shutdown has not started.
        /// </remarks>
        bool IsHealthyForWatchdog { get; }
    }
}
