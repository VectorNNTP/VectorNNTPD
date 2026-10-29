using System.ComponentModel.DataAnnotations;

namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// Diagnostic limits for the VectorNNTP <c>SPEEDTEST</c> extension under <c>Nntpd:SpeedTest</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These bounds cap synthetic diagnostic traffic. They are not article-ingestion,
    /// HistoryDB, Redis, or Transit feed settings. <c>SPEEDTEST</c> does not open outbound
    /// peer sockets from <c>ConnectTo</c>.
    /// </para>
    /// <para>
    /// Defaults are sized for a short operator diagnostic, not a general-purpose traffic generator.
    /// </para>
    /// </remarks>
    public sealed class SpeedTestOptions
    {
        /// <summary>Default maximum test duration in seconds.</summary>
        public const int DefaultMaxDurationSeconds = 10;

        /// <summary>Minimum accepted duration in seconds.</summary>
        public const int MinDurationSeconds = 1;

        /// <summary>Maximum accepted duration in seconds.</summary>
        public const int MaxDurationSecondsLimit = 60;

        /// <summary>Default maximum payload bytes transferred in one test (64 MiB).</summary>
        public const long DefaultMaxBytes = 64L * 1024 * 1024;

        /// <summary>Minimum accepted byte limit (one payload line).</summary>
        public const long MinBytes = 1024;

        /// <summary>Maximum accepted byte limit (1 GiB).</summary>
        public const long MaxBytesLimit = 1024L * 1024 * 1024;

        /// <summary>Default maximum concurrent SPEEDTEST operations on this host.</summary>
        public const int DefaultMaxConcurrent = 2;

        /// <summary>Minimum accepted global concurrency.</summary>
        public const int MinConcurrent = 1;

        /// <summary>Maximum accepted global concurrency.</summary>
        public const int MaxConcurrentLimit = 8;

        /// <summary>Default maximum concurrent SPEEDTEST operations per Transit identifier.</summary>
        public const int DefaultMaxConcurrentPerPeer = 1;

        /// <summary>Minimum accepted per-peer concurrency.</summary>
        public const int MinConcurrentPerPeer = 1;

        /// <summary>Maximum accepted per-peer concurrency.</summary>
        public const int MaxConcurrentPerPeerLimit = 4;

        /// <summary>
        /// Gets or sets the maximum wall-clock duration of one SPEEDTEST payload transfer.
        /// </summary>
        /// <remarks>Default is <c>10</c>. Valid range is <c>1–60</c>.</remarks>
        [Range(MinDurationSeconds, MaxDurationSecondsLimit)]
        public int MaxDurationSeconds { get; set; } = DefaultMaxDurationSeconds;

        /// <summary>
        /// Gets or sets the maximum synthetic payload bytes written for one SPEEDTEST.
        /// </summary>
        /// <remarks>
        /// Default is 64 MiB. Valid range is <c>1024–1073741824</c>.
        /// Counts payload body octets (pattern lines including CRLF), not status lines.
        /// </remarks>
        [Range(MinBytes, MaxBytesLimit)]
        public long MaxBytes { get; set; } = DefaultMaxBytes;

        /// <summary>
        /// Gets or sets the maximum number of SPEEDTEST operations that may run at once on this host.
        /// </summary>
        /// <remarks>Default is <c>2</c>. Valid range is <c>1–8</c>.</remarks>
        [Range(MinConcurrent, MaxConcurrentLimit)]
        public int MaxConcurrent { get; set; } = DefaultMaxConcurrent;

        /// <summary>
        /// Gets or sets the maximum number of simultaneous SPEEDTEST operations for one Transit identifier.
        /// </summary>
        /// <remarks>Default is <c>1</c>. Valid range is <c>1–4</c>.</remarks>
        [Range(MinConcurrentPerPeer, MaxConcurrentPerPeerLimit)]
        public int MaxConcurrentPerPeer { get; set; } = DefaultMaxConcurrentPerPeer;

        /// <summary>Returns an immutable copy of the current limits.</summary>
        public SpeedTestLimits Snapshot() => new(this);
    }

    /// <summary>Immutable SPEEDTEST limits captured at acquire time.</summary>
    public readonly struct SpeedTestLimits
    {
        /// <summary>Initializes limits from <paramref name="options"/>.</summary>
        public SpeedTestLimits(SpeedTestOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            MaxDuration = TimeSpan.FromSeconds(options.MaxDurationSeconds);
            MaxBytes = options.MaxBytes;
            MaxConcurrent = options.MaxConcurrent;
            MaxConcurrentPerPeer = options.MaxConcurrentPerPeer;
        }

        /// <summary>Creates limits directly (tests).</summary>
        public SpeedTestLimits(TimeSpan maxDuration, long maxBytes, int maxConcurrent, int maxConcurrentPerPeer)
        {
            MaxDuration = maxDuration;
            MaxBytes = maxBytes;
            MaxConcurrent = maxConcurrent;
            MaxConcurrentPerPeer = maxConcurrentPerPeer;
        }

        /// <summary>Gets the maximum payload duration.</summary>
        public TimeSpan MaxDuration { get; }

        /// <summary>Gets the maximum payload bytes.</summary>
        public long MaxBytes { get; }

        /// <summary>Gets the host-wide concurrency cap.</summary>
        public int MaxConcurrent { get; }

        /// <summary>Gets the per-peer concurrency cap.</summary>
        public int MaxConcurrentPerPeer { get; }
    }
}
