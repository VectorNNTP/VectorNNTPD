using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Logging;
using VectorNNTPD.StorageService.Tests.Fixtures;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.StorageService.Tests.Logging
{

    [Collection(SerilogCollection.Name)]
    public sealed class FileLoggingTests
    {
        [Fact]
        public void ProductionAppsettings_DeclaresAsyncFileSinkContract()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FindProductionAppsettings()));
            var async = doc.RootElement.GetProperty("Serilog").GetProperty("WriteTo")[1];
            Assert.Equal("Async", async.GetProperty("Name").GetString());
            Assert.Equal(50000, async.GetProperty("Args").GetProperty("bufferSize").GetInt32());
            Assert.True(async.GetProperty("Args").GetProperty("blockWhenFull").GetBoolean());
            var file = async.GetProperty("Args").GetProperty("configure")[0];
            Assert.Equal("File", file.GetProperty("Name").GetString());
            var args = file.GetProperty("Args");
            Assert.Equal("Debug", args.GetProperty("restrictedToMinimumLevel").GetString());
            Assert.Equal("Day", args.GetProperty("rollingInterval").GetString());
            Assert.Equal(14, args.GetProperty("retainedFileCountLimit").GetInt32());
            Assert.True(args.GetProperty("buffered").GetBoolean());
            Assert.Equal("00:00:01", args.GetProperty("flushToDiskInterval").GetString());
            Assert.False(args.GetProperty("rollOnFileSizeLimit").GetBoolean());
            Assert.Equal(JsonValueKind.Null, args.GetProperty("fileSizeLimitBytes").ValueKind);
            Assert.False(args.TryGetProperty("hooks", out _));
            Assert.Equal(
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                args.GetProperty("outputTemplate").GetString());
            Assert.Equal("/logs/pending-.log", args.GetProperty("path").GetString());
            Assert.DoesNotContain("VectorNNTP.NNTPD", args.GetProperty("path").GetString(), StringComparison.Ordinal);

            var usingNames = doc.RootElement.GetProperty("Serilog").GetProperty("Using")
                .EnumerateArray().Select(static e => e.GetString()).ToArray();
            Assert.Contains("Serilog.Sinks.File", usingNames);
            Assert.Contains("Serilog.Sinks.Async", usingNames);
            Assert.DoesNotContain("Serilog.Sinks.File.Archive", usingNames);
            Assert.Equal("-.log", StorageServerFileLogging.RollingPathSuffix);
            Assert.Equal(".gz", StorageServerFileLogging.GzipArchiveSuffix);
            Assert.Equal("/logs", StorageServerOptions.DefaultLogDir);
        }

        [Fact]
        public void BindResolvedFilePath_OverwritesProductionJsonPlaceholderFromLogDir()
        {
            var logDir = CreateTempLogDir();
            try
            {
                var configuration = new ConfigurationManager();
                configuration.AddJsonFile(FindProductionAppsettings(), optional: false, reloadOnChange: false);
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [$"{StorageServerOptions.SectionName}:{nameof(StorageServerOptions.LogDir)}"] = logDir,
                        [$"{StorageServerOptions.SectionName}:{nameof(StorageServerOptions.ApplicationName)}"] =
                            "not-the-entry-assembly",
                    });

                StorageServerFileLogging.BindResolvedFilePath(configuration);

                var expected = StorageServerFileLogging.RollingFilePath(
                    logDir,
                    ApplicationJsonConfiguration.EntryAssemblyName);
                Assert.Equal(expected, configuration["Serilog:WriteTo:1:Args:configure:0:Args:path"]);
                Assert.DoesNotContain("not-the-entry-assembly", expected, StringComparison.Ordinal);
                Assert.Equal(expected, configuration["Serilog:WriteTo:1:Args:configure:0:Args:path"]);
                Assert.DoesNotContain("not-the-entry-assembly", expected, StringComparison.Ordinal);
                Assert.True(Directory.Exists(logDir));
                Assert.DoesNotContain("VectorNNTP.NNTPD", expected, StringComparison.Ordinal);
            }
            finally
            {
                TryDelete(logDir);
            }
        }

        [Fact]
        public void ResolveDirectory_UsesDefaultLogsAgainstApplicationBase()
        {
            var baseDir = Path.Combine(Path.GetTempPath(), "vectornntp-ss-log-base", Guid.NewGuid().ToString("N"));
            var resolved = StorageServerFileLogging.ResolveDirectory(StorageServerOptions.DefaultLogDir, baseDir);
            Assert.Equal(
                ApplicationLocalPath.ResolveApplicationLocalPath(
                    StorageServerOptions.DefaultLogDir,
                    baseDir),
                resolved);
            Assert.Equal("/logs", StorageServerOptions.DefaultLogDir);
            Assert.True(Path.IsPathRooted(resolved));
            // Absolute /logs must not be rewritten under the application base directory.
            var normalizedBase = Path.GetFullPath(baseDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Assert.False(
                resolved.StartsWith(normalizedBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || resolved.StartsWith(normalizedBase + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(resolved, normalizedBase, StringComparison.OrdinalIgnoreCase));

            var absolute = Path.Combine(Path.GetTempPath(), "vectornntp-ss-logdir-abs");
            Assert.Equal(Path.GetFullPath(absolute), StorageServerFileLogging.ResolveDirectory(absolute));
        }

        [Fact]
        public void RollingFilePath_UsesStorageServerApplicationName()
        {
            var path = StorageServerFileLogging.RollingFilePath("/logs", "VectorNNTPD.StorageService");
            Assert.Equal(Path.Combine("/logs", "VectorNNTPD.StorageService-.log"), path);
            Assert.DoesNotContain("VectorNNTP.NNTPD", path, StringComparison.Ordinal);
        }

        [Fact]
        public void ProductionJson_BindsUnlimitedDailyAsyncGzipFileSink()
        {
            var logDir = CreateTempLogDir();
            try
            {
                var configuration = new ConfigurationManager();
                configuration.AddJsonFile(FindProductionAppsettings(), optional: false, reloadOnChange: false);
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [$"{StorageServerOptions.SectionName}:{nameof(StorageServerOptions.LogDir)}"] = logDir,
                        [$"{StorageServerOptions.SectionName}:{nameof(StorageServerOptions.ApplicationName)}"] =
                            ApplicationJsonConfiguration.EntryAssemblyName,
                    });
                StorageServerFileLogging.BindResolvedFilePath(configuration);

                var loggerConfiguration = new LoggerConfiguration();
                StorageServerConfiguredSinks.Apply(loggerConfiguration, configuration);
                using var logger = loggerConfiguration.CreateLogger();

                var sinks = WalkLogEventSinks(logger).ToArray();
                var fileSink = Assert.Single(
                    sinks,
                    static n => n.GetType().Name.Equals("GzipRollingFileSink", StringComparison.Ordinal));
                Assert.Null(ReadInstanceField(fileSink, "_fileSizeLimitBytes"));
                Assert.False(Assert.IsType<bool>(ReadInstanceField(fileSink, "_rollOnFileSizeLimit")!));
                Assert.Equal(14, ReadInstanceField(fileSink, "_retainedFileCountLimit"));
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
                Assert.Equal(50000, Convert.ToInt32(boundedCapacity, CultureInfo.InvariantCulture));
                AssertFlushesOncePerSecond(sinks);
            }
            finally
            {
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }

        [Fact]
        public void ConfigureStorageServerLogging_ResolvesFilePathUnderLogsStorage()
        {
            var logDir = CreateTempLogDir();
            try
            {
                var builder = Host.CreateApplicationBuilder([]);
                foreach (var pair in StorageServerTestOptions.CreateValidConfigurationPairs())
                {
                    builder.Configuration[pair.Key] = pair.Value;
                }

                builder.Configuration[$"{StorageServerOptions.SectionName}:LogDir"] = logDir;
                builder.Configuration[$"{StorageServerOptions.SectionName}:ApplicationName"] =
                    ApplicationJsonConfiguration.EntryAssemblyName;
                foreach (var pair in StorageServerFileLogging.AsyncFileWriteToKeys())
                {
                    builder.Configuration[pair.Key] = pair.Value;
                }

                builder.Configuration["Serilog:WriteTo:0:Name"] = "Console";
                builder.Configuration["Serilog:WriteTo:0:Args:restrictedToMinimumLevel"] = "Debug";
                builder.Configuration["Serilog:WriteTo:0:Args:outputTemplate"] =
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
                builder.Configuration["Serilog:Properties:Application"] = ApplicationJsonConfiguration.EntryAssemblyName;

                builder.ConfigureStorageServerLogging();
                using var host = builder.Build();

                var bound = builder.Configuration["Serilog:WriteTo:1:Args:configure:0:Args:path"];
                Assert.Equal(
                    StorageServerFileLogging.RollingFilePath(logDir, ApplicationJsonConfiguration.EntryAssemblyName),
                    bound);
                Assert.DoesNotContain("VectorNNTP.NNTPD", bound, StringComparison.Ordinal);
            }
            finally
            {
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }

        private static string CreateTempLogDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "vectornntp-ss-file-logging", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string FindProductionAppsettings()
        {
            var start = new DirectoryInfo(AppContext.BaseDirectory);
            for (var dir = start; dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "src", "VectorNNTPD.StorageService", "VectorNNTPD.StorageService.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("Could not locate StorageServer VectorNNTPD.StorageService.json.");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }

        private static object? ReadInstanceField(object instance, string name)
        {
            for (var type = instance.GetType(); type is not null; type = type.BaseType)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field is not null)
                {
                    return field.GetValue(instance);
                }
            }

            return null;
        }

        private static void AssertFlushesOncePerSecond(IEnumerable<object> sinks)
        {
            var flush = Assert.Single(
                sinks,
                static n => n.GetType().Name.Equals("PeriodicFileFlushSink", StringComparison.Ordinal));
            Assert.Equal("VectorNNTPD.Common", flush.GetType().Assembly.GetName().Name);
            var timer = ReadInstanceField(flush, "_timer")
                        ?? throw new InvalidOperationException("PeriodicFileFlushSink._timer was not found.");
            var holder = timer.GetType().GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(timer)
                         ?? throw new InvalidOperationException("Timer._timer was not found.");
            var queueTimer = holder.GetType().GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(holder)
                             ?? throw new InvalidOperationException("TimerHolder._timer was not found.");
            var period = queueTimer.GetType().GetField("_period", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(queueTimer)
                         ?? throw new InvalidOperationException($"{queueTimer.GetType().FullName}._period was not found.");
            Assert.Equal(1000u, Assert.IsType<uint>(period));
        }

        private static IEnumerable<object> WalkLogEventSinks(Serilog.ILogger logger)
        {
            var sinkField = logger.GetType().GetField("_sink", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException("Logger._sink was not found.");
            var root = sinkField.GetValue(logger)
                       ?? throw new InvalidOperationException("Logger._sink was null.");
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<object>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!seen.Add(current))
                {
                    continue;
                }

                yield return current;
                foreach (var child in EnumerateChildSinks(current))
                {
                    stack.Push(child);
                }
            }
        }

        private static IEnumerable<object> EnumerateChildSinks(object sink)
        {
            foreach (var field in sink.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (field.FieldType == typeof(ILogEventSink) || typeof(ILogEventSink).IsAssignableFrom(field.FieldType))
                {
                    if (field.GetValue(sink) is { } child)
                    {
                        yield return child;
                    }
                }
                else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(field.FieldType)
                         && field.FieldType != typeof(string))
                {
                    if (field.GetValue(sink) is System.Collections.IEnumerable enumerable)
                    {
                        foreach (var item in enumerable)
                        {
                            if (item is ILogEventSink childSink)
                            {
                                yield return childSink;
                            }
                        }
                    }
                }
            }
        }
    }
}
