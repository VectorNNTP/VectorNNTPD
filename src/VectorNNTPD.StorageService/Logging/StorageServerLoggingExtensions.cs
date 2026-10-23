using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Serilog.Events;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.StorageService.Logging
{

    /// <summary>
    /// Configures Serilog as the exclusive logging implementation for VectorNNTPD.StorageService.
    /// </summary>
    public static class StorageServerLoggingExtensions
    {
        /// <summary>
        /// Single-line console template suitable for interactive terminals and journald collection.
        /// </summary>
        public const string ConsoleOutputTemplate =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

        /// <summary>
        /// Creates an early bootstrap logger used before the Generic Host is built.
        /// </summary>
        /// <returns>A bootstrap logger assigned to <see cref="Log.Logger"/>.</returns>
        public static Serilog.ILogger CreateBootstrapLogger()
        {
            return new LoggerConfiguration()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", ApplicationJsonConfiguration.EntryAssemblyName)
                .WriteTo.Console(outputTemplate: ConsoleOutputTemplate)
                .CreateBootstrapLogger();
        }

        /// <summary>Writer installed by <see cref="UseAutoFlushConsoleOutput"/> for process lifetime.</summary>
        private static StreamWriter? _autoFlushConsoleOut;

        /// <summary><c>1</c> after auto-flush stdout is installed.</summary>
        private static int _consoleOutConfigured;

        /// <summary>
        /// Makes <see cref="Console.Out"/> flush every write.
        /// </summary>
        /// <remarks>
        /// When stdout is a console, the runtime already flushes often enough that Information
        /// lines appear immediately. When stdout is a pipe (IDE, redirected capture),
        /// <see cref="Console.Out"/> is block-buffered: a startup burst fills the buffer and
        /// becomes visible, then later Information events sit unseen until the buffer fills
        /// again or the process exits. Serilog's Console sink writes to <see cref="Console.Out"/>
        /// and does not Flush. This must run before the first Serilog Console sink is created.
        /// The writer leaves the standard output stream open (<c>leaveOpen: true</c>) and is
        /// retained for process lifetime so teardown does not close stdout. A second call does nothing.
        /// </remarks>
        public static void UseAutoFlushConsoleOutput()
        {
            if (Interlocked.Exchange(ref _consoleOutConfigured, 1) != 0)
            {
                return;
            }

            // Assign to the process-lifetime field immediately so ownership is not a local IDisposable.
            _autoFlushConsoleOut = new StreamWriter(
                Console.OpenStandardOutput(),
                Console.OutputEncoding,
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };
            Console.SetOut(_autoFlushConsoleOut);
        }

        /// <summary>
        /// Removes Microsoft default logging providers and registers Serilog as the sole provider.
        /// </summary>
        /// <param name="builder">The host application builder.</param>
        /// <param name="configure">
        /// Optional additional Serilog configuration applied after reading <c>appsettings</c>.
        /// Used by tests to attach in-memory sinks without reintroducing MEL providers.
        /// </param>
        /// <returns>The same <paramref name="builder"/> instance.</returns>
        /// <remarks>
        /// <para>
        /// Call order: clear MEL providers, remove default <see cref="ILoggerFactory"/> registrations,
        /// then <c>AddSerilog</c> with <c>writeToProviders: false</c> so framework and application
        /// <see cref="ILogger{T}"/> traffic flows only through Serilog sinks.
        /// </para>
        /// <para>
        /// Console formatting is owned by Serilog. journald collects Serilog console stdout under
        /// systemd. Microsoft console formatter options that <c>AddSystemd()</c> may register are
        /// stripped by platform hosting and are not part of this logging pipeline.
        /// </para>
        /// </remarks>
        public static HostApplicationBuilder ConfigureStorageServerLogging(
            this HostApplicationBuilder builder,
            Action<LoggerConfiguration>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Logging.ClearProviders();

            // Host.CreateApplicationBuilder registers a default LoggerFactory; remove it so
            // SerilogLoggerFactory is the only ILoggerFactory in the container.
            builder.Services.RemoveAll<ILoggerFactory>();
            builder.Services.RemoveAll<ILoggerProvider>();

            StorageServerFileLogging.BindResolvedFilePath(builder.Configuration);

            builder.Services.AddSerilog(
                (services, loggerConfiguration) =>
                {
                    loggerConfiguration
                        .ReadFrom.Configuration(StorageServerConfiguredSinks.WithoutWriteTo(builder.Configuration))
                        .ReadFrom.Services(services)
                        .Enrich.FromLogContext()
                        .Enrich.WithProperty("Application", ApplicationJsonConfiguration.EntryAssemblyName);

                    StorageServerConfiguredSinks.Apply(loggerConfiguration, builder.Configuration);
                    configure?.Invoke(loggerConfiguration);
                },
                preserveStaticLogger: false,
                writeToProviders: false);

            return builder;
        }

        /// <summary>
        /// Emits one Information event through the host <see cref="ILoggerFactory"/> after
        /// Serilog has replaced the bootstrap logger.
        /// </summary>
        /// <param name="services">The built host service provider.</param>
        /// <param name="environmentName">Host environment name (no secrets).</param>
        /// <param name="contentRootPath">Resolved content root used for <c>VectorNNTPD.StorageService.json</c>.</param>
        public static void WriteLoggingInitialized(
            IServiceProvider services,
            string environmentName,
            string contentRootPath)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
            ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

            var factory = services.GetRequiredService<ILoggerFactory>();
            var logger = factory.CreateLogger(StorageServerLogCategories.Hosting);
            HostingLogMessages.LoggingInitialized(
                logger,
                ApplicationJsonConfiguration.EntryAssemblyName,
                factory.GetType().Name,
                StorageServerLogCategories.Hosting,
                environmentName,
                contentRootPath);
        }

        /// <summary>
        /// Returns registered <see cref="ILoggerProvider"/> implementations for diagnostics and tests.
        /// </summary>
        /// <remarks>
        /// When Serilog is configured via <c>AddSerilog</c>, logging is provided by replacing
        /// <see cref="ILoggerFactory"/> with <c>SerilogLoggerFactory</c>. In that mode the provider
        /// list is typically empty, which confirms Microsoft default providers were not retained.
        /// </remarks>
        public static IReadOnlyList<ILoggerProvider> GetLoggerProviders(this IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(services);
            return services.GetServices<ILoggerProvider>().ToArray();
        }
    }
}
