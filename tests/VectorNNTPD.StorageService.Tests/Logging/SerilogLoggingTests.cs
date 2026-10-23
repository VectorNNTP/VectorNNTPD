using System.Collections.Concurrent;
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
using VectorNNTPD.StorageService.Logging;
using VectorNNTPD.StorageService.Tests.Fixtures;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.StorageService.Tests.Logging
{

    [Collection(SerilogCollection.Name)]
    public sealed class SerilogLoggingTests
    {
        [Fact]
        public void ConfigureStorageServerLogging_RegistersSerilog_AndRemovesDefaultProviders()
        {
            var builder = CreateLoggingBuilder();
            builder.ConfigureStorageServerLogging();

            using var host = builder.Build();

            var factory = host.Services.GetRequiredService<ILoggerFactory>();
            Assert.Equal("SerilogLoggerFactory", factory.GetType().Name);

            var providers = host.Services.GetLoggerProviders();
            Assert.DoesNotContain(providers, static p => p is ConsoleLoggerProvider);
            Assert.DoesNotContain(providers, static p => p is DebugLoggerProvider);
            Assert.DoesNotContain(
                providers,
                static p => p.GetType().Name.Contains("EventLog", StringComparison.Ordinal));
            Assert.Empty(providers);
        }

        [Fact]
        public void ConfigureStorageServerLogging_DoesNotRegisterDuplicateSerilogFactories()
        {
            var builder = CreateLoggingBuilder();
            builder.ConfigureStorageServerLogging();

            using var host = builder.Build();
            var factories = host.Services.GetServices<ILoggerFactory>().ToArray();
            Assert.Single(factories);
            Assert.Equal("SerilogLoggerFactory", factories[0].GetType().Name);
        }

        [Fact]
        public void ApplicationProperty_IsStorageServer_NotNntpd()
        {
            var sink = new CollectingSink();
            using var host = CreateSerilogHost(sink);

            var logger = host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger(StorageServerLogCategories.Hosting);
            logger.LogInformation("probe");

            var evt = Assert.Single(sink.Events, e => e.MessageTemplate.Text.Contains("probe", StringComparison.Ordinal));
            Assert.True(evt.Properties.TryGetValue("Application", out var application));
            Assert.Equal($"\"{ApplicationJsonConfiguration.EntryAssemblyName}\"", application.ToString());
            Assert.DoesNotContain("VectorNNTP.NNTPD", application.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void BootstrapLogger_UsesEntryAssemblyName()
        {
            var previous = Log.Logger;
            try
            {
                var name = ApplicationJsonConfiguration.EntryAssemblyName;
                Assert.False(string.IsNullOrWhiteSpace(name));
                Log.Logger = StorageServerLoggingExtensions.CreateBootstrapLogger();
                Assert.NotNull(Log.Logger);
                // Under VSTest the entry assembly is testhost, not the production host assembly.
                Assert.NotEqual("VectorNNTP.NNTPD", name);
                Assert.DoesNotContain("VectorNNTP.NNTPD", name, StringComparison.Ordinal);
            }
            finally
            {
                Log.Logger = previous;
            }
        }

        [Fact]
        public void ProductionAppsettings_DeclaresStorageServerIdentityAndDebugLevels()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FindProductionAppsettings()));
            var serilog = doc.RootElement.GetProperty("Serilog");
            Assert.False(serilog.TryGetProperty("Properties", out _));
            Assert.DoesNotContain(
                "VectorNNTP.NNTPD",
                serilog.GetRawText(),
                StringComparison.Ordinal);

            Assert.Equal(
                "Debug",
                serilog.GetProperty("MinimumLevel").GetProperty("Override")
                    .GetProperty("VectorNNTPD.StorageService").GetString());

            var console = serilog.GetProperty("WriteTo")[0];
            Assert.Equal("Console", console.GetProperty("Name").GetString());
            Assert.Equal(
                "Debug",
                console.GetProperty("Args").GetProperty("restrictedToMinimumLevel").GetString());

            var file = serilog.GetProperty("WriteTo")[1].GetProperty("Args").GetProperty("configure")[0];
            Assert.Equal(
                "Debug",
                file.GetProperty("Args").GetProperty("restrictedToMinimumLevel").GetString());
        }

        [Fact]
        public void LoggingConfiguration_LoadsWithoutErrors()
        {
            var logDir = Path.Join(Path.GetTempPath(), "vectornntp-ss-serilog-load", Guid.NewGuid().ToString("N"));
            try
            {
                using var configuration = new ConfigurationManager();
                configuration.AddJsonFile(FindProductionAppsettings(), optional: false, reloadOnChange: false);
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["StorageServer:LogDir"] = logDir,
                        ["StorageServer:ApplicationName"] = ApplicationJsonConfiguration.EntryAssemblyName,
                    });
                StorageServerFileLogging.BindResolvedFilePath(configuration);

                using var logger = new LoggerConfiguration()
                    .ReadFrom.Configuration(configuration)
                    .CreateLogger();

                logger.Information("configuration-load-ok");
            }
            finally
            {
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }

        [Fact]
        public void CloseAndFlush_IsIdempotent()
        {
            var previous = Log.Logger;
            try
            {
                Log.Logger = new LoggerConfiguration()
                    .WriteTo.Sink(new CollectingSink())
                    .CreateLogger();
                Log.Information("flush-probe");
                Log.CloseAndFlush();
                Log.CloseAndFlush();
            }
            finally
            {
                Log.Logger = previous;
            }
        }

        private static HostApplicationBuilder CreateLoggingBuilder()
        {
            var builder = Host.CreateApplicationBuilder([]);
            builder.Configuration.AddInMemoryCollection(StorageServerTestOptions.CreateValidConfigurationPairs());
            return builder;
        }

        private static IHost CreateSerilogHost(CollectingSink sink)
        {
            var builder = CreateLoggingBuilder();
            foreach (var pair in StorageServerFileLogging.AsyncFileWriteToKeys())
            {
                builder.Configuration[pair.Key] = pair.Value;
            }

            builder.Configuration["Serilog:WriteTo:0:Name"] = "Console";
            builder.Configuration["Serilog:WriteTo:0:Args:restrictedToMinimumLevel"] = "Debug";
            builder.Configuration["Serilog:WriteTo:0:Args:outputTemplate"] =
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
            builder.Configuration["Serilog:Properties:Application"] = ApplicationJsonConfiguration.EntryAssemblyName;

            builder.ConfigureStorageServerLogging(lc =>
            {
                lc.MinimumLevel.Verbose();
                lc.WriteTo.Sink(sink);
            });
            return builder.Build();
        }

        private static string FindProductionAppsettings()
        {
            var start = new DirectoryInfo(AppContext.BaseDirectory);
            for (var dir = start; dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Join(dir.FullName, "src", "VectorNNTPD.StorageService", "VectorNNTPD.StorageService.json");
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
    }

    internal sealed class CollectingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            _events.Enqueue(logEvent);
        }
    }
}
