using Microsoft.Extensions.Configuration;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Storage;

namespace VectorNNTPD.StorageService.Logging
{

    /// <summary>
    /// Resolves <see cref="StorageServerOptions.LogDir"/> into the Serilog File path before
    /// <c>ReadFrom.Configuration</c>. Operational File/Async settings live in <c>VectorNNTPD.StorageService.json</c>.
    /// </summary>
    /// <remarks>
    /// Serilog.Settings.Configuration cannot expand <c>StorageServer:LogDir</c> into <c>path</c>.
    /// This type creates the directory and overwrites the File sink path so the JSON
    /// placeholder is never the runtime directory. It does not add a second File sink.
    /// </remarks>
    internal static class StorageServerFileLogging
    {
        /// <summary>Serilog rolling path token: <c>{entry assembly name}-yyyyMMdd.log</c>.</summary>
        public const string RollingPathSuffix = "-.log";

        /// <summary>
        /// Suffix appended when a completed log is gzip-compressed: <c>{original-file-name}.gz</c>.
        /// </summary>
        public const string GzipArchiveSuffix = ".gz";

        /// <summary>Console-sink fallback minimum when <c>Serilog:WriteTo</c> is omitted.</summary>
        public const Serilog.Events.LogEventLevel ConsoleMinimumLevel = Serilog.Events.LogEventLevel.Debug;

        /// <summary>
        /// Resolves <paramref name="logDir"/> through Common
        /// <see cref="ApplicationLocalPath.ResolveApplicationLocalPath"/> against
        /// <paramref name="applicationBaseDirectory"/> (default
        /// <see cref="AppContext.BaseDirectory"/>). Absolute paths stay absolute.
        /// </summary>
        public static string ResolveDirectory(string? logDir, string? applicationBaseDirectory = null)
        {
            var configured = string.IsNullOrWhiteSpace(logDir)
                ? StorageServerOptions.DefaultLogDir
                : logDir.Trim();
            return ApplicationLocalPath.ResolveApplicationLocalPath(
                configured,
                applicationBaseDirectory ?? AppContext.BaseDirectory);
        }

        /// <summary>
        /// Builds the Serilog rolling path <c>{logDir}/{applicationName}-.log</c>.
        /// </summary>
        /// <remarks>
        /// <paramref name="applicationName"/> must be a single relative segment so it cannot escape
        /// <paramref name="logDir"/> through a rooted or multi-segment value.
        /// </remarks>
        public static string RollingFilePath(string logDir, string applicationName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(logDir);
            // Validate the configured name before appending the rolling suffix so ".." cannot become "..-.log".
            var name = StorageRelativePath.RequireSingleRelativeSegment(applicationName);
            return StorageRelativePath.CombineUnder(logDir, name + RollingPathSuffix);
        }

        /// <summary>
        /// Archive file name produced when a completed log is gzip-compressed.
        /// </summary>
        public static string GzipArchiveFileName(string rolledLogPath) =>
            Path.GetFileName(rolledLogPath) + GzipArchiveSuffix;

        /// <summary>
        /// Creates <see cref="StorageServerOptions.LogDir"/> and binds the resolved File path over the JSON placeholder.
        /// </summary>
        public static void BindResolvedFilePath(
            ConfigurationManager configuration,
            string? applicationBaseDirectory = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var logDir = ResolveDirectory(
                configuration[$"{StorageServerOptions.SectionName}:{nameof(StorageServerOptions.LogDir)}"],
                applicationBaseDirectory);
            Directory.CreateDirectory(logDir);

            var path = RollingFilePath(logDir, ApplicationJsonConfiguration.EntryAssemblyName);
            var pathKey = FindConfiguredFilePathKey(configuration);
            if (pathKey is null)
            {
                return;
            }

            configuration.AddInMemoryCollection(new Dictionary<string, string?> { [pathKey] = path });
        }

        private static string? FindConfiguredFilePathKey(IConfiguration configuration)
        {
            foreach (var writeTo in configuration.GetSection("Serilog:WriteTo").GetChildren())
            {
                if (!string.Equals(writeTo["Name"], "Async", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var inner in writeTo.GetSection("Args:configure").GetChildren())
                {
                    if (string.Equals(inner["Name"], "File", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"{inner.Path}:Args:path";
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// In-memory Serilog File/Async keys matching production <c>VectorNNTPD.StorageService.json</c> (path is a placeholder).
        /// </summary>
        public static Dictionary<string, string?> AsyncFileWriteToKeys(int writeToIndex = 1) => new()
        {
            [$"Serilog:WriteTo:{writeToIndex}:Name"] = "Async",
            [$"Serilog:WriteTo:{writeToIndex}:Args:bufferSize"] = "50000",
            [$"Serilog:WriteTo:{writeToIndex}:Args:blockWhenFull"] = "true",
            [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Name"] = "File",
            [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:path"] =
                "/logs/" + ApplicationJsonConfiguration.EntryAssemblyName + "-.log",
            [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:restrictedToMinimumLevel"] = "Debug",
            [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:outputTemplate"] =
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
            [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:fileSizeLimitBytes"] = null,
            [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:buffered"] = "true",
            [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:flushToDiskInterval"] = "00:00:01",
            [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:rollingInterval"] = "Day",
            [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:rollOnFileSizeLimit"] = "false",
            [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:retainedFileCountLimit"] = "14",
        };
    }
}
