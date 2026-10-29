namespace VectorNNTPD.ReaderService.Hosting.Systemd
{
    /// <summary>
    /// Testable abstraction over systemd service notifications.
    /// </summary>
    public interface ISystemdNotifyBridge
    {
        /// <summary>Gets a value indicating whether notifications are delivered to systemd.</summary>
        bool IsEnabled { get; }

        /// <summary>Sends <c>READY=1</c>.</summary>
        void NotifyReady();

        /// <summary>Sends <c>STOPPING=1</c>.</summary>
        void NotifyStopping();

        /// <summary>Sends <c>STATUS=</c> with the specified status text.</summary>
        /// <param name="status">Human-readable status (must not contain newlines).</param>
        void NotifyStatus(string status);

        /// <summary>Sends <c>WATCHDOG=1</c>.</summary>
        void NotifyWatchdog();
    }
}
