namespace VectorNNTPD.BackFillerService.Networking
{
    /// <summary>
    /// TCP keepalive probe timings applied with <c>SO_KEEPALIVE</c>.
    /// </summary>
    /// <remarks>
    /// Defaults are chosen so dead-path detection begins and progresses before the default
    /// NNTP <c>CommandTimeout</c> (30 s) and listener <c>IoProgressTimeout</c> (60 s):
    /// idle <see cref="DefaultTimeSeconds"/>, then up to <see cref="DefaultRetryCount"/> probes
    /// spaced by <see cref="DefaultIntervalSeconds"/> (about 25 s worst case to drop).
    /// Values are seconds, matching the .NET <c>TcpKeepAliveTime</c>, <c>TcpKeepAliveInterval</c>,
    /// and <c>TcpKeepAliveRetryCount</c> socket options.
    /// </remarks>
    /// <param name="TimeSeconds">Idle seconds before the first keepalive probe.</param>
    /// <param name="IntervalSeconds">Seconds between consecutive probes.</param>
    /// <param name="RetryCount">Number of unanswered probes before the stack drops the connection.</param>
    internal readonly record struct TcpKeepAliveSettings(int TimeSeconds, int IntervalSeconds, int RetryCount)
    {
        /// <summary>Default idle seconds before the first probe.</summary>
        internal const int DefaultTimeSeconds = 10;

        /// <summary>Default seconds between probes.</summary>
        internal const int DefaultIntervalSeconds = 5;

        /// <summary>Default unanswered probe count before the connection is dropped.</summary>
        internal const int DefaultRetryCount = 3;

        /// <summary>Smallest accepted idle, interval, or retry value.</summary>
        internal const int MinimumPositive = 1;

        /// <summary>Largest accepted idle time in seconds (two hours).</summary>
        internal const int MaximumTimeSeconds = 7200;

        /// <summary>Largest accepted probe interval in seconds.</summary>
        internal const int MaximumIntervalSeconds = 300;

        /// <summary>Largest accepted retry count.</summary>
        internal const int MaximumRetryCount = 20;

        /// <summary>Production and configuration default timings.</summary>
        internal static TcpKeepAliveSettings Default { get; } = new(
            DefaultTimeSeconds,
            DefaultIntervalSeconds,
            DefaultRetryCount);

        /// <summary>
        /// Throws when any field is outside the accepted ranges used by configuration validation.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">A field is outside its documented range.</exception>
        internal void Validate()
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(TimeSeconds, MinimumPositive);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(TimeSeconds, MaximumTimeSeconds);
            ArgumentOutOfRangeException.ThrowIfLessThan(IntervalSeconds, MinimumPositive);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(IntervalSeconds, MaximumIntervalSeconds);
            ArgumentOutOfRangeException.ThrowIfLessThan(RetryCount, MinimumPositive);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(RetryCount, MaximumRetryCount);
        }
    }
}
