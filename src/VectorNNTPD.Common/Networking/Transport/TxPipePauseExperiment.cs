namespace VectorNNTPD.Common.Networking.Transport
{
    /// <summary>
    /// DIAGNOSTIC-ONLY. Off-by-default TX output-pipe pause-threshold override.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Production default remains <see cref="NntpPipeOptions.PauseWriterThreshold"/> (64 KiB).
    /// This type never changes the input (RX) pipe. It exists only so a SPEEDTEST sweep can
    /// vary how far the TX producer may run ahead before <c>FlushAsync</c> backpressures.
    /// </para>
    /// <para>
    /// Activate only with <c>VECTORNNTP_TX_PIPE_PAUSE</c> set to an allowlisted size
    /// (<c>64KiB</c> … <c>8MiB</c>), or via <see cref="Set"/> in tests. Unset / empty / <c>0</c>
    /// / <c>false</c> leave production options unchanged. Any other value is rejected so a typo
    /// cannot silently apply a large buffer.
    /// </para>
    /// </remarks>
    internal static class TxPipePauseExperiment
    {
        /// <summary>Environment variable that selects an experimental TX pipe pause size.</summary>
        public const string EnvironmentVariableName = "VECTORNNTP_TX_PIPE_PAUSE";

        /// <summary>Allowlisted pause sizes (bytes). 64 KiB is the production control.</summary>
        public static readonly long[] AllowedPauseBytes =
        [
            64 * 1024,
            128 * 1024,
            256 * 1024,
            512 * 1024,
            1024 * 1024,
            2 * 1024 * 1024,
            4 * 1024 * 1024,
            8 * 1024 * 1024,
        ];

        private static OverrideState? _state;

        static TxPipePauseExperiment()
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            if (string.IsNullOrWhiteSpace(value) ||
                value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!TryParseAllowed(value, out var pause))
            {
                throw new InvalidOperationException(
                    EnvironmentVariableName +
                    " must be an allowlisted size (64KiB, 128KiB, 256KiB, 512KiB, 1MiB, 2MiB, 4MiB, 8MiB) or the equivalent byte count.");
            }

            Volatile.Write(ref _state, new OverrideState(pause));
        }

        /// <summary>Gets whether an experimental pause size is configured.</summary>
        public static bool IsConfigured => Volatile.Read(ref _state) is not null;

        /// <summary>Gets the configured pause bytes, or <see langword="null"/> when inactive.</summary>
        public static long? ConfiguredPauseBytes => Volatile.Read(ref _state)?.PauseBytes;

        /// <summary>Test/experiment hook. <paramref name="pauseBytes"/> must be allowlisted.</summary>
        public static void Set(long pauseBytes)
        {
            if (!IsAllowed(pauseBytes))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pauseBytes),
                    pauseBytes,
                    "TX pipe pause experiment accepts only 64KiB…8MiB allowlisted sizes.");
            }

            Volatile.Write(ref _state, new OverrideState(pauseBytes));
        }

        /// <summary>Clears the override (tests). Production default applies.</summary>
        public static void Clear() => Volatile.Write(ref _state, null);

        /// <summary>
        /// Resolves the TX output-pipe thresholds. When inactive, returns production 64 KiB / 32 KiB
        /// and <see langword="false"/>.
        /// </summary>
        public static bool TryGetEffective(out long pauseBytes, out long resumeBytes)
        {
            var configured = Volatile.Read(ref _state);
            if (configured is null)
            {
                pauseBytes = NntpPipeOptions.PauseWriterThreshold;
                resumeBytes = NntpPipeOptions.ResumeWriterThreshold;
                return false;
            }

            pauseBytes = configured.PauseBytes;
            resumeBytes = ResumeFor(configured.PauseBytes);
            return true;
        }

        /// <summary>Resume is half of pause, never below the production 32 KiB resume.</summary>
        public static long ResumeFor(long pauseBytes)
        {
            var half = pauseBytes / 2;
            return half < NntpPipeOptions.ResumeWriterThreshold
                ? NntpPipeOptions.ResumeWriterThreshold
                : half;
        }

        /// <summary>Parses an allowlisted size token or byte count.</summary>
        public static bool TryParseAllowed(string value, out long pauseBytes)
        {
            pauseBytes = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim();
            if (long.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var raw) &&
                IsAllowed(raw))
            {
                pauseBytes = raw;
                return true;
            }

            if (TryParseSizeToken(trimmed, out var parsed) && IsAllowed(parsed))
            {
                pauseBytes = parsed;
                return true;
            }

            return false;
        }

        /// <summary>Returns whether <paramref name="pauseBytes"/> is on the experiment allowlist.</summary>
        public static bool IsAllowed(long pauseBytes)
        {
            for (var i = 0; i < AllowedPauseBytes.Length; i++)
            {
                if (AllowedPauseBytes[i] == pauseBytes)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseSizeToken(string value, out long bytes)
        {
            bytes = 0;
            var span = value.AsSpan().Trim();
            var suffixLen = 0;
            long multiplier;
            if (span.EndsWith("KiB", StringComparison.OrdinalIgnoreCase) ||
                span.EndsWith("kib", StringComparison.OrdinalIgnoreCase))
            {
                suffixLen = 3;
                multiplier = 1024;
            }
            else if (span.EndsWith("MiB", StringComparison.OrdinalIgnoreCase) ||
                     span.EndsWith("mib", StringComparison.OrdinalIgnoreCase))
            {
                suffixLen = 3;
                multiplier = 1024 * 1024;
            }
            else if (span.Length > 0 && (span[^1] is 'k' or 'K'))
            {
                suffixLen = 1;
                multiplier = 1024;
            }
            else if (span.Length > 0 && (span[^1] is 'm' or 'M'))
            {
                suffixLen = 1;
                multiplier = 1024 * 1024;
            }
            else
            {
                return false;
            }

            var number = span[..^suffixLen];
            if (!long.TryParse(number, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count) ||
                count <= 0)
            {
                return false;
            }

            try
            {
                bytes = checked(count * multiplier);
            }
            catch (OverflowException)
            {
                return false;
            }

            return true;
        }

        private sealed class OverrideState(long pauseBytes)
        {
            public long PauseBytes { get; } = pauseBytes;
        }
    }
}
