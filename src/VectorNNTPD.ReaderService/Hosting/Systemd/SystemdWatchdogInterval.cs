namespace VectorNNTPD.ReaderService.Hosting.Systemd
{
    /// <summary>
    /// Calculates systemd watchdog heartbeat intervals from the systemd-provided deadline.
    /// </summary>
    public static class SystemdWatchdogInterval
    {
        /// <summary>
        /// Derives a heartbeat interval that leaves margin before the systemd watchdog deadline.
        /// </summary>
        /// <param name="watchdogTimeout">Deadline supplied by systemd (<c>WATCHDOG_USEC</c>).</param>
        /// <param name="fraction">Fraction of the deadline between heartbeats; must be in (0, 1).</param>
        /// <returns>The heartbeat interval.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when inputs are invalid.</exception>
        public static TimeSpan Calculate(TimeSpan watchdogTimeout, double fraction = 0.5)
        {
            if (watchdogTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(watchdogTimeout), watchdogTimeout, "Watchdog timeout must be positive.");
            }

            if (double.IsNaN(fraction) || fraction is <= 0 or >= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "Fraction must be in the open interval (0, 1).");
            }

            var ticks = (long)Math.Floor(watchdogTimeout.Ticks * fraction);
            if (ticks < TimeSpan.TicksPerMillisecond)
            {
                ticks = TimeSpan.TicksPerMillisecond;
            }

            // Always leave at least 1ms margin before the deadline.
            var maxTicks = Math.Max(TimeSpan.TicksPerMillisecond, watchdogTimeout.Ticks - TimeSpan.TicksPerMillisecond);
            if (ticks > maxTicks)
            {
                ticks = maxTicks;
            }

            return TimeSpan.FromTicks(ticks);
        }
    }
}
