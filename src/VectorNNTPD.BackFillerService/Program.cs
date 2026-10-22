using Serilog;
using Serilog.Debugging;
using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Logging;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Core;

/// <summary>Process entry point. Builds the Generic Host from the entry-assembly directory and runs it.</summary>
internal class Program
{
    /// <summary>
    /// Starts the host, or runs startup smoke when that environment variable is set, and returns the process exit code.
    /// </summary>
    /// <param name="args">
    /// Command-line arguments. Logging flags are read here; the same array is passed to the Generic Host builder.
    /// </param>
    /// <returns>
    /// Zero when the host stops cleanly or startup smoke completes. <see cref="BackFillerProcessBoundary.FailureExitCode"/>
    /// when the host throws. Logger shutdown failures do not change a clean exit.
    /// </returns>
    /// <remarks>
    /// Content root is <see cref="AppContext.BaseDirectory"/>. A host exception is logged and does not escape.
    /// Startup smoke builds and disposes the host without calling <c>RunAsync</c>.
    /// </remarks>
    private static async Task<int> Main(string[] args)
    {
        var loggingCommandLine = BackFillerLoggingCommandLine.FromArguments(args);
        if (loggingCommandLine.Console)
        {
            BackFillerLoggingExtensions.UseAutoFlushConsoleOutput();
        }

        Log.Logger = BackFillerLoggingExtensions.CreateBootstrapLogger(loggingCommandLine);

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
                BackFillerJsonFiles.SharedRabbitMqFileName);
            var startupSmoke = BackFillerStartupSmoke.IsRequested(
                Environment.GetEnvironmentVariable(BackFillerStartupSmoke.EnvironmentVariable));
            if (startupSmoke)
            {
                BackFillerStartupSmoke.UseIsolatedConfiguration(builder.Configuration);
            }

            builder.ConfigureBackFillerLogging(commandLine: loggingCommandLine);
            builder.ConfigureBackFillerPlatformHosting();
            builder.AddBackFillerHosting();

            var host = builder.Build();
            BackFillerLoggingExtensions.WriteLoggingInitialized(
                host.Services,
                builder.Environment.EnvironmentName,
                builder.Environment.ContentRootPath);
            if (startupSmoke)
            {
                exitCode = BackFillerStartupSmoke.Complete(host, Console.Out);
            }
            else
            {
                await host.RunAsync().ConfigureAwait(false);
                Log.Information("{Application} host stopped cleanly", ApplicationJsonConfiguration.EntryAssemblyName);
            }
        }
        // codeql[cs/catch-of-all-exceptions] The process boundary must log every host failure and still return the documented exit code.
        catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
        {
            exitCode = BackFillerProcessBoundary.ObserveHostFailure(ex);
        }
        finally
        {
            exitCode = await BackFillerProcessBoundary.FinishAsync(exitCode).ConfigureAwait(false);
        }

        return exitCode;
    }
}

/// <summary>
/// Final process boundary for host failures that escape <see cref="Program"/>.
/// </summary>
/// <remarks>
/// Every exception, including NNTP database authentication and availability failures, ACME and Cloudflare
/// failures, configuration validation, and cancellation, is logged and becomes exit code
/// <see cref="FailureExitCode"/>. A failure while writing that log is reported through <see cref="SelfLog"/>
/// and still returns <see cref="FailureExitCode"/>. Logger shutdown runs after that result is chosen.
/// A logger-shutdown failure is written only to <see cref="SelfLog"/> and never changes the chosen exit code,
/// including when the host stopped cleanly. Console restoration still runs after either outcome.
/// </remarks>
internal static class BackFillerProcessBoundary
{
    /// <summary>Exit code returned after a host failure.</summary>
    internal const int FailureExitCode = 1;

    /// <summary>
    /// Logs <paramref name="exception"/> as a fatal host failure and returns <see cref="FailureExitCode"/>.
    /// </summary>
    /// <param name="exception">Failure that escaped the host run.</param>
    /// <returns><see cref="FailureExitCode"/>.</returns>
    internal static int ObserveHostFailure(Exception exception) =>
        ObserveHostFailure(
            exception,
            static failure => Log.Fatal(
                failure,
                "{Application} host terminated unexpectedly",
                ApplicationJsonConfiguration.EntryAssemblyName));

    /// <summary>
    /// Passes <paramref name="exception"/> to <paramref name="logFatal"/> and returns <see cref="FailureExitCode"/>.
    /// </summary>
    /// <param name="exception">Failure that escaped the host run.</param>
    /// <param name="logFatal">Fatal log writer. A throw from this callback is reported through <see cref="SelfLog"/>.</param>
    /// <returns><see cref="FailureExitCode"/>.</returns>
    internal static int ObserveHostFailure(Exception exception, Action<Exception> logFatal)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(logFatal);
        try
        {
            logFatal(exception);
        }
        // codeql[cs/catch-of-all-exceptions] The process boundary must log every host failure and still return the documented exit code.
        catch (Exception logFailure) when (!ExceptionHelpers.IsFatal(logFailure))
        {
            try
            {
                SelfLog.WriteLine("BackFiller host failure log failed: {0}", logFailure);
            }
            // codeql[cs/catch-of-all-exceptions] The process boundary must log every host failure and still return the documented exit code.
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                // The process boundary must still return the failure exit code.
            }
        }

        return FailureExitCode;
    }

    /// <summary>
    /// Closes the bootstrap logger, then restores console output, and returns <paramref name="exitCode"/>.
    /// </summary>
    /// <param name="exitCode">Result already chosen for the host run. Zero means the host stopped cleanly.</param>
    /// <returns>
    /// <paramref name="exitCode"/> unchanged. Logger shutdown failures are recorded on <see cref="SelfLog"/> only.
    /// </returns>
    internal static Task<int> FinishAsync(int exitCode) =>
        FinishAsync(
            exitCode,
            static () => Log.CloseAndFlushAsync().AsTask(),
            BackFillerLoggingExtensions.RestoreConsoleOutput);

    /// <summary>
    /// Runs <paramref name="closeAndFlush"/> and then <paramref name="restoreConsole"/>.
    /// </summary>
    /// <param name="exitCode">Result already chosen for the host run.</param>
    /// <param name="closeAndFlush">Logger shutdown. A throw is contained and does not change <paramref name="exitCode"/>.</param>
    /// <param name="restoreConsole">Console restoration. It runs even when <paramref name="closeAndFlush"/> throws.</param>
    /// <returns><paramref name="exitCode"/> unchanged.</returns>
    internal static async Task<int> FinishAsync(int exitCode, Func<Task> closeAndFlush, Action restoreConsole)
    {
        ArgumentNullException.ThrowIfNull(closeAndFlush);
        ArgumentNullException.ThrowIfNull(restoreConsole);
        try
        {
            await closeAndFlush().ConfigureAwait(false);
        }
        // codeql[cs/catch-of-all-exceptions] Logger shutdown must not replace the process result or skip console restoration.
        catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
        {
            ObserveShutdownFailure(exitCode, ex);
        }
        finally
        {
            try
            {
                restoreConsole();
            }
            // codeql[cs/catch-of-all-exceptions] Console restoration must not replace the process result.
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                try
                {
                    SelfLog.WriteLine("BackFiller console restore failed: {0}", ex);
                }
                // codeql[cs/catch-of-all-exceptions] Console restoration must not replace the process result.
                catch (Exception selfLogFailure) when (!ExceptionHelpers.IsFatal(selfLogFailure))
                {
                    // The process boundary must still return the chosen exit code.
                }
            }
        }

        return exitCode;
    }

    /// <summary>
    /// Records a logger-shutdown failure on <see cref="SelfLog"/> without changing <paramref name="exitCode"/>.
    /// </summary>
    /// <param name="exitCode">Exit code chosen before logger shutdown. Used only to select the diagnostic text.</param>
    /// <param name="exception">Failure from logger shutdown.</param>
    /// <remarks>
    /// Does not call <see cref="ObserveHostFailure(Exception)"/>: that path writes through Serilog's fatal logger,
    /// which may re-enter a failing file sink. SelfLog is the only safe channel once logger disposal has failed.
    /// </remarks>
    private static void ObserveShutdownFailure(int exitCode, Exception exception)
    {
        var message = exitCode == 0
            ? "BackFiller logger shutdown failed after a clean host run: {0}"
            : "BackFiller logger shutdown failed after a host failure: {0}";
        try
        {
            SelfLog.WriteLine(message, exception);
        }
        // codeql[cs/catch-of-all-exceptions] A secondary shutdown diagnostic must not replace the process result.
        catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
        {
            // The chosen exit code remains the process result.
        }
    }
}
