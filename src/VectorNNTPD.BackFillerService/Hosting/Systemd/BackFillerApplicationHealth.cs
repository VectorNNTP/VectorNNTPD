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
    /// When a not-after is recorded with <see cref="MarkCertificateAvailable"/>, each
    /// <see cref="IsHealthyForWatchdog"/> read compares it to the injected clock so expiry is not sticky
    /// across a stale observation snapshot.
    /// </remarks>
    internal sealed class BackFillerApplicationHealth : IApplicationHealth
    {
        /// <summary>Clock used to evaluate recorded certificate not-after against current time.</summary>
        private readonly TimeProvider _time;

        /// <summary>Non-zero after <see cref="MarkStarted"/>.</summary>
        private int _started;

        /// <summary>Non-zero after <see cref="MarkStopping"/>.</summary>
        private int _stopping;

        /// <summary>Non-zero after <see cref="MarkUnexpectedTermination"/>.</summary>
        private int _unexpectedTermination;

        /// <summary>
        /// Non-zero while the published certificate is missing, unusable, or explicitly marked unavailable.
        /// Cleared by <see cref="MarkCertificateAvailable"/>. Expiry after a recorded not-after is checked
        /// separately via <see cref="_certificateNotAfterUtcTicks"/>.
        /// </summary>
        private int _certificateUnavailable;

        /// <summary>
        /// UTC ticks of the published certificate's not-after when keep-alives were last cleared.
        /// Zero means no expiry bound (tests that mark available without a not-after, or after unavailable).
        /// </summary>
        private long _certificateNotAfterUtcTicks;

        /// <summary>Creates health that evaluates certificate expiry with <paramref name="time"/>.</summary>
        /// <param name="time">
        /// Clock for not-after checks. Null uses <see cref="TimeProvider.System"/>.
        /// Production registers <see cref="TimeProvider.System"/>; tests may inject a manual clock.
        /// </param>
        public BackFillerApplicationHealth(TimeProvider? time = null)
        {
            _time = time ?? TimeProvider.System;
        }

        /// <inheritdoc />
        public bool IsHealthyForWatchdog =>
            Volatile.Read(ref _started) == 1
            && Volatile.Read(ref _stopping) == 0
            && Volatile.Read(ref _unexpectedTermination) == 0
            && Volatile.Read(ref _certificateUnavailable) == 0
            && CertificateNotExpired();

        /// <summary>
        /// Raised after <see cref="MarkCertificateAvailable"/> clears the certificate block.
        /// Subscribers may retry deferred work such as systemd <c>READY=1</c>.
        /// </summary>
        internal event Action? WatchdogHealthMayHaveImproved;

        /// <summary>Stops watchdog keep-alives until <see cref="MarkCertificateAvailable"/>.</summary>
        /// <remarks>
        /// Reversible. Does not mark unexpected termination and does not stop the host by itself.
        /// Sets the unavailable flag before clearing the recorded not-after so a concurrent
        /// <see cref="IsHealthyForWatchdog"/> read cannot observe a cleared expiry bound while still available
        /// (which would briefly treat an expired certificate as healthy).
        /// </remarks>
        internal void MarkCertificateUnavailable()
        {
            Interlocked.Exchange(ref _certificateUnavailable, 1);
            Volatile.Write(ref _certificateNotAfterUtcTicks, 0);
        }

        /// <summary>Allows watchdog keep-alives again after a usable certificate is published.</summary>
        /// <param name="notAfter">
        /// Exclusive UTC end of validity for the published material. Null leaves no expiry bound on this flag
        /// (callers that only exercise start/stop health). Production certificate observations always pass the
        /// material not-after so keep-alives stop when the clock reaches expiry without a new observation.
        /// </param>
        /// <remarks>
        /// Does not clear <see cref="_stopping"/> or <see cref="_unexpectedTermination"/>.
        /// Writes the not-after before clearing the unavailable flag so a concurrent health read cannot
        /// observe available without the replacement expiry bound.
        /// Raises <see cref="WatchdogHealthMayHaveImproved"/> after clearing the certificate block.
        /// </remarks>
        internal void MarkCertificateAvailable(DateTimeOffset? notAfter = null)
        {
            Volatile.Write(ref _certificateNotAfterUtcTicks, notAfter?.UtcTicks ?? 0);
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

        /// <summary>
        /// True when no not-after was recorded, or the clock is still before that instant.
        /// </summary>
        private bool CertificateNotExpired()
        {
            var notAfterTicks = Volatile.Read(ref _certificateNotAfterUtcTicks);
            if (notAfterTicks == 0)
            {
                return true;
            }

            return _time.GetUtcNow().UtcTicks < notAfterTicks;
        }
    }
}
