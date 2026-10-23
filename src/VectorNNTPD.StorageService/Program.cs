using Serilog;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Hosting;
using VectorNNTPD.StorageService.Logging;

/// <summary>Process entry point. Builds the Generic Host from the entry-assembly directory and runs it.</summary>
internal static class Program
{
    /// <summary>
    /// Starts the host, or runs startup smoke when that environment variable is set, and returns the process exit code.
    /// </summary>
    /// <param name="args">Command-line arguments passed to the Generic Host builder.</param>
    /// <returns>
    /// Zero when the host stops cleanly or startup smoke completes. One when the host throws.
    /// </returns>
    /// <remarks>
    /// Content root is <see cref="AppContext.BaseDirectory"/>. Startup smoke builds and disposes the host
    /// without calling <c>RunAsync</c>.
    /// </remarks>
    private static async Task<int> Main(string[] args)
    {
        StorageServerLoggingExtensions.UseAutoFlushConsoleOutput();
        Log.Logger = StorageServerLoggingExtensions.CreateBootstrapLogger();

        var exitCode = 0;
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
                "VectorNNTPD.RabbitMQ.json");

            var startupSmoke = StorageServerStartupSmoke.IsRequested(
                Environment.GetEnvironmentVariable(StorageServerStartupSmoke.EnvironmentVariable));
            if (startupSmoke)
            {
                StorageServerStartupSmoke.UseIsolatedConfiguration(builder.Configuration);
            }

            builder.ConfigureStorageServerLogging();
            builder.ConfigureStorageServerPlatformHosting();
            builder.AddStorageServerHosting();

            var host = builder.Build();
            StorageServerLoggingExtensions.WriteLoggingInitialized(
                host.Services,
                builder.Environment.EnvironmentName,
                builder.Environment.ContentRootPath);
            if (startupSmoke)
            {
                exitCode = StorageServerStartupSmoke.Complete(host, Console.Out);
            }
            else
            {
                await host.RunAsync().ConfigureAwait(false);
                Log.Information("{Application} host stopped cleanly", ApplicationJsonConfiguration.EntryAssemblyName);
            }
        }
        catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
        {
            Log.Fatal(ex, "{Application} host terminated unexpectedly", ApplicationJsonConfiguration.EntryAssemblyName);
            exitCode = 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }

        return exitCode;
    }
}
