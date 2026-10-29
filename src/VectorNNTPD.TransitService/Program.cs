using Serilog;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Hosting;
using VectorNNTPD.TransitService.Logging;
using VectorNNTPD.TransitService.Session.CommandProcessor;

namespace VectorNNTPD.TransitService
{
    /// <summary>Process entry point for the NNTP transit-server host.</summary>
    internal static class Program
    {
        /// <summary>
        /// Builds and runs the transit Generic Host, or runs startup smoke when that environment variable is set.
        /// </summary>
        /// <param name="args">Command-line arguments passed to the host builder.</param>
        /// <returns>Zero on clean shutdown or successful smoke; one on unexpected termination.</returns>
        private static async Task<int> Main(string[] args)
        {
            NntpdLoggingExtensions.UseAutoFlushConsoleOutput();
            Log.Logger = NntpdLoggingExtensions.CreateBootstrapLogger();

            try
            {
                Log.Information("{Application} host starting", ApplicationJsonConfiguration.EntryAssemblyName);

                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
                {
                    Args = args,
                    ContentRootPath = AppContext.BaseDirectory,
                });
                builder.Environment.ContentRootPath = AppContext.BaseDirectory;
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(
                    builder.Configuration,
                    builder.Environment.EnvironmentName);
                ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(
                    builder.Configuration,
                    builder.Environment.EnvironmentName,
                    NntpServerJsonFiles.SharedRabbitMqFileName);

                var startupSmoke = TransitStartupSmoke.IsRequested(
                    Environment.GetEnvironmentVariable(TransitStartupSmoke.EnvironmentVariable));
                if (startupSmoke)
                {
                    TransitStartupSmoke.UseIsolatedConfiguration(builder.Configuration);
                }

                builder.ConfigureNntpdLogging();
                builder.ConfigureNntpdPlatformHosting();
                builder.Services.AddNntpTransitHosting();

                var host = builder.Build();
                NntpCommandLoggers.Configure(host.Services.GetRequiredService<ILoggerFactory>());
                NntpdLoggingExtensions.WriteLoggingInitialized(
                    host.Services,
                    builder.Environment.EnvironmentName,
                    builder.Environment.ContentRootPath);
                if (startupSmoke)
                {
                    return TransitStartupSmoke.Complete(host, Console.Out);
                }

                await host.RunAsync().ConfigureAwait(false);

                Log.Information("{Application} host stopped cleanly", ApplicationJsonConfiguration.EntryAssemblyName);
                return 0;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                Log.Fatal(ex, "{Application} host terminated unexpectedly", ApplicationJsonConfiguration.EntryAssemblyName);
                return 1;
            }
            finally
            {
                await Log.CloseAndFlushAsync().ConfigureAwait(false);
            }
        }
    }
}
