using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Serilog.Formatting.Json;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.Common.Logging;
using VectorNNTPD.Common.Configuration;
using System.Globalization;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Logging
{
    /// <summary>
    /// Resolves <see cref="BackFillerFileLoggingTargetOptions.LogDir"/> and applies the BackFiller
    /// Serilog sink graph in code (Native AOT / single-file safe).
    /// </summary>
    /// <remarks>
    /// File, syslog, RabbitMQ, and optional console sinks are created here. The file directory is
    /// <c>BackFiller:Logging:File:LogDir</c>. Level, retention, JSON formatting, and target
    /// switches come from <see cref="BackFillerLoggingOptions"/>. A <c>Serilog</c> configuration
    /// section is not read. RabbitMQ logging publishes through <see cref="RabbitMqLogEventSink"/>
    /// on the existing <see cref="IRabbitMqService"/> connection.
    /// </remarks>
    internal static class BackFillerFileLogging
    {
        /// <summary>Serilog rolling path token: <c>{entry assembly name}-yyyyMMdd.log</c>.</summary>
        private const string RollingPathSuffix = "-.log";

        /// <summary>
        /// Text output template used when <see cref="BackFillerLoggingOptions.Json"/> is false and the
        /// process switch for ambient context enrichment is off. <c>SourceContext</c> is not rendered.
        /// </summary>
        internal const string SinkOutputTemplate =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

        /// <summary>
        /// Text output template used when <see cref="BackFillerLoggingOptions.Json"/> is false and the
        /// process switch for ambient context enrichment is on. Renders <c>{SourceContext}</c>.
        /// </summary>
        private const string SinkOutputTemplateWithSourceContext =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

        /// <summary>Restricted minimum level for sinks when the configured level is at least Debug.</summary>
        /// <remarks>
        /// When the configured minimum is below Debug, sinks use that lower level instead.
        /// <see cref="ConfigureLogger"/> still sets the logger minimum to the configured level.
        /// </remarks>
        internal const LogEventLevel SinkMinimumLevel = LogEventLevel.Debug;

        /// <summary>Event buffer size passed to the async wrapper around the file and RabbitMQ sinks.</summary>
        internal const int AsyncBufferSize = 50000;

        /// <summary>
        /// When true, the async file and RabbitMQ wrappers block once <see cref="AsyncBufferSize"/> events are queued.
        /// </summary>
        /// <remarks>
        /// False so a stalled file sink (disk full, day-roll gzip under lock) drops excess events instead of
        /// blocking article-path loggers and delaying settlement. Daily rotation, compression, and retention
        /// are unchanged. Critical diagnostics may be lost under sustained backpressure; Serilog SelfLog may
        /// record drops. Prefer ops alerts on log-volume disk pressure over blocking the data plane.
        /// </remarks>
        private const bool AsyncBlockWhenFull = false;

        /// <summary>Passed as the File sink <c>buffered</c> flag. True leaves that sink buffered.</summary>
        private const bool FileBuffered = true;

        /// <summary>
        /// Serilog File <c>flushToDiskInterval</c> of one second. The host logger is built here,
        /// not by <c>ReadFrom.Configuration</c>.
        /// </summary>
        internal static readonly TimeSpan FileFlushToDiskInterval = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Passed as the File sink <c>rollOnFileSizeLimit</c> flag.
        /// False leaves rolling to the daily interval, and no file size limit is set.
        /// </summary>
        private const bool RollOnFileSizeLimit = false;

        /// <summary>
        /// Resolves <paramref name="logDirectory"/> through Common
        /// <see cref="ApplicationLocalPath.ResolveApplicationLocalPath"/> against
        /// <paramref name="applicationBaseDirectory"/> (default
        /// <see cref="AppContext.BaseDirectory"/>). Absolute paths stay absolute.
        /// </summary>
        /// <param name="logDirectory">Configured directory. Null or whitespace is rejected. The value is trimmed before resolution.</param>
        /// <param name="applicationBaseDirectory">
        /// Application base passed to <see cref="ApplicationLocalPath.ResolveApplicationLocalPath"/>.
        /// Null uses <see cref="AppContext.BaseDirectory"/>.
        /// </param>
        /// <returns>The fully qualified log directory.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="logDirectory"/> is null or whitespace.</exception>
        private static string ResolveDirectory(string logDirectory, string? applicationBaseDirectory = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
            return ApplicationLocalPath.ResolveApplicationLocalPath(
                logDirectory.Trim(),
                applicationBaseDirectory ?? AppContext.BaseDirectory);
        }

        /// <summary>
        /// Builds the Serilog rolling path <c>{logDirectory}/{applicationName}-.log</c>.
        /// </summary>
        /// <param name="logDirectory">Directory that will contain the rolling file.</param>
        /// <param name="applicationName">Entry assembly name. Trimmed and suffixed with <see cref="RollingPathSuffix"/>.</param>
        /// <returns>The path passed to the File sink, including the Serilog rolling token in <see cref="RollingPathSuffix"/>.</returns>
        /// <exception cref="ArgumentException">Thrown when either argument is null or whitespace.</exception>
        private static string RollingFilePath(string logDirectory, string applicationName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
            var fileName = applicationName.Trim() + RollingPathSuffix;
            if (Path.IsPathRooted(fileName)
                || fileName is "." or ".."
                || fileName.Contains("..", StringComparison.Ordinal)
                || fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            {
                throw new ArgumentException("The log file name must stay inside the log directory.", nameof(applicationName));
            }

            return Path.Join(logDirectory, fileName);
        }

        /// <summary>
        /// Creates the log directory and returns the rolling File sink path for the current configuration.
        /// </summary>
        /// <param name="configuration">Application configuration. The file directory is read from <c>BackFiller:Logging</c>.</param>
        /// <param name="applicationBaseDirectory">Base directory for a relative log directory. Null uses <see cref="AppContext.BaseDirectory"/>.</param>
        /// <returns>The rolling file path after the log directory is created.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when file logging is disabled or <c>LogDir</c> is missing.</exception>
        private static string EnsureRollingFilePath(
            IConfiguration configuration,
            string? applicationBaseDirectory = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var logDir = ResolveDirectory(ReadRequiredFileLogDir(BindLogging(configuration)), applicationBaseDirectory);
            Directory.CreateDirectory(logDir);
            return RollingFilePath(logDir, ApplicationJsonConfiguration.EntryAssemblyName);
        }

        /// <summary>
        /// Applies the code-defined BackFiller sink graph.
        /// </summary>
        /// <param name="loggerConfiguration">The Serilog configuration to update.</param>
        /// <param name="configuration">Application configuration.</param>
        /// <param name="applicationBaseDirectory">
        /// Directory used to resolve a relative file-log path. Defaults to <see cref="AppContext.BaseDirectory"/>.
        /// </param>
        /// <param name="commandLine">
        /// Process switches that enable the console sink and ambient context enrichment.
        /// </param>
        /// <param name="services">
        /// Host services used to resolve <see cref="IRabbitMqService"/> when RabbitMQ logging is enabled.
        /// </param>
        /// <remarks>
        /// Does not call <c>ReadFrom.Configuration</c> and does not read a <c>Serilog</c> section.
        /// File, syslog, RabbitMQ, and console sinks are added only when their switches are on. Targets are not
        /// mutually exclusive. <paramref name="commandLine"/> enables the console sink and, when requested,
        /// ambient context enrichment and rendering of <c>SourceContext</c> in text and JSON output.
        /// Those switches are not configuration settings. Other JSON properties are still written.
        /// The original log event keeps <c>SourceContext</c>; sinks that omit it format a copy.
        /// </remarks>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="loggerConfiguration"/> or <paramref name="configuration"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when retention days are outside the accepted range, an enabled RabbitMQ or syslog target is incomplete,
        /// or RabbitMQ logging is enabled and <see cref="IRabbitMqService"/> is not registered.
        /// File-path resolution also throws when file logging is enabled without <c>LogDir</c>.
        /// </exception>
        internal static void ConfigureLogger(
            LoggerConfiguration loggerConfiguration,
            IConfiguration configuration,
            string? applicationBaseDirectory = null,
            BackFillerLoggingCommandLine commandLine = default,
            IServiceProvider? services = null)
        {
            ArgumentNullException.ThrowIfNull(loggerConfiguration);
            ArgumentNullException.ThrowIfNull(configuration);

            var logging = BindLogging(configuration);
            var minimumLevel = BackFillerLogLevelParser.ParseOrDefault(logging.LogLevel);
            var retainedFileCountLimit = ReadLogRetentionDays(logging);
            var rabbit = logging.RabbitMq ?? new BackFillerRabbitMqLoggingTargetOptions();
            if (rabbit.Enabled)
            {
                ValidateEnabledRabbitMq(rabbit);
            }

            var syslog = logging.Syslog ?? new BackFillerSyslogLoggingTargetOptions();
            if (syslog.Enabled)
            {
                ValidateEnabledSyslog(syslog);
            }

            var sinkLevel = minimumLevel < SinkMinimumLevel ? minimumLevel : SinkMinimumLevel;
            var includeSourceContext = commandLine.EnrichFromLogContext;
            ITextFormatter? jsonFormatter = logging.Json ? CreateJsonFormatter(includeSourceContext) : null;
            var file = logging.File ?? new BackFillerFileLoggingTargetOptions();
            var fileEnabled = file.Enabled;

            loggerConfiguration
                .MinimumLevel.Is(minimumLevel)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .MinimumLevel.Override("VectorNNTPD.BackFillerService", minimumLevel)
                .Enrich.WithProperty("Application", ApplicationJsonConfiguration.EntryAssemblyName);

            if (commandLine.EnrichFromLogContext)
            {
                loggerConfiguration.Enrich.FromLogContext();
            }

            if (commandLine.Console)
            {
                WriteConsole(loggerConfiguration, sinkLevel, jsonFormatter, includeSourceContext);
            }

            if (fileEnabled)
            {
                var path = EnsureRollingFilePath(configuration, applicationBaseDirectory);
                loggerConfiguration.WriteTo.Async(
                    sink => WriteFile(sink, path, sinkLevel, retainedFileCountLimit, jsonFormatter, includeSourceContext),
                    bufferSize: AsyncBufferSize,
                    blockWhenFull: AsyncBlockWhenFull);
            }

            if (rabbit.Enabled)
            {
                WriteRabbitMq(loggerConfiguration, rabbit, sinkLevel, jsonFormatter, services, includeSourceContext);
            }

            if (syslog.Enabled)
            {
                WriteSyslog(loggerConfiguration, syslog, sinkLevel, jsonFormatter, includeSourceContext);
            }
        }

        /// <summary>Selects the text template for sinks that are not using the JSON formatter.</summary>
        /// <param name="includeSourceContext">
        /// <see langword="true"/> when the process switch for ambient context enrichment is on.
        /// </param>
        /// <returns>
        /// <see cref="SinkOutputTemplateWithSourceContext"/> when <paramref name="includeSourceContext"/> is true;
        /// otherwise <see cref="SinkOutputTemplate"/>.
        /// </returns>
        internal static string OutputTemplate(bool includeSourceContext) =>
            includeSourceContext ? SinkOutputTemplateWithSourceContext : SinkOutputTemplate;

        /// <summary>Creates the JSON formatter shared by every JSON-enabled sink.</summary>
        /// <param name="includeSourceContext">
        /// <see langword="true"/> when the process switch for ambient context enrichment is on.
        /// </param>
        /// <returns>
        /// Serilog's JSON formatter when <paramref name="includeSourceContext"/> is true.
        /// Otherwise, a formatter that writes the same JSON from a copy of the event that has no
        /// <c>SourceContext</c> property.
        /// </returns>
        private static ITextFormatter CreateJsonFormatter(bool includeSourceContext) =>
            includeSourceContext
                ? new JsonFormatter(renderMessage: true)
                : new SourceContextOmittedJsonFormatter();

        /// <summary>Binds <c>BackFiller:Logging</c>. Missing keys keep the option defaults.</summary>
        /// <param name="configuration">Application configuration.</param>
        /// <returns>
        /// Bound options. Null file, RabbitMQ, and syslog children are replaced with new default instances.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> is null.</exception>
        private static BackFillerLoggingOptions BindLogging(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var logging = new BackFillerLoggingOptions();
            configuration.GetSection($"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}").Bind(logging);
            logging.File ??= new BackFillerFileLoggingTargetOptions();
            logging.RabbitMq ??= new BackFillerRabbitMqLoggingTargetOptions();
            logging.Syslog ??= new BackFillerSyslogLoggingTargetOptions();
            return logging;
        }

        /// <summary>
        /// Reads retention from a bound logging section, or the default when the object uses the default.
        /// </summary>
        /// <param name="logging">Bound logging options.</param>
        /// <returns><see cref="BackFillerLoggingOptions.LogRetentionDays"/> when it is inside the accepted inclusive range.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="logging"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The value is outside the accepted range.</exception>
        private static int ReadLogRetentionDays(BackFillerLoggingOptions logging)
        {
            ArgumentNullException.ThrowIfNull(logging);
            var days = logging.LogRetentionDays;
            if (days < BackFillerLoggingOptions.MinimumLogRetentionDays
                || days > BackFillerLoggingOptions.MaximumLogRetentionDays)
            {
                throw new InvalidOperationException(
                    $"BackFiller:Logging:LogRetentionDays '{days.ToString(CultureInfo.InvariantCulture)}' is not valid. Use an integer in the range {BackFillerLoggingOptions.MinimumLogRetentionDays}–{BackFillerLoggingOptions.MaximumLogRetentionDays}.");
            }

            return days;
        }

        /// <summary>Returns the trimmed file log directory when file logging is enabled.</summary>
        /// <param name="logging">Bound logging options. A null file child is treated as a new default target.</param>
        /// <returns>The trimmed <c>LogDir</c>.</returns>
        /// <exception cref="InvalidOperationException">Thrown when file logging is disabled or <c>LogDir</c> is null or whitespace.</exception>
        private static string ReadRequiredFileLogDir(BackFillerLoggingOptions logging)
        {
            var file = logging.File ?? new BackFillerFileLoggingTargetOptions();
            if (!file.Enabled)
            {
                throw new InvalidOperationException("BackFiller file logging is disabled.");
            }

            if (string.IsNullOrWhiteSpace(file.LogDir))
            {
                throw new InvalidOperationException(
                    "BackFiller:Logging:File:LogDir is required when file logging is enabled (old key: DirLogs).");
            }

            return file.LogDir.Trim();
        }

        /// <summary>Rejects an enabled RabbitMQ logging target that has no exchange or routing key.</summary>
        /// <param name="rabbit">RabbitMQ target already known to be enabled.</param>
        /// <exception cref="InvalidOperationException">Thrown when <c>Exchange</c> or <c>RoutingKey</c> is null or whitespace.</exception>
        private static void ValidateEnabledRabbitMq(BackFillerRabbitMqLoggingTargetOptions rabbit)
        {
            if (string.IsNullOrWhiteSpace(rabbit.Exchange))
            {
                throw new InvalidOperationException(
                    "BackFiller:Logging:RabbitMQ:Exchange is required when RabbitMQ logging is enabled.");
            }

            if (string.IsNullOrWhiteSpace(rabbit.RoutingKey))
            {
                throw new InvalidOperationException(
                    "BackFiller:Logging:RabbitMQ:RoutingKey is required when RabbitMQ logging is enabled.");
            }
        }

        /// <summary>Adds an async RabbitMQ sink that publishes through the registered <see cref="IRabbitMqService"/>.</summary>
        /// <param name="loggerConfiguration">Logger configuration that receives the sink.</param>
        /// <param name="rabbit">Enabled target. Exchange and routing key are passed through.</param>
        /// <param name="sinkLevel">Restricted minimum level for the sink.</param>
        /// <param name="jsonFormatter">JSON formatter when JSON logging is on; null selects the text template and text content type.</param>
        /// <param name="services">Host services. Must resolve <see cref="IRabbitMqService"/>.</param>
        /// <param name="includeSourceContext">Selects whether the text template renders <c>{SourceContext}</c>.</param>
        /// <exception cref="InvalidOperationException">Thrown when <see cref="IRabbitMqService"/> is not registered.</exception>
        /// <remarks>
        /// The sink uses <see cref="AsyncBufferSize"/> and <see cref="AsyncBlockWhenFull"/>.
        /// JSON uses <see cref="RabbitMqLogEventSink.JsonContentType"/>; text uses <see cref="RabbitMqLogEventSink.TextContentType"/>.
        /// </remarks>
        private static void WriteRabbitMq(
            LoggerConfiguration loggerConfiguration,
            BackFillerRabbitMqLoggingTargetOptions rabbit,
            LogEventLevel sinkLevel,
            ITextFormatter? jsonFormatter,
            IServiceProvider? services,
            bool includeSourceContext)
        {
            var rabbitMq = services?.GetService<IRabbitMqService>()
                ?? throw new InvalidOperationException(
                    "BackFiller:Logging:RabbitMQ is enabled but IRabbitMqService is not registered.");
            ITextFormatter formatter = jsonFormatter
                ?? new MessageTemplateTextFormatter(OutputTemplate(includeSourceContext), CultureInfo.InvariantCulture);
            var sink = new RabbitMqLogEventSink(
                rabbitMq,
                rabbit.Exchange,
                rabbit.RoutingKey,
                ApplicationJsonConfiguration.EntryAssemblyName,
                formatter,
                jsonFormatter is null ? RabbitMqLogEventSink.TextContentType : RabbitMqLogEventSink.JsonContentType);
            loggerConfiguration.WriteTo.Async(
                writeTo => writeTo.Sink(sink, restrictedToMinimumLevel: sinkLevel),
                bufferSize: AsyncBufferSize,
                blockWhenFull: AsyncBlockWhenFull);
        }

        /// <summary>Rejects an enabled syslog target with a blank host, a port outside 1-65535, or a protocol other than UDP or TCP.</summary>
        /// <param name="syslog">Syslog target already known to be enabled.</param>
        /// <exception cref="InvalidOperationException">Thrown when host, port, or protocol is not acceptable.</exception>
        /// <remarks>Protocol matching is trim plus <see cref="StringComparison.OrdinalIgnoreCase"/> against the UDP and TCP constants.</remarks>
        private static void ValidateEnabledSyslog(BackFillerSyslogLoggingTargetOptions syslog)
        {
            if (string.IsNullOrWhiteSpace(syslog.Host))
            {
                throw new InvalidOperationException(
                    "BackFiller:Logging:Syslog:Host is required when syslog logging is enabled.");
            }

            if (syslog.Port is < 1 or > 65535)
            {
                throw new InvalidOperationException(
                    "BackFiller:Logging:Syslog:Port must be an integer in the range 1–65535.");
            }

            if (!IsUdp(syslog.Protocol) && !IsTcp(syslog.Protocol))
            {
                throw new InvalidOperationException("BackFiller:Logging:Syslog:Protocol must be Udp or Tcp.");
            }
        }

        /// <summary>Adds the console sink at <paramref name="sinkLevel"/>.</summary>
        /// <param name="loggerConfiguration">Logger configuration that receives the sink.</param>
        /// <param name="sinkLevel">Restricted minimum level.</param>
        /// <param name="jsonFormatter">JSON formatter when JSON logging is on; null selects <see cref="OutputTemplate"/>.</param>
        /// <param name="includeSourceContext">Selects whether the text template renders <c>{SourceContext}</c>.</param>
        private static void WriteConsole(
            LoggerConfiguration loggerConfiguration,
            LogEventLevel sinkLevel,
            ITextFormatter? jsonFormatter,
            bool includeSourceContext)
        {
            if (jsonFormatter is null)
            {
                loggerConfiguration.WriteTo.Console(
                    restrictedToMinimumLevel: sinkLevel,
                    outputTemplate: OutputTemplate(includeSourceContext));
                return;
            }

            loggerConfiguration.WriteTo.Console(jsonFormatter, restrictedToMinimumLevel: sinkLevel);
        }

        /// <summary>Adds the daily rolling file sink, with or without the JSON formatter.</summary>
        /// <param name="sink">Async wrapper configuration.</param>
        /// <param name="path">Rolling path from <see cref="RollingFilePath"/>.</param>
        /// <param name="sinkLevel">Restricted minimum level.</param>
        /// <param name="retainedFileCountLimit">Retained file count from <see cref="BackFillerLoggingOptions.LogRetentionDays"/>.</param>
        /// <param name="jsonFormatter">JSON formatter when JSON logging is on; null selects <see cref="OutputTemplate"/>.</param>
        /// <param name="includeSourceContext">Selects whether the text template renders <c>{SourceContext}</c>.</param>
        /// <remarks>
        /// Both branches set <see cref="FileBuffered"/>, <see cref="FileFlushToDiskInterval"/>,
        /// daily rolling, <see cref="RollOnFileSizeLimit"/>, a null file size limit,
        /// and gzip compression when a day becomes inactive.
        /// </remarks>
        private static void WriteFile(
            LoggerSinkConfiguration sink,
            string path,
            LogEventLevel sinkLevel,
            int retainedFileCountLimit,
            ITextFormatter? jsonFormatter,
            bool includeSourceContext)
        {
            if (jsonFormatter is null)
            {
                sink.DailyGzipFile(
                    path,
                    restrictedToMinimumLevel: sinkLevel,
                    outputTemplate: OutputTemplate(includeSourceContext),
                    fileSizeLimitBytes: null,
                    buffered: FileBuffered,
                    flushToDiskInterval: FileFlushToDiskInterval,
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: RollOnFileSizeLimit,
                    retainedFileCountLimit: retainedFileCountLimit);
                return;
            }

            sink.DailyGzipFile(
                jsonFormatter,
                path,
                restrictedToMinimumLevel: sinkLevel,
                fileSizeLimitBytes: null,
                buffered: FileBuffered,
                flushToDiskInterval: FileFlushToDiskInterval,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: RollOnFileSizeLimit,
                retainedFileCountLimit: retainedFileCountLimit);
        }

        /// <summary>Adds a UDP or TCP syslog sink. TCP is opened with TLS off.</summary>
        /// <param name="loggerConfiguration">Logger configuration that receives the sink.</param>
        /// <param name="syslog">Enabled syslog target. Host is trimmed.</param>
        /// <param name="sinkLevel">Restricted minimum level.</param>
        /// <param name="jsonFormatter">JSON formatter when JSON logging is on; null leaves the syslog sink formatter unset.</param>
        /// <param name="includeSourceContext">
        /// When false, the syslog sink sees a copy of each event without <c>SourceContext</c>.
        /// The syslog formatters otherwise render that property themselves, including around a JSON body.
        /// </param>
        /// <remarks>
        /// The application name is <see cref="ApplicationJsonConfiguration.EntryAssemblyName"/>.
        /// UDP is chosen by <see cref="IsUdp"/>; any other protocol uses the TCP sink.
        /// </remarks>
        private static void WriteSyslog(
            LoggerConfiguration loggerConfiguration,
            BackFillerSyslogLoggingTargetOptions syslog,
            LogEventLevel sinkLevel,
            ITextFormatter? jsonFormatter,
            bool includeSourceContext)
        {
            if (!includeSourceContext)
            {
                loggerConfiguration.WriteTo.Logger(sub =>
                {
                    sub.MinimumLevel.Is(LogEventLevel.Verbose);
                    sub.Enrich.With(new OmitSourceContextEnricher());
                    AddSyslogSink(sub, syslog, sinkLevel, jsonFormatter);
                });
                return;
            }

            AddSyslogSink(loggerConfiguration, syslog, sinkLevel, jsonFormatter);
        }

        /// <summary>Adds a UDP or TCP syslog sink. TCP is opened with TLS off.</summary>
        /// <param name="loggerConfiguration">Logger configuration that receives the sink.</param>
        /// <param name="syslog">Enabled syslog target. Host is trimmed.</param>
        /// <param name="sinkLevel">Restricted minimum level.</param>
        /// <param name="jsonFormatter">JSON formatter when JSON logging is on; null leaves the syslog sink formatter unset.</param>
        private static void AddSyslogSink(
            LoggerConfiguration loggerConfiguration,
            BackFillerSyslogLoggingTargetOptions syslog,
            LogEventLevel sinkLevel,
            ITextFormatter? jsonFormatter)
        {
            var host = syslog.Host.Trim();
            var appName = ApplicationJsonConfiguration.EntryAssemblyName;
            if (IsUdp(syslog.Protocol))
            {
                loggerConfiguration.WriteTo.UdpSyslog(
                    host,
                    syslog.Port,
                    appName: appName,
                    restrictedToMinimumLevel: sinkLevel,
                    formatter: jsonFormatter);
                return;
            }

            loggerConfiguration.WriteTo.TcpSyslog(
                host,
                syslog.Port,
                appName: appName,
                useTls: false,
                restrictedToMinimumLevel: sinkLevel,
                formatter: jsonFormatter);
        }

        /// <summary>Reports whether <paramref name="protocol"/> is UDP after trim, ignoring case.</summary>
        /// <param name="protocol">Configured protocol. Null is not UDP.</param>
        /// <returns><see langword="true"/> when the trimmed value equals <see cref="BackFillerSyslogLoggingTargetOptions.UdpProtocol"/>.</returns>
        private static bool IsUdp(string? protocol) =>
            string.Equals(protocol?.Trim(), BackFillerSyslogLoggingTargetOptions.UdpProtocol, StringComparison.OrdinalIgnoreCase);

        /// <summary>Reports whether <paramref name="protocol"/> is TCP after trim, ignoring case.</summary>
        /// <param name="protocol">Configured protocol. Null is not TCP.</param>
        /// <returns><see langword="true"/> when the trimmed value equals <see cref="BackFillerSyslogLoggingTargetOptions.TcpProtocol"/>.</returns>
        private static bool IsTcp(string? protocol) =>
            string.Equals(protocol?.Trim(), BackFillerSyslogLoggingTargetOptions.TcpProtocol, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Writes Serilog JSON from a copy of the event that has no <c>SourceContext</c> property.
        /// </summary>
        /// <remarks>
        /// <see cref="JsonFormatter"/> emits every property. Removing <c>SourceContext</c> on a copy leaves
        /// the caller's event unchanged and leaves every other property in the JSON document.
        /// </remarks>
        private sealed class SourceContextOmittedJsonFormatter : ITextFormatter
        {
            /// <summary>JSON formatter used after <c>SourceContext</c> has been removed from the copy.</summary>
            private readonly JsonFormatter _inner = new(renderMessage: true);

            /// <summary>Formats <paramref name="logEvent"/> without rendering <c>SourceContext</c>.</summary>
            /// <param name="logEvent">Event to format. Not modified.</param>
            /// <param name="output">Destination for the JSON document.</param>
            public void Format(LogEvent logEvent, TextWriter output)
            {
                ArgumentNullException.ThrowIfNull(logEvent);
                ArgumentNullException.ThrowIfNull(output);
                if (!logEvent.Properties.ContainsKey(Constants.SourceContextPropertyName))
                {
                    _inner.Format(logEvent, output);
                    return;
                }

                var properties = logEvent.Properties
                    .Where(static pair => pair.Key != Constants.SourceContextPropertyName)
                    .Select(static pair => new LogEventProperty(pair.Key, pair.Value));
                var copy = logEvent is { TraceId: { } traceId, SpanId: { } spanId }
                    ? new LogEvent(
                        logEvent.Timestamp,
                        logEvent.Level,
                        logEvent.Exception,
                        logEvent.MessageTemplate,
                        properties,
                        traceId,
                        spanId)
                    : new LogEvent(
                        logEvent.Timestamp,
                        logEvent.Level,
                        logEvent.Exception,
                        logEvent.MessageTemplate,
                        properties);
                _inner.Format(copy, output);
            }
        }

        /// <summary>
        /// Drops <c>SourceContext</c> from a syslog sub-logger event so the syslog formatters do not render it.
        /// </summary>
        private sealed class OmitSourceContextEnricher : ILogEventEnricher
        {
            /// <summary>Removes <see cref="Constants.SourceContextPropertyName"/> when it is present.</summary>
            /// <param name="logEvent">Event received by the syslog sub-logger.</param>
            /// <param name="propertyFactory">Unused. The enricher only removes a property.</param>
            public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
            {
                ArgumentNullException.ThrowIfNull(logEvent);
                ArgumentNullException.ThrowIfNull(propertyFactory);
                logEvent.RemovePropertyIfPresent(Constants.SourceContextPropertyName);
            }
        }
    }
}
