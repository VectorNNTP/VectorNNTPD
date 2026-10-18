using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging.Debug;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Logging;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.BackFillerService.Tests.Logging
{
    /// <summary>
    /// ConfigureBackFillerLogging Serilog wiring, levels, paths, retention, and production JSON contract.
    /// </summary>
    [Collection(SerilogCollection.Name)]
    public sealed class BackFillerSerilogLoggingTests
    {
        /// <summary>Host registers single SerilogLoggerFactory and removes default ILoggerProvider registrations.</summary>
        [Fact]
        public void ConfigureBackFillerLogging_RegistersSerilog_AndRemovesDefaultProviders()
        {
            using var host = CreateLoggingHost();
            var factories = host.Services.GetServices<ILoggerFactory>().ToArray();
            Assert.Single(factories);
            Assert.Equal("SerilogLoggerFactory", factories[0].GetType().Name);

            var providers = host.Services.GetServices<ILoggerProvider>().ToArray();
            Assert.Empty(providers);
            Assert.DoesNotContain(providers, static p => p is ConsoleLoggerProvider);
            Assert.DoesNotContain(providers, static p => p is DebugLoggerProvider);
        }
        /// <summary>Default omits debug; explicit Debug enables it; explicit Information disables debug.</summary>

        [Fact]
        public void LogLevel_OmittedUsesInformation_ExplicitDebugEnablesDebug_ExplicitInformationDisablesDebug()
        {
            Assert.Equal(LogEventLevel.Debug, BackFillerFileLogging.SinkMinimumLevel);

            var logDir = CreateTempLogDir();
            try
            {
                using (var host = CreateLoggingHost(logDir))
                {
                    var omitted = host.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("VectorNNTPD.BackFillerService.Hosting");
                    Assert.True(omitted.IsEnabled(LogLevel.Information));
                    Assert.False(omitted.IsEnabled(LogLevel.Debug));
                }

                Log.CloseAndFlush();

                using (var host = CreateLoggingHost(
                    logDir,
                    settings: new Dictionary<string, string?>
                    {
                        [$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:{nameof(BackFillerLoggingOptions.LogLevel)}"] = "Debug",
                    }))
                {
                    var debug = host.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("VectorNNTPD.BackFillerService.Hosting");
                    Assert.True(debug.IsEnabled(LogLevel.Debug));
                }

                Log.CloseAndFlush();

                using (var host = CreateLoggingHost(
                    logDir,
                    settings: new Dictionary<string, string?>
                    {
                        [$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:{nameof(BackFillerLoggingOptions.LogLevel)}"] = "Information",
                    }))
                {
                    var information = host.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("VectorNNTPD.BackFillerService.Hosting");
                    Assert.True(information.IsEnabled(LogLevel.Information));
                    Assert.False(information.IsEnabled(LogLevel.Debug));
                }
            }
            finally
            {
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }
        /// <summary>File logs land under ApplicationLocalPath resolved from configured log dir.</summary>

        [Fact]
        public void FileSink_IsConfigured_BeneathApplicationLocalLogs()
        {
            var logDir = CreateTempLogDir();
            try
            {
                using (var host = CreateLoggingHost(logDir))
                {
                    var logger = host.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("VectorNNTPD.BackFillerService");
                    logger.LogInformation("backfiller-file-sink-marker");
                }

                Log.CloseAndFlush();

                var expectedDir = ApplicationLocalPath.ResolveApplicationLocalPath(logDir, AppContext.BaseDirectory);
                Assert.True(Directory.Exists(expectedDir));
                Assert.Contains(
                    Directory.GetFiles(expectedDir, ApplicationJsonConfiguration.EntryAssemblyName + "-*.log"),
                    path => File.ReadAllText(path).Contains("backfiller-file-sink-marker", StringComparison.Ordinal));
            }
            finally
            {
                TryDelete(logDir);
            }
        }
        /// <summary>Relative log dir resolves from application base, not process cwd.</summary>

        [Fact]
        public void FileLog_ResolvesRelativeDirectory_FromApplicationBase_NotWorkingDirectory()
        {
            var previous = Environment.CurrentDirectory;
            var cwd = Directory.CreateTempSubdirectory("bf-serilog-cwd-").FullName;
            var relative = "bf-rel-" + Guid.NewGuid().ToString("N");
            var expected = ApplicationLocalPath.ResolveApplicationLocalPath(relative, AppContext.BaseDirectory);
            try
            {
                Environment.CurrentDirectory = cwd;
                using (var host = CreateLoggingHost(relative))
                {
                    var logger = host.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("VectorNNTPD.BackFillerService");
                    logger.LogInformation("backfiller-relative-log-marker");
                }

                Log.CloseAndFlush();

                Assert.True(Directory.Exists(expected));
                Assert.False(Directory.Exists(Path.Join(cwd, relative)));
                Assert.StartsWith(
                    Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    expected,
                    StringComparison.OrdinalIgnoreCase);
                Assert.Contains(
                    Directory.GetFiles(expected, ApplicationJsonConfiguration.EntryAssemblyName + "-*.log"),
                    path => File.ReadAllText(path).Contains("backfiller-relative-log-marker", StringComparison.Ordinal));
            }
            finally
            {
                Environment.CurrentDirectory = previous;
                TryDelete(cwd);
                TryDelete(expected);
            }
        }
        /// <summary>First log write creates configured absolute log directory.</summary>

        [Fact]
        public void FileLog_CreatesDirectory_FromConfiguredLogDirectory()
        {
            var logDir = Path.Join(Path.GetTempPath(), "vectornntp-bf-roll-" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.False(Directory.Exists(logDir));
                using (var host = CreateLoggingHost(logDir))
                {
                    var logger = host.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("VectorNNTPD.BackFillerService");
                    logger.LogInformation("backfiller-created-dir-marker");
                }

                Log.CloseAndFlush();

                Assert.True(Directory.Exists(logDir));
                var files = Directory.GetFiles(logDir, ApplicationJsonConfiguration.EntryAssemblyName + "-*.log");
                Assert.Contains(
                    files,
                    path => File.ReadAllText(path).Contains("backfiller-created-dir-marker", StringComparison.Ordinal));
                Assert.DoesNotContain(
                    files,
                    path => path.Contains("logs/VectorNNTPD.BackFillerService-.log", StringComparison.Ordinal));
            }
            finally
            {
                TryDelete(logDir);
            }
        }
        /// <summary>Shipped VectorNNTPD.BackFillerService.json uses BackFiller:Logging only, not Serilog section.</summary>

        [Fact]
        public void ProductionJson_DeclaresLoggingSettings_AndOmitsSerilogSection()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FindProductionConfigurationFile()));
            Assert.False(doc.RootElement.TryGetProperty("Serilog", out _));
            var backFiller = doc.RootElement.GetProperty("BackFiller");
            Assert.False(backFiller.TryGetProperty("LogDir", out _));
            Assert.False(backFiller.TryGetProperty("LogDirectory", out _));
            Assert.False(backFiller.TryGetProperty("LogLevel", out _));
            var logging = backFiller.GetProperty("Logging");
            Assert.Equal(BackFillerLoggingOptions.DefaultLogLevel, logging.GetProperty("LogLevel").GetString());
            Assert.Equal(BackFillerLoggingOptions.DefaultLogRetentionDays, logging.GetProperty("LogRetentionDays").GetInt32());
            Assert.False(logging.GetProperty("Json").GetBoolean());
            Assert.True(logging.GetProperty("File").GetProperty("Enabled").GetBoolean());
            Assert.Equal("logs", logging.GetProperty("File").GetProperty("LogDir").GetString());
            Assert.False(logging.GetProperty("RabbitMQ").GetProperty("Enabled").GetBoolean());
            Assert.Equal("logs", logging.GetProperty("RabbitMQ").GetProperty("Exchange").GetString());
            Assert.Equal("backfiller", logging.GetProperty("RabbitMQ").GetProperty("RoutingKey").GetString());
            Assert.False(logging.GetProperty("Syslog").GetProperty("Enabled").GetBoolean());
            Assert.Equal(514, logging.GetProperty("Syslog").GetProperty("Port").GetInt32());
            Assert.Equal("Udp", logging.GetProperty("Syslog").GetProperty("Protocol").GetString());
            Assert.Equal(TimeSpan.FromSeconds(1), BackFillerFileLogging.FileFlushToDiskInterval);
            Assert.False(backFiller.TryGetProperty("Serilog", out _));
        }
        /// <summary>Each configured LogLevel maps to expected ILogger.IsEnabled threshold.</summary>

        [Theory]
        [InlineData("Verbose", LogLevel.Trace)]
        [InlineData("Debug", LogLevel.Debug)]
        [InlineData("Information", LogLevel.Information)]
        [InlineData("Warning", LogLevel.Warning)]
        [InlineData("Error", LogLevel.Error)]
        [InlineData("Fatal", LogLevel.Critical)]
        public void LogLevel_ControlsSerilogMinimumLevel(string configured, LogLevel enabled)
        {
            var logDir = CreateTempLogDir();
            try
            {
                using var host = CreateLoggingHost(
                    logDir,
                    settings: new Dictionary<string, string?>
                    {
                        [$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:{nameof(BackFillerLoggingOptions.LogLevel)}"] = configured,
                    });
                var logger = host.Services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("VectorNNTPD.BackFillerService");
                Assert.True(logger.IsEnabled(enabled));
                if (enabled != LogLevel.Trace)
                {
                    Assert.False(logger.IsEnabled(enabled - 1));
                }
            }
            finally
            {
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }
        /// <summary>LogRetentionDays binds GzipRollingFileSink retained file count.</summary>

        [Fact]
        public void LogRetentionDays_ControlsFileRetention()
        {
            var logDir = CreateTempLogDir();
            try
            {
                using var configuration = new ConfigurationManager();
                configuration[$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:File:{nameof(BackFillerFileLoggingTargetOptions.LogDir)}"] = logDir;
                configuration[$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:{nameof(BackFillerLoggingOptions.LogRetentionDays)}"] = "9";

                var loggerConfiguration = new LoggerConfiguration();
                BackFillerFileLogging.ConfigureLogger(loggerConfiguration, configuration);
                using var built = loggerConfiguration.CreateLogger();

                var fileSink = Assert.Single(
                    WalkLogEventSinks(built),
                    static n => n.GetType().Name.Equals("GzipRollingFileSink", StringComparison.Ordinal));
                Assert.Equal(9, ReadInstanceField(fileSink, "_retainedFileCountLimit"));
            }
            finally
            {
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }
        /// <summary>Serilog config section is ignored; BackFiller logging options remain authoritative.</summary>

        [Fact]
        public void SerilogJsonSection_DoesNotChangeLevelDirectoryOrRetention()
        {
            var logDir = CreateTempLogDir();
            var decoy = CreateTempLogDir();
            try
            {
                using (var host = CreateLoggingHost(
                    logDir,
                    settings: new Dictionary<string, string?>
                    {
                        [$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:{nameof(BackFillerLoggingOptions.LogLevel)}"] = "Warning",
                        [$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:{nameof(BackFillerLoggingOptions.LogRetentionDays)}"] = "4",
                        ["Serilog:MinimumLevel:Default"] = "Verbose",
                        ["Serilog:MinimumLevel:Override:VectorNNTPD.BackFillerService"] = "Verbose",
                        ["Serilog:WriteTo:0:Name"] = "Console",
                        ["Serilog:WriteTo:1:Name"] = "Async",
                        ["Serilog:WriteTo:1:Args:configure:0:Name"] = "File",
                        ["Serilog:WriteTo:1:Args:configure:0:Args:path"] = Path.Join(decoy, "evil-.log"),
                        ["Serilog:WriteTo:1:Args:configure:0:Args:retainedFileCountLimit"] = "99",
                        ["Serilog:WriteTo:1:Args:configure:0:Args:rollingInterval"] = "Hour",
                        ["Serilog:WriteTo:1:Args:configure:0:Args:flushToDiskInterval"] = "00:05:00",
                    }))
                {
                    var logger = host.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("VectorNNTPD.BackFillerService");
                    Assert.True(logger.IsEnabled(LogLevel.Warning));
                    Assert.False(logger.IsEnabled(LogLevel.Information));
                    logger.LogWarning("serilog-section-ignored-marker");
                }

                Log.CloseAndFlush();
                Assert.Contains(
                    Directory.GetFiles(logDir, ApplicationJsonConfiguration.EntryAssemblyName + "-*.log"),
                    path => File.ReadAllText(path).Contains("serilog-section-ignored-marker", StringComparison.Ordinal));
                Assert.Empty(Directory.GetFiles(decoy, "*", SearchOption.AllDirectories));
            }
            finally
            {
                TryDelete(logDir);
                TryDelete(decoy);
            }
        }
        /// <summary>Invalid LogLevel throws InvalidOperationException listing allowed names.</summary>

        [Theory]
        [InlineData("Trace")]
        [InlineData("Critical")]
        [InlineData("nope")]
        [InlineData("")]
        public void InvalidLogLevel_IsRejected(string configured)
        {
            var logDir = CreateTempLogDir();
            try
            {
                using var configuration = new ConfigurationManager();
                configuration[$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:File:{nameof(BackFillerFileLoggingTargetOptions.LogDir)}"] = logDir;
                configuration[$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:{nameof(BackFillerLoggingOptions.LogLevel)}"] = configured;

                var error = Assert.Throws<InvalidOperationException>(
                    () => BackFillerFileLogging.ConfigureLogger(new LoggerConfiguration(), configuration));
                Assert.Contains("BackFiller:Logging:LogLevel", error.Message, StringComparison.Ordinal);
                Assert.Contains("Verbose, Debug, Information, Warning, Error, or Fatal", error.Message, StringComparison.Ordinal);
            }
            finally
            {
                TryDelete(logDir);
            }
        }
        /// <summary>Default configure builds async gzip daily sink with expected buffer and flush wiring.</summary>

        [Fact]
        public void ExplicitConfiguration_BuildsUnlimitedDailyAsyncGzipFileSink()
        {
            var logDir = CreateTempLogDir();
            try
            {
                using var configuration = new ConfigurationManager();
                configuration[$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:File:{nameof(BackFillerFileLoggingTargetOptions.LogDir)}"] = logDir;

                var loggerConfiguration = new LoggerConfiguration();
                BackFillerFileLogging.ConfigureLogger(loggerConfiguration, configuration);
                using var built = loggerConfiguration.CreateLogger();

                var sinks = WalkLogEventSinks(built).ToArray();
                var fileSink = Assert.Single(
                    sinks,
                    static n => n.GetType().Name.Equals("GzipRollingFileSink", StringComparison.Ordinal));
                Assert.Equal("VectorNNTPD.Common", fileSink.GetType().Assembly.GetName().Name);
                Assert.Null(ReadInstanceField(fileSink, "_fileSizeLimitBytes"));
                Assert.False(Assert.IsType<bool>(ReadInstanceField(fileSink, "_rollOnFileSizeLimit")!));
                Assert.Equal(BackFillerLoggingOptions.DefaultLogRetentionDays, ReadInstanceField(fileSink, "_retainedFileCountLimit"));
                Assert.True(Assert.IsType<bool>(ReadInstanceField(fileSink, "_buffered")!));
                Assert.DoesNotContain(sinks, static n => n.GetType().Name.Equals("RollingFileSink", StringComparison.Ordinal));

                var asyncSink = Assert.Single(
                    sinks,
                    static n => n.GetType().Name.Equals("BackgroundWorkerSink", StringComparison.Ordinal));
                Assert.True(Assert.IsType<bool>(ReadInstanceField(asyncSink, "_blockWhenFull")!));
                var queue = ReadInstanceField(asyncSink, "_queue")
                            ?? throw new InvalidOperationException("BackgroundWorkerSink._queue was not found.");
                var boundedCapacity = queue.GetType().GetProperty("BoundedCapacity")?.GetValue(queue)
                                      ?? throw new InvalidOperationException("BoundedCapacity was not found.");
                Assert.Equal(
                    BackFillerFileLogging.AsyncBufferSize,
                    Convert.ToInt32(boundedCapacity, CultureInfo.InvariantCulture));

                Assert.DoesNotContain(
                    sinks,
                    static n => n.GetType().Name.Contains("Console", StringComparison.Ordinal));
                AssertFlushesOncePerSecond(sinks);
            }
            finally
            {
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }
        /// <summary>Console and file include debug; trace stays off file sink at default levels.</summary>

        [Fact]
        public void ConsoleReceivesDebug_FileReceivesDebugNotTrace()
        {
            var logDir = CreateTempLogDir();
            using var captured = new StringWriter();
            var previous = Console.Out;
            try
            {
                Console.SetOut(captured);
                using (var host = CreateLoggingHost(
                    logDir,
                    configure: static lc => lc.MinimumLevel.Override("VectorNNTPD.BackFillerService", LogEventLevel.Verbose),
                    commandLine: new BackFillerLoggingCommandLine(Console: true, EnrichFromLogContext: false)))
                {
                    var logger = host.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("VectorNNTPD.BackFillerService");

                    Assert.True(logger.IsEnabled(LogLevel.Debug));
                    Assert.True(logger.IsEnabled(LogLevel.Trace));

                    logger.LogInformation("backfiller-console-info-marker");
                    logger.LogDebug("backfiller-console-debug-marker");
                    logger.LogTrace("backfiller-file-trace-marker");
                }

                Log.CloseAndFlush();
            }
            finally
            {
                Console.SetOut(previous);
            }

            var console = captured.ToString();
            Assert.Contains("backfiller-console-info-marker", console, StringComparison.Ordinal);
            Assert.Contains("backfiller-console-debug-marker", console, StringComparison.Ordinal);
            Assert.DoesNotContain("backfiller-file-trace-marker", console, StringComparison.Ordinal);

            var daily = Assert.Single(
                Directory.GetFiles(logDir, ApplicationJsonConfiguration.EntryAssemblyName + "-*.log"),
                path => Path.GetFileName(path).StartsWith(ApplicationJsonConfiguration.EntryAssemblyName + "-", StringComparison.Ordinal)
                        && path.EndsWith(".log", StringComparison.Ordinal)
                        && !path.EndsWith(".log.gz", StringComparison.Ordinal));
            var fileText = File.ReadAllText(daily);
            Assert.Contains("backfiller-console-info-marker", fileText, StringComparison.Ordinal);
            Assert.Contains("backfiller-console-debug-marker", fileText, StringComparison.Ordinal);
            Assert.DoesNotContain("backfiller-file-trace-marker", fileText, StringComparison.Ordinal);
            TryDelete(logDir);
        }
        /// <summary>WriteLoggingInitialized emits information event without secrets via Serilog.</summary>

        [Fact]
        public void LoggingInitialized_ReachesSerilog_ThroughILogger()
        {
            var sink = new CollectingSink();
            using var host = CreateLoggingHost(configure: lc => lc.WriteTo.Sink(sink));

            BackFillerLoggingExtensions.WriteLoggingInitialized(
                host.Services,
                "Production",
                AppContext.BaseDirectory);

            var evt = Assert.Single(
                sink.Events,
                e => e.RenderMessage().Contains("Application logging initialized", StringComparison.Ordinal));
            Assert.Equal(LogEventLevel.Information, evt.Level);
            Assert.Contains("SerilogLoggerFactory", evt.RenderMessage(), StringComparison.Ordinal);
            Assert.Contains(BackFillerLogCategories.Hosting, evt.RenderMessage(), StringComparison.Ordinal);
            Assert.Contains(ApplicationJsonConfiguration.EntryAssemblyName, evt.RenderMessage(), StringComparison.Ordinal);
            Assert.DoesNotContain("Password", evt.RenderMessage(), StringComparison.OrdinalIgnoreCase);
        }
        /// <summary>Production references sink packages but not Serilog.Settings.Configuration or RabbitMQ sink package.</summary>

        [Fact]
        public void ProductionAssembly_ReferencesSinkPackages_NotSettingsConfiguration()
        {
            var names = typeof(BackFillerLoggingExtensions).Assembly
                .GetReferencedAssemblies()
                .Select(static a => a.Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert.Contains("Serilog.Sinks.Console", names);
            Assert.Contains("Serilog.Sinks.File", names);
            Assert.DoesNotContain("VectorNNTPD.BackFillerService", names);
            Assert.DoesNotContain("Serilog.Sinks.File.Archive", names);
            Assert.Contains("Serilog.Sinks.Async", names);
            Assert.Contains("Serilog.Sinks.Syslog", names);
            Assert.DoesNotContain("Serilog.Sinks.RabbitMQ", names);
            Assert.DoesNotContain("Serilog.Settings.Configuration", names);
        }
        /// <summary>Host logging works without Serilog:WriteTo configuration keys.</summary>

        [Fact]
        public void HostLogger_DoesNotRequireSerilogWriteToConfigurationKeys()
        {
            var logDir = CreateTempLogDir();
            try
            {
                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
                {
                    ContentRootPath = AppContext.BaseDirectory,
                });
                builder.Configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:File:{nameof(BackFillerFileLoggingTargetOptions.LogDir)}"] = logDir,
                    });
                builder.ConfigureBackFillerLogging();
                using (var host = builder.Build())
                {
                    var logger = host.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("VectorNNTPD.BackFillerService");
                    logger.LogInformation("no-serilog-writeto-keys-marker");
                }

                Log.CloseAndFlush();

                Assert.Contains(
                    Directory.GetFiles(logDir, ApplicationJsonConfiguration.EntryAssemblyName + "-*.log"),
                    path => File.ReadAllText(path).Contains("no-serilog-writeto-keys-marker", StringComparison.Ordinal));
            }
            finally
            {
                TryDelete(logDir);
            }
        }

        /// <summary>Builds a generic host with BackFiller logging configured from in-memory settings.</summary>
        /// <param name="logDir">Optional file log directory; otherwise a temp dir is created.</param>
        /// <param name="configure">Optional extra Serilog configuration.</param>
        /// <param name="settings">Additional configuration key/value pairs.</param>
        /// <param name="commandLine">Logging command-line switches.</param>
        /// <returns>Built host with logging pipeline registered.</returns>
        private static IHost CreateLoggingHost(
            string? logDir = null,
            Action<LoggerConfiguration>? configure = null,
            IReadOnlyDictionary<string, string?>? settings = null,
            BackFillerLoggingCommandLine commandLine = default)
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = AppContext.BaseDirectory,
            });
            var pairs = new Dictionary<string, string?>
            {
                [$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:File:{nameof(BackFillerFileLoggingTargetOptions.LogDir)}"] =
                    logDir ?? CreateTempLogDir(),
            };
            if (settings is not null)
            {
                foreach (var (key, value) in settings)
                {
                    pairs[key] = value;
                }
            }

            builder.Configuration.AddInMemoryCollection(pairs);
            builder.ConfigureBackFillerLogging(configure, commandLine);
            return builder.Build();
        }

        /// <summary>Creates a unique directory under the system temp folder for file sink tests.</summary>
        /// <returns>Existing empty directory path.</returns>
        private static string CreateTempLogDir()
        {
            var dir = Path.Join(Path.GetTempPath(), "vectornntp-bf-filelog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Walks upward from the test base directory to locate committed BackFiller JSON settings.</summary>
        /// <returns>Absolute path to <c>VectorNNTPD.BackFillerService.json</c>.</returns>
        /// <exception cref="FileNotFoundException">When the production settings file cannot be found.</exception>
        private static string FindProductionConfigurationFile()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = RelativePath.Under(dir.FullName, "src", "VectorNNTPD.BackFillerService", "VectorNNTPD.BackFillerService.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            throw new FileNotFoundException("Could not locate src/VectorNNTPD.BackFillerService/VectorNNTPD.BackFillerService.json.");
        }

        /// <summary>Best-effort recursive delete of a temp log directory; ignores files still in use.</summary>
        /// <param name="path">Directory to remove.</param>
        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                /* A directory that is still in use must not fail the test. */
            }
        }

        /// <summary>Reflects a private instance field across the inheritance chain for sink introspection.</summary>
        /// <param name="instance">Object hosting the field.</param>
        /// <param name="name">Field name.</param>
        /// <returns>Field value or null when absent.</returns>
        private static object? ReadInstanceField(object instance, string name)
        {
            for (var type = instance.GetType(); type is not null; type = type.BaseType)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field is not null)
                {
                    return field.GetValue(instance);
                }
            }

            return null;
        }

        /// <summary>Asserts the periodic file flush sink timer period is one second (1000 ms).</summary>
        /// <param name="sinks">Sink objects discovered from the logger graph.</param>
        private static void AssertFlushesOncePerSecond(IEnumerable<object> sinks)
        {
            var flush = Assert.Single(
                sinks,
                static n => n.GetType().Name.Equals("PeriodicFileFlushSink", StringComparison.Ordinal));
            Assert.Equal("VectorNNTPD.Common", flush.GetType().Assembly.GetName().Name);
            var timer = ReadInstanceField(flush, "_timer")
                        ?? throw new InvalidOperationException("PeriodicFlushToDiskSink._timer was not found.");
            var holder = timer.GetType().GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(timer)
                         ?? throw new InvalidOperationException("Timer._timer was not found.");
            var queueTimer = holder.GetType().GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(holder)
                             ?? throw new InvalidOperationException("TimerHolder._timer was not found.");
            var period = queueTimer.GetType().GetField("_period", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(queueTimer)
                         ?? throw new InvalidOperationException($"{queueTimer.GetType().FullName}._period was not found.");
            Assert.Equal(1000u, Assert.IsType<uint>(period));
        }

        /// <summary>Walks nested <see cref="ILogEventSink"/> references reachable from fields.</summary>
        /// <param name="root">Logger or wrapper to traverse.</param>
        /// <returns>Unique sink objects in discovery order.</returns>
        private static IEnumerable<object> WalkLogEventSinks(object root)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<object>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!seen.Add(current))
                {
                    continue;
                }

                yield return current;
                foreach (var value in current.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(field => field.GetValue(current)))
                {
                    if (value is ILogEventSink sink)
                    {
                        pending.Push(sink);
                        continue;
                    }

                    if (value is IEnumerable<ILogEventSink> sinks)
                    {
                        foreach (var inner in sinks)
                        {
                            pending.Push(inner);
                        }
                    }
                }
            }
        }

        /// <summary>Thread-safe in-memory sink for Serilog integration assertions.</summary>
        private sealed class CollectingSink : ILogEventSink
        {
            /// <summary>Events queued by host logging threads. <see cref="Events"/> copies them at read time.</summary>
            private readonly ConcurrentQueue<LogEvent> _events = new();

            /// <summary>Snapshot of queued events at read time.</summary>
            internal IReadOnlyCollection<LogEvent> Events => _events.ToArray();

            /// <inheritdoc />
            public void Emit(LogEvent logEvent)
            {
                ArgumentNullException.ThrowIfNull(logEvent);
                _events.Enqueue(logEvent);
            }
        }
    }
}
