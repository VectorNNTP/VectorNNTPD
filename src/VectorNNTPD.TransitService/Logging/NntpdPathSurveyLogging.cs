using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.File;
using VectorNNTPD.Common.Logging;
using VectorNNTPD.TransitService.Configuration;

namespace VectorNNTPD.TransitService.Logging
{
    /// <summary>
    /// Dedicated Serilog pipeline for the INN-style Path-survey (<c>inpaths</c>) file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Operational File/Async settings bind from <c>Serilog:Inpaths</c> using the same
    /// argument names as the application File sink (path, rolling, retention,
    /// buffering, <c>flushToDiskInterval</c>). The Path-survey line format is not read from configuration;
    /// the sink is always constructed with <see cref="InnPathSurveyTextFormatter"/>.
    /// </para>
    /// <para>
    /// This small bridge exists because Serilog.Settings.Configuration cannot attach
    /// a code-owned formatter without also exposing a user-editable
    /// <c>outputTemplate</c> or <c>formatter</c> string. Path resolution still uses
    /// <see cref="NntpdOptions.LogDir"/> via <see cref="NntpdFileLogging"/>.
    /// </para>
    /// <para>
    /// Production <see cref="PathSurveyFileSinkSettings.RetainedFileCountLimit"/> is 1,
    /// so only the active day is kept. When the day rolls, the completed file is handed
    /// to <see cref="ICompletedPathSurveyFileHandler"/> and then gzip-compressed.
    /// Retention then deletes that compressed day because it is outside the one-file limit.
    /// The handler opens the file with share-read/write/delete before compression, so it
    /// can keep reading after the uncompressed file is replaced. Handler failure is logged
    /// and does not skip gzip.
    /// </para>
    /// </remarks>
    public static class NntpdPathSurveyLogging
    {
        /// <summary>Serilog source context for Path-survey events. Not used by the application File/Console sinks.</summary>
        public const string SourceContext = "VectorNNTPD.TransitService.Inpaths";

        /// <summary>Configuration section that holds Path-survey File/Async operational arguments.</summary>
        public const string SectionName = "Serilog:Inpaths";

        /// <summary>Canonical rolling path token: <c>inpaths-yyyyMMdd.log</c>.</summary>
        public const string RollingFileName = "inpaths";

        /// <summary>Log event property for the canonical Path value.</summary>
        public const string PathProperty = "PathSurveyPath";

        /// <summary>
        /// Overwrites <c>Serilog:Inpaths:path</c> from <see cref="NntpdOptions.LogDir"/>
        /// so the JSON placeholder is never the runtime path.
        /// </summary>
        public static void BindResolvedPathSurveyPath(
            IConfiguration configuration,
            string? applicationBaseDirectory = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            if (configuration[SectionName + ":path"] is null)
            {
                return;
            }

            var logDir = NntpdFileLogging.ResolveDirectory(
                configuration[$"{NntpdOptions.SectionName}:{nameof(NntpdOptions.LogDir)}"],
                applicationBaseDirectory);
            Directory.CreateDirectory(logDir);
            var path = PathSurveyRollingFilePath(logDir);
            if (configuration is IConfigurationBuilder builder)
            {
                builder.AddInMemoryCollection(new Dictionary<string, string?> { [SectionName + ":path"] = path });
            }
        }

        /// <summary>Builds <c>{logDirectory}/inpaths-.log</c> for Serilog daily rolling.</summary>
        public static string PathSurveyRollingFilePath(string logDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
            return Path.Join(logDirectory, RollingFileName + NntpdFileLogging.RollingPathSuffix);
        }

        /// <summary>
        /// Reads File/Async operational settings from <c>Serilog:Inpaths</c>.
        /// The Path-survey formatter is never taken from configuration.
        /// </summary>
        public static PathSurveyFileSinkSettings ReadSettings(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var inpaths = configuration.GetSection(SectionName);
            var path = inpaths["path"];
            if (string.IsNullOrWhiteSpace(path))
            {
                path = PathSurveyRollingFilePath(
                    NntpdFileLogging.ResolveDirectory(
                        configuration[$"{NntpdOptions.SectionName}:{nameof(NntpdOptions.LogDir)}"]));
            }

            return new PathSurveyFileSinkSettings(
                path,
                ParseRolling(inpaths["rollingInterval"]),
                inpaths.GetValue<int?>("retainedFileCountLimit") ?? 1,
                inpaths.GetValue("buffered", true),
                inpaths.GetValue("rollOnFileSizeLimit", false),
                inpaths.GetValue<long?>("fileSizeLimitBytes"),
                inpaths.GetValue("bufferSize", 50000),
                inpaths.GetValue("blockWhenFull", true),
                inpaths.GetValue<TimeSpan?>("flushToDiskInterval"));
        }

        /// <summary>
        /// Creates a dedicated Serilog logger that writes only Path-survey lines.
        /// </summary>
        /// <param name="configuration">Application configuration containing <c>Serilog:Inpaths</c>.</param>
        /// <param name="completedFileHandler">
        /// Invoked with the uncompressed rolled file before gzip. Production
        /// enqueues the file for background ninpaths processing.
        /// </param>
        /// <param name="hookLogger">Logger for completed-file handler failures; must not be the Path-survey logger.</param>
        public static Serilog.Core.Logger CreateLogger(
            IConfiguration configuration,
            ICompletedPathSurveyFileHandler completedFileHandler,
            Microsoft.Extensions.Logging.ILogger hookLogger)
        {
            ArgumentNullException.ThrowIfNull(completedFileHandler);
            ArgumentNullException.ThrowIfNull(hookLogger);
            var settings = ReadSettings(configuration);
            Directory.CreateDirectory(
                Path.GetDirectoryName(Path.GetFullPath(settings.Path)) ?? NntpdOptions.DefaultLogDir);

            return new LoggerConfiguration()
                .MinimumLevel.Information()
                .Enrich.WithProperty(Serilog.Core.Constants.SourceContextPropertyName, SourceContext)
                .Filter.ByIncludingOnly(static e => e.Properties.ContainsKey(PathProperty))
                .WriteTo.Async(
                    a => a.DailyGzipFile(
                        new InnPathSurveyTextFormatter(),
                        settings.Path,
                        restrictedToMinimumLevel: LogEventLevel.Information,
                        fileSizeLimitBytes: settings.FileSizeLimitBytes,
                        buffered: settings.Buffered,
                        flushToDiskInterval: settings.FlushToDiskInterval,
                        rollingInterval: settings.RollingInterval,
                        rollOnFileSizeLimit: settings.RollOnFileSizeLimit,
                        retainedFileCountLimit: settings.RetainedFileCountLimit,
                        onCompletedFile: path =>
                        {
                            try
                            {
                                completedFileHandler.OnCompletedFile(path);
                            }
                            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                            catch (Exception ex)
                            {
                                PathSurveyLogMessages.CompletedFileHandlerFailed(hookLogger, ex, path);
                            }
                        }),
                    bufferSize: settings.BufferSize,
                    blockWhenFull: settings.BlockWhenFull)
                .CreateLogger();
        }

        private static RollingInterval ParseRolling(string? value) =>
            Enum.TryParse<RollingInterval>(value, ignoreCase: true, out var interval)
                ? interval
                : RollingInterval.Day;
    }

    /// <summary>
    /// Operational File/Async settings for the dedicated Path-survey sink.
    /// The Path-survey line format is not represented here.
    /// </summary>
    /// <param name="Path">Resolved Path-survey file path, including Serilog rolling token.</param>
    /// <param name="RollingInterval">Serilog File rolling interval.</param>
    /// <param name="RetainedFileCountLimit">
    /// Daily files to keep, including the active file. <c>.log</c> and <c>.log.gz</c> for the same day count as one.
    /// Production uses 1 so a completed day is removed after the ninpaths handoff and gzip.
    /// </param>
    /// <param name="Buffered">Whether the File sink buffers writes.</param>
    /// <param name="RollOnFileSizeLimit">Whether size-based rolling is enabled.</param>
    /// <param name="FileSizeLimitBytes">Serilog File size cap; <see langword="null"/> is unlimited.</param>
    /// <param name="BufferSize">Serilog.Sinks.Async buffer size.</param>
    /// <param name="BlockWhenFull">Whether Async blocks when the buffer is full.</param>
    /// <param name="FlushToDiskInterval">
    /// Serilog File periodic flush. <see langword="null"/> leaves buffering until dispose.
    /// Production <c>Serilog:Inpaths</c> sets one second.
    /// </param>
    public readonly record struct PathSurveyFileSinkSettings(
        string Path,
        RollingInterval RollingInterval,
        int RetainedFileCountLimit,
        bool Buffered,
        bool RollOnFileSizeLimit,
        long? FileSizeLimitBytes,
        int BufferSize,
        bool BlockWhenFull,
        TimeSpan? FlushToDiskInterval);
}
