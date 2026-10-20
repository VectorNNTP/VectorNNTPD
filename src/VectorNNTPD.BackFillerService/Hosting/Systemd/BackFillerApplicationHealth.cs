namespace VectorNNTPD.BackFillerService.Hosting.Systemd
{
    /// <summary>
    /// Host-lifetime health used for systemd watchdog decisions.
    /// </summary>
    /// <remarks>
    /// Readiness for watchdog purposes means all Generic Host hosted services started successfully, shutdown
    /// has not begun, no unexpected supervised-service termination has been recorded, and the published TLS
    /// certificate is not blocking keep-alives. Start, shutdown, and unexpected termination are sticky.
    /// <see cref="MarkCertificateUnavailable"/> is cleared by <see cref="MarkCertificateAvailable"/>.
    /// </remarks>
    internal sealed class BackFillerApplicationHealth : IApplicationHealth
    {
        /// <summary>Non-zero after <see cref="MarkStarted"/>.</summary>
        private int _started;

        /// <summary>Non-zero after <see cref="MarkStopping"/>.</summary>
        private int _stopping;

        /// <summary>Non-zero after <see cref="MarkUnexpectedTermination"/>.</summary>
        private int _unexpectedTermination;

        /// <summary>
        /// Non-zero while the published certificate is missing, unusable, expired, or inside the renewal window
        /// after a renewal that did not replace it. Cleared by <see cref="MarkCertificateAvailable"/>.
        /// </summary>
        private int _certificateUnavailable;

        /// <inheritdoc />
        public bool IsHealthyForWatchdog =>
            Volatile.Read(ref _started) == 1
            && Volatile.Read(ref _stopping) == 0
            && Volatile.Read(ref _unexpectedTermination) == 0
            && Volatile.Read(ref _certificateUnavailable) == 0;

        /// <summary>
        /// Raised after <see cref="MarkCertificateAvailable"/> clears the certificate block.
        /// Subscribers may retry deferred work such as systemd <c>READY=1</c>.
        /// </summary>
        internal event Action? WatchdogHealthMayHaveImproved;

        /// <summary>Stops watchdog keep-alives until <see cref="MarkCertificateAvailable"/>.</summary>
        /// <remarks>Reversible. Does not mark unexpected termination and does not stop the host by itself.</remarks>
        internal void MarkCertificateUnavailable() => Interlocked.Exchange(ref _certificateUnavailable, 1);

        /// <summary>Allows watchdog keep-alives again after a usable certificate is published.</summary>
        /// <remarks>
        /// Does not clear <see cref="_stopping"/> or <see cref="_unexpectedTermination"/>.
        /// Raises <see cref="WatchdogHealthMayHaveImproved"/> after clearing the certificate block.
        /// </remarks>
        internal void MarkCertificateAvailable()
        {
            Interlocked.Exchange(ref _certificateUnavailable, 0);
            WatchdogHealthMayHaveImproved?.Invoke();
        }

        /// <summary>Marks successful host start (all hosted services started).</summary>
        /// <remarks>Leaves <see cref="_stopping"/> and <see cref="_unexpectedTermination"/> unchanged.</remarks>
        internal void MarkStarted() => Interlocked.Exchange(ref _started, 1);

        /// <summary>Marks that graceful shutdown has begun.</summary>
        /// <remarks>Sticky. A later <see cref="MarkStarted"/> does not make <see cref="IsHealthyForWatchdog"/> true.</remarks>
        internal void MarkStopping() => Interlocked.Exchange(ref _stopping, 1);

        /// <summary>Marks an unexpected supervised-service termination.</summary>
        /// <remarks>
        /// Sticky. Callers do not distinguish a faulted execution from one that completed or was canceled.
        /// </remarks>
        internal void MarkUnexpectedTermination() => Interlocked.Exchange(ref _unexpectedTermination, 1);
    }
}
