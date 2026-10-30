using System.Runtime.CompilerServices;

namespace VectorNNTPD.Common.Networking.Transport
{
    /// <summary>
    /// DIAGNOSTIC-ONLY. Off-by-default TX write-granularity override.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Production default remains one <c>ConnectionByteTransport.WriteAsync</c> per Pipe
    /// segment. This type never changes Pipe thresholds, schedulers, Channel, TCS,
    /// NetworkStream, or socket options. It exists only so a SPEEDTEST sweep can
    /// copy consecutive Pipe segments into one larger transport write.
    /// </para>
    /// <para>
    /// Activate only with <c>VECTORNNTP_TX_WRITE_GRANULARITY</c> set to an allowlisted
    /// size (<c>64KiB</c> … <c>4MiB</c>), or via <see cref="Set"/> / <see cref="SetFor"/>
    /// in tests. Unset / empty / <c>0</c> / <c>false</c> leave the production path
    /// unchanged. Any other value is rejected so a typo cannot silently apply a large
    /// aggregate.
    /// </para>
    /// </remarks>
    internal static class TxWriteGranularityExperiment
    {
        /// <summary>Environment variable that selects an experimental TX write aggregate size.</summary>
        public const string EnvironmentVariableName = "VECTORNNTP_TX_WRITE_GRANULARITY";

        /// <summary>Allowlisted aggregate sizes (bytes). 64 KiB is the instrumented control.</summary>
        public static readonly int[] AllowedTargetBytes =
        [
            64 * 1024,
            128 * 1024,
            256 * 1024,
            512 * 1024,
            1024 * 1024,
            4 * 1024 * 1024,
        ];

        private static readonly ConditionalWeakTable<ConnectionByteTransport, StrongBox<int>> PerTransport = [];

        private static OverrideState? _state;

        static TxWriteGranularityExperiment()
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            if (string.IsNullOrWhiteSpace(value) ||
                value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!TryParseAllowed(value, out var target))
            {
                throw new InvalidOperationException(
                    EnvironmentVariableName +
                    " must be an allowlisted size (64KiB, 128KiB, 256KiB, 512KiB, 1MiB, 4MiB) or the equivalent byte count.");
            }

            Volatile.Write(ref _state, new OverrideState(target));
        }

        /// <summary>Gets whether a process-wide experimental aggregate size is configured.</summary>
        public static bool IsConfigured => Volatile.Read(ref _state) is not null;

        /// <summary>Gets the process-wide target bytes, or <see langword="null"/> when inactive.</summary>
        public static int? ConfiguredTargetBytes => Volatile.Read(ref _state)?.TargetBytes;

        /// <summary>Test/experiment hook. <paramref name="targetBytes"/> must be allowlisted.</summary>
        public static void Set(int targetBytes)
        {
            if (!IsAllowed(targetBytes))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targetBytes),
                    targetBytes,
                    "TX write-granularity experiment accepts only 64KiB…4MiB allowlisted sizes.");
            }

            Volatile.Write(ref _state, new OverrideState(targetBytes));
        }

        /// <summary>Clears the process-wide override (tests). Production default applies.</summary>
        public static void Clear() => Volatile.Write(ref _state, null);

        /// <summary>
        /// Test-only per-transport override. Does not affect other connections.
        /// </summary>
        public static void SetFor(ConnectionByteTransport transport, int targetBytes)
        {
            ArgumentNullException.ThrowIfNull(transport);
            if (!IsAllowed(targetBytes))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targetBytes),
                    targetBytes,
                    "TX write-granularity experiment accepts only 64KiB…4MiB allowlisted sizes.");
            }

            PerTransport.AddOrUpdate(transport, new StrongBox<int>(targetBytes));
        }

        /// <summary>Removes a per-transport override (tests).</summary>
        public static void ClearFor(ConnectionByteTransport transport)
        {
            ArgumentNullException.ThrowIfNull(transport);
            PerTransport.Remove(transport);
        }

        /// <summary>
        /// Resolves the aggregate target for this send pump. Per-transport overrides win
        /// over the process-wide setting. Returns <see langword="false"/> when inactive.
        /// </summary>
        public static bool TryResolveTarget(ConnectionByteTransport? transport, out int targetBytes)
        {
            if (transport is not null && PerTransport.TryGetValue(transport, out var box))
            {
                targetBytes = box.Value;
                return true;
            }

            return TryGetTarget(out targetBytes);
        }

        /// <summary>Gets the process-wide target, or <see langword="false"/> when inactive.</summary>
        public static bool TryGetTarget(out int targetBytes)
        {
            var configured = Volatile.Read(ref _state);
            if (configured is null)
            {
                targetBytes = 0;
                return false;
            }

            targetBytes = configured.TargetBytes;
            return true;
        }

        /// <summary>Parses an allowlisted size token or byte count.</summary>
        public static bool TryParseAllowed(string value, out int targetBytes)
        {
            targetBytes = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim();
            if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var raw) &&
                IsAllowed(raw))
            {
                targetBytes = raw;
                return true;
            }

            if (TryParseSizeToken(trimmed, out var parsed) && IsAllowed(parsed))
            {
                targetBytes = parsed;
                return true;
            }

            return false;
        }

        /// <summary>Returns whether <paramref name="targetBytes"/> is on the experiment allowlist.</summary>
        public static bool IsAllowed(int targetBytes)
        {
            for (var i = 0; i < AllowedTargetBytes.Length; i++)
            {
                if (AllowedTargetBytes[i] == targetBytes)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseSizeToken(string value, out int bytes)
        {
            bytes = 0;
            var span = value.AsSpan().Trim();
            var suffixLen = 0;
            int multiplier;
            if (span.EndsWith("KiB", StringComparison.OrdinalIgnoreCase))
            {
                suffixLen = 3;
                multiplier = 1024;
            }
            else if (span.EndsWith("MiB", StringComparison.OrdinalIgnoreCase))
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
            if (!int.TryParse(number, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count) ||
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

        private sealed class OverrideState(int targetBytes)
        {
            public int TargetBytes { get; } = targetBytes;
        }
    }
}
