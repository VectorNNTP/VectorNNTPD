namespace VectorNNTPD.StorageService.Hosting.Systemd
{

    /// <summary>
    /// Describes the process relationship to systemd for hosting decisions.
    /// </summary>
    public interface ISystemdRuntime
    {
        /// <summary>Gets a value indicating whether the process is running on Linux.</summary>
        bool IsLinux { get; }

        /// <summary>
        /// Gets a value indicating whether the process appears to be managed by systemd
        /// (direct systemd service / compatible notify environment).
        /// </summary>
        bool IsSystemdService { get; }

        /// <summary>
        /// Gets a value indicating whether systemd notify is available for this process
        /// (typically <c>NOTIFY_SOCKET</c> was present when the host notifier was constructed).
        /// </summary>
        bool IsNotifyEnabled { get; }

        /// <summary>
        /// Gets the systemd watchdog deadline when configured and applicable to this process;
        /// otherwise <see langword="null"/>.
        /// </summary>
        TimeSpan? WatchdogTimeout { get; }

        /// <summary>
        /// Gets a value indicating whether systemd expects watchdog keep-alives from this process.
        /// </summary>
        bool IsWatchdogConfigured { get; }
    }
}
