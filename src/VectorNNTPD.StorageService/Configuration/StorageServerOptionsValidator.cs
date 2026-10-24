using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.StorageService.Configuration
{

    /// <summary>
    /// Validates <see cref="StorageServerOptions"/> at bind / startup time.
    /// </summary>
    /// <remarks>
    /// Does not bind sockets, create directories, or connect to Cloudflare.
    /// Failure messages never include secret values.
    /// </remarks>
    public sealed class StorageServerOptionsValidator : IValidateOptions<StorageServerOptions>
    {
        private readonly string? _applicationBaseDirectory;

        /// <summary>
        /// Validates options. Relative directories resolve against <see cref="AppContext.BaseDirectory"/>.
        /// </summary>
        public StorageServerOptionsValidator()
            : this(applicationBaseDirectory: null)
        {
        }

        /// <summary>
        /// Validates options. Relative directories resolve against
        /// <paramref name="applicationBaseDirectory"/>, or <see cref="AppContext.BaseDirectory"/> when
        /// that argument is null or whitespace. This is the same rule as
        /// <see cref="ApplicationLocalPath.ResolveApplicationLocalPath"/>.
        /// </summary>
        /// <param name="applicationBaseDirectory">Application binary directory. Not the process working directory.</param>
        internal StorageServerOptionsValidator(string? applicationBaseDirectory)
        {
            _applicationBaseDirectory = applicationBaseDirectory;
        }

        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, StorageServerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var failures = new List<string>();
            ValidateIdentity(options, failures);
            ValidateBindPorts(options, failures);
            ValidateAcme(options, failures);
            ValidateDirectories(options, failures, _applicationBaseDirectory);
            ValidateLifecycle(options, failures);
            ValidateListener(options, failures);
            ValidateSystemd(options, failures);

            return failures.Count > 0
                ? ValidateOptionsResult.Fail(failures)
                : ValidateOptionsResult.Success;
        }

        private static void ValidateSystemd(StorageServerOptions options, List<string> failures)
        {
            if (options.Systemd is null)
            {
                failures.Add($"{nameof(StorageServerOptions.Systemd)} must be provided.");
                return;
            }

            var fraction = options.Systemd.WatchdogIntervalFraction;
            if (double.IsNaN(fraction) || fraction is <= 0 or >= 1)
            {
                failures.Add(
                    $"{nameof(StorageServerOptions.Systemd)}.{nameof(StorageServerSystemdOptions.WatchdogIntervalFraction)} must be in the open interval (0, 1).");
            }
            else if (fraction is < 0.05 or > 0.9)
            {
                failures.Add(
                    $"{nameof(StorageServerOptions.Systemd)}.{nameof(StorageServerSystemdOptions.WatchdogIntervalFraction)} must be between 0.05 and 0.9 inclusive.");
            }
        }

        private static void ValidateIdentity(StorageServerOptions options, List<string> failures)
        {
            if (options.ServerId is not { } serverId)
            {
                failures.Add(ServerIdRules.Validate(null, "StorageServer:ServerId")!);
                return;
            }

            var serverIdFailure = ServerIdRules.Validate(serverId, "StorageServer:ServerId");
            if (serverIdFailure is not null)
            {
                failures.Add(serverIdFailure);
                return;
            }

            var hostLabel = ApplicationFqdn.FormatHostLabel(StorageServerOptions.ApplicationPrefix, serverId);
            if (!StorageServerIdentity.IsValidDnsLabel(hostLabel))
            {
                failures.Add("StorageServer generated host label is not a valid DNS label.");
            }
        }

        private static void ValidateBindPorts(StorageServerOptions options, List<string> failures)
        {
            if (options.BindPort is < 0 or > 65535)
            {
                failures.Add("StorageServer:BindPort must be an integer in the range 0–65535 (0 = unused; cleartext is never listened on).");
            }

            if (options.BindPortTls is null or < 1 or > 65535)
            {
                failures.Add(
                    "StorageServer:BindPortTls is required and must be an integer in the range 1–65535 because StorageServer is TLS-only. There is no cleartext listener fallback.");
            }
        }

        private static void ValidateAcme(StorageServerOptions options, List<string> failures)
        {
            // Directory URL, renewal days, zone id, and DNS suffix come from nntpsharedconfig.
            if (string.IsNullOrWhiteSpace(options.AcmeStateDir) && string.IsNullOrWhiteSpace(options.CertificateDirectory))
            {
                failures.Add("StorageServer:AcmeStateDir must be a non-empty filesystem path.");
            }
        }

        private static void ValidateDirectories(
            StorageServerOptions options,
            List<string> failures,
            string? applicationBaseDirectory)
        {
            ValidateFilesystemPath(
                options.LogDir,
                "StorageServer:LogDir",
                failures,
                applicationBaseDirectory);
            ValidateFilesystemPath(
                options.DbDir,
                "StorageServer:DbDir",
                failures,
                applicationBaseDirectory);
            ValidateDbDirSeparatedFromSpools(options, failures, applicationBaseDirectory);
            ValidateSegmentPools(options.SegmentPools, failures, applicationBaseDirectory);
            ValidateSpoolPersistence(options.SpoolPersistence, failures);
        }

        /// <summary>Validates worker limits and the queued-byte budget.</summary>
        /// <param name="persistence">Persistence options. Null is a missing section.</param>
        /// <param name="failures">Validation messages.</param>
        private static void ValidateSpoolPersistence(SpoolPersistenceOptions? persistence, List<string> failures)
        {
            if (persistence is null)
            {
                failures.Add("StorageServer:SpoolPersistence is required.");
                return;
            }

            if (persistence.MaxQueuedBytes is < 1_048_576 or > 8L * 1024 * 1024 * 1024)
            {
                failures.Add("StorageServer:SpoolPersistence:MaxQueuedBytes must be from 1 MiB through 8 GiB.");
            }

            if (persistence.MinWorkers is < 1 or > 32)
            {
                failures.Add("StorageServer:SpoolPersistence:MinWorkers must be from 1 through 32.");
            }

            if (persistence.MaxWorkers < persistence.MinWorkers || persistence.MaxWorkers > 64)
            {
                failures.Add("StorageServer:SpoolPersistence:MaxWorkers must be from MinWorkers through 64.");
            }

            if (persistence.MaxInFlightPerSpool < 1 || persistence.MaxInFlightPerSpool > persistence.MaxWorkers)
            {
                failures.Add("StorageServer:SpoolPersistence:MaxInFlightPerSpool must be from 1 through MaxWorkers.");
            }

            if (persistence.ScaleUpQueueDepth < 1)
            {
                failures.Add("StorageServer:SpoolPersistence:ScaleUpQueueDepth must be at least 1.");
            }

            if (persistence.ScaleDownQueueDepth < 0 || persistence.ScaleDownQueueDepth >= persistence.ScaleUpQueueDepth)
            {
                failures.Add("StorageServer:SpoolPersistence:ScaleDownQueueDepth must be from 0 through ScaleUpQueueDepth - 1.");
            }

            if (persistence.ScaleDwellMilliseconds is < 100 or > 60_000)
            {
                failures.Add("StorageServer:SpoolPersistence:ScaleDwellMilliseconds must be from 100 through 60000.");
            }

            if (persistence.ControlIntervalMilliseconds < 50 || persistence.ControlIntervalMilliseconds > persistence.ScaleDwellMilliseconds)
            {
                failures.Add("StorageServer:SpoolPersistence:ControlIntervalMilliseconds must be from 50 through ScaleDwellMilliseconds.");
            }

            if (persistence.ScaleUpAgeMilliseconds < 0)
            {
                failures.Add("StorageServer:SpoolPersistence:ScaleUpAgeMilliseconds must be at least 0.");
            }

            if (persistence.IneligibleRetryMilliseconds is < 10 or > 5_000)
            {
                failures.Add("StorageServer:SpoolPersistence:IneligibleRetryMilliseconds must be from 10 through 5000.");
            }
        }

        /// <summary>
        /// Rejects a database directory that is the same as a spool or nested with one.
        /// The check is lexical after canonicalization. It does not probe the physical device.
        /// </summary>
        /// <param name="options">Options being validated.</param>
        /// <param name="failures">Validation messages.</param>
        /// <param name="applicationBaseDirectory">Application binary directory used to resolve relative paths.</param>
        private static void ValidateDbDirSeparatedFromSpools(
            StorageServerOptions options,
            List<string> failures,
            string? applicationBaseDirectory)
        {
            if (string.IsNullOrWhiteSpace(options.DbDir) || options.SegmentPools?.Directories is null)
            {
                return;
            }

            string databaseDirectory;
            try
            {
                databaseDirectory = ApplicationLocalPath.ResolveApplicationLocalPath(options.DbDir.Trim(), applicationBaseDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            for (var index = 0; index < options.SegmentPools.Directories.Length; index++)
            {
                var entry = options.SegmentPools.Directories[index];
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                string spool;
                try
                {
                    spool = ApplicationLocalPath.ResolveApplicationLocalPath(entry.Trim(), applicationBaseDirectory);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }

                if (PathsOverlap(databaseDirectory, spool))
                {
                    failures.Add(
                        "StorageServer:DbDir must not be the same directory as StorageServer:SegmentPools:Directories:"
                        + index
                        + ", nested inside it, or a parent of it. The article index does not fall back onto a spool volume.");
                }
            }
        }

        /// <summary>Compares two canonical paths for equality or nesting.</summary>
        /// <param name="left">First canonical path.</param>
        /// <param name="right">Second canonical path.</param>
        /// <returns><see langword="true"/> when the paths are equal or one contains the other.</returns>
        private static bool PathsOverlap(string left, string right)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(left, right, comparison))
            {
                return true;
            }

            var leftPrefix = left.EndsWith(Path.DirectorySeparatorChar) ? left : left + Path.DirectorySeparatorChar;
            if (right.StartsWith(leftPrefix, comparison))
            {
                return true;
            }

            var rightPrefix = right.EndsWith(Path.DirectorySeparatorChar) ? right : right + Path.DirectorySeparatorChar;
            return left.StartsWith(rightPrefix, comparison);
        }

        private static void ValidateSegmentPools(
            SegmentPoolsOptions? pools,
            List<string> failures,
            string? applicationBaseDirectory)
        {
            if (pools is null)
            {
                failures.Add("StorageServer:SegmentPools is required.");
                return;
            }

            var percentCount = failures.Count;
            ValidatePercent("StorageServer:SegmentPools:LowPercent", pools.LowPercent, failures);
            ValidatePercent("StorageServer:SegmentPools:HighPercent", pools.HighPercent, failures);
            ValidatePercent("StorageServer:SegmentPools:CriticalPercent", pools.CriticalPercent, failures);
            if (failures.Count == percentCount
                && (pools.LowPercent >= pools.HighPercent || pools.HighPercent >= pools.CriticalPercent))
            {
                failures.Add(
                    "StorageServer:SegmentPools requires LowPercent < HighPercent < CriticalPercent "
                    + $"(LowPercent={pools.LowPercent}, HighPercent={pools.HighPercent}, CriticalPercent={pools.CriticalPercent}).");
            }

            if (pools.Directories is null || pools.Directories.Length == 0)
            {
                failures.Add("StorageServer:SegmentPools:Directories must contain at least one directory.");
                return;
            }

            var seen = new HashSet<string>(PathComparer);
            for (var index = 0; index < pools.Directories.Length; index++)
            {
                var key = $"StorageServer:SegmentPools:Directories:{index}";
                var entry = pools.Directories[index];
                if (string.IsNullOrWhiteSpace(entry))
                {
                    failures.Add($"{key} is required and cannot be empty.");
                    continue;
                }

                var trimmed = entry.Trim();
                if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    failures.Add($"{key} contains invalid path characters.");
                    continue;
                }

                string resolved;
                try
                {
                    resolved = ApplicationLocalPath.ResolveApplicationLocalPath(trimmed, applicationBaseDirectory);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    failures.Add($"{key} is not a valid filesystem path.");
                    continue;
                }

                if (!seen.Add(resolved))
                {
                    failures.Add($"{key} duplicates another SegmentPools directory after canonicalization ('{resolved}').");
                }
            }
        }

        private static void ValidatePercent(string key, int value, List<string> failures)
        {
            if (value is < 0 or > 100)
            {
                failures.Add($"{key} must be an integer in the inclusive range 0–100.");
            }
        }

        private static StringComparer PathComparer { get; } =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private static void ValidateFilesystemPath(
            string? path,
            string configurationKey,
            List<string> failures,
            string? applicationBaseDirectory)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                failures.Add($"{configurationKey} is required and cannot be empty.");
                return;
            }

            var trimmed = path.Trim();
            if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                failures.Add($"{configurationKey} contains invalid path characters.");
                return;
            }

            try
            {
                _ = ApplicationLocalPath.ResolveApplicationLocalPath(trimmed, applicationBaseDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                failures.Add($"{configurationKey} is not a valid filesystem path.");
            }
        }

        private static void ValidateLifecycle(StorageServerOptions options, List<string> failures)
        {
            if (string.IsNullOrWhiteSpace(options.ApplicationName))
            {
                failures.Add($"{nameof(StorageServerOptions.ApplicationName)} must be a non-empty string.");
            }
            else if (options.ApplicationName.Length > 128)
            {
                failures.Add($"{nameof(StorageServerOptions.ApplicationName)} must be 128 characters or fewer.");
            }

            if (options.GracefulShutdownTimeout < TimeSpan.FromSeconds(1))
            {
                failures.Add($"{nameof(StorageServerOptions.GracefulShutdownTimeout)} must be at least 1 second.");
            }

            if (options.GracefulShutdownTimeout > TimeSpan.FromHours(1))
            {
                failures.Add($"{nameof(StorageServerOptions.GracefulShutdownTimeout)} must not exceed 1 hour.");
            }

            if (options.StartupTimeout is { } startup)
            {
                if (startup < TimeSpan.FromSeconds(1))
                {
                    failures.Add($"{nameof(StorageServerOptions.StartupTimeout)} must be at least 1 second when set.");
                }

                if (startup > TimeSpan.FromHours(1))
                {
                    failures.Add($"{nameof(StorageServerOptions.StartupTimeout)} must not exceed 1 hour when set.");
                }
            }
        }

        private static void ValidateListener(StorageServerOptions options, List<string> failures)
        {
            var listener = options.Listener ?? new StorageServerListenerOptions();
            if (listener.ParserAccumulationMaxBytes < 32768)
            {
                failures.Add("StorageServer:Listener:ParserAccumulationMaxBytes must be between 32768 and 2147483647.");
            }

            if (listener.TlsHandshakeTimeoutSeconds is < 1 or > 300)
            {
                failures.Add("StorageServer:Listener:TlsHandshakeTimeoutSeconds must be between 1 and 300.");
            }

            if (listener.IoProgressTimeoutSeconds is < 1 or > 600)
            {
                failures.Add("StorageServer:Listener:IoProgressTimeoutSeconds must be between 1 and 600.");
            }

            if (listener.ArticleReceiveTimeoutSeconds is < 30 or > 3600)
            {
                failures.Add("StorageServer:Listener:ArticleReceiveTimeoutSeconds must be between 30 and 3600.");
            }

            if (listener.MaxActiveConnections < 1)
            {
                failures.Add("StorageServer:Listener:MaxActiveConnections must be between 1 and 2147483647.");
            }
        }
    }
}
