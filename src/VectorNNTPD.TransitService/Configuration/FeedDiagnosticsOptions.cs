using System.ComponentModel.DataAnnotations;

namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// Temporary opt-in real-feed observability under <c>Nntpd:FeedDiagnostics</c>.
    /// </summary>
    /// <remarks>
    /// Off by default. When disabled, the reporter does not run and session probes are not
    /// attached. Enable with <c>Nntpd:FeedDiagnostics:Enabled</c> or environment variable
    /// <see cref="EnvironmentVariableName"/>. This is diagnostic instrumentation, not a
    /// permanent per-article log path.
    /// </remarks>
    public sealed class FeedDiagnosticsOptions
    {
        /// <summary>Environment variable that enables feed diagnostics (<c>VECTORNNTP_FEED_DIAGNOSTICS</c>).</summary>
        public const string EnvironmentVariableName = "VECTORNNTP_FEED_DIAGNOSTICS";

        /// <summary>Default snapshot interval in seconds.</summary>
        public const int DefaultIntervalSeconds = 5;

        /// <summary>Minimum accepted interval in seconds.</summary>
        public const int MinIntervalSeconds = 1;

        /// <summary>Maximum accepted interval in seconds.</summary>
        public const int MaxIntervalSeconds = 60;

        /// <summary>Gets or sets whether the periodic feed snapshot reporter is enabled.</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the snapshot interval in seconds.
        /// </summary>
        /// <remarks>Default is <c>10</c>. Valid range is <c>1–60</c>.</remarks>
        [Range(MinIntervalSeconds, MaxIntervalSeconds)]
        public int IntervalSeconds { get; set; } = DefaultIntervalSeconds;

        /// <summary>Gets or sets whether each snapshot includes compact per-session lines.</summary>
        public bool IncludeSessions { get; set; } = true;

        /// <summary>
        /// Returns whether feed diagnostics should run for <paramref name="options"/>.
        /// The environment variable wins when set to a truthy value.
        /// </summary>
        public static bool ResolveEnabled(NntpdOptions? options)
        {
            if (IsEnvironmentEnabled())
            {
                return true;
            }

            return options?.FeedDiagnostics?.Enabled == true;
        }

        /// <summary>Returns whether <see cref="EnvironmentVariableName"/> is a truthy value.</summary>
        public static bool IsEnvironmentEnabled()
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
