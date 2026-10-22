using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTPD.StorageService.Configuration;

namespace VectorNNTPD.StorageService.Hosting
{
    /// <summary>
    /// Builds the real host and stops after configuration binding, without starting hosted services.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set <see cref="EnvironmentVariable"/> to <see cref="EnabledValue"/> and
    /// <see cref="ConfigurationPathVariable"/> to an isolated JSON file. <see cref="Program"/> then uses
    /// <see cref="UseIsolatedConfiguration"/>, builds the host through the normal registration path, and calls
    /// <see cref="Complete"/>. It does not call <c>IHost.RunAsync</c>, so database, RabbitMQ, DNS, ACME, and
    /// listener services are not started.
    /// </para>
    /// <para>
    /// Success writes one stdout line beginning with <see cref="CheckpointPrefix"/> and returns 0. That proves
    /// configuration binding and options validation for <see cref="StorageServerOptions"/> when its
    /// <c>IOptions</c> value is resolved. It does not run hosted-service <c>ValidateOnStart</c>,
    /// <c>IHost.StartAsync</c>, or graceful shutdown, and it does not prove a live broker, database,
    /// certificate issuance, or a bound listener. <see cref="StorageServerRuntimeOptions"/> is not required
    /// because that snapshot is published only after nntpsharedconfig startup.
    /// </para>
    /// </remarks>
    internal static class StorageServerStartupSmoke
    {
        /// <summary>Environment variable that selects startup smoke. The value must be <see cref="EnabledValue"/>.</summary>
        internal const string EnvironmentVariable = "VECTORNNTP_STORAGESERVICE_STARTUP_SMOKE";

        /// <summary>The only value of <see cref="EnvironmentVariable"/> that selects startup smoke.</summary>
        internal const string EnabledValue = "1";

        /// <summary>Environment variable naming the isolated JSON file that replaces application file configuration.</summary>
        internal const string ConfigurationPathVariable = "VECTORNNTP_STORAGESERVICE_STARTUP_SMOKE_CONFIG";

        /// <summary>Stdout prefix written after configuration binding succeeds.</summary>
        private const string CheckpointPrefix = "StorageService startup smoke: configuration";

        /// <summary>Returns whether <paramref name="value"/> requests startup smoke.</summary>
        /// <param name="value">Raw environment value. Null and every value other than <see cref="EnabledValue"/> are ignored.</param>
        /// <returns><see langword="true"/> only when <paramref name="value"/> is exactly <see cref="EnabledValue"/>.</returns>
        internal static bool IsRequested(string? value) =>
            string.Equals(value, EnabledValue, StringComparison.Ordinal);

        /// <summary>
        /// Removes file configuration sources and appends the JSON file named by <see cref="ConfigurationPathVariable"/>.
        /// </summary>
        /// <param name="configuration">Host configuration builder after the application JSON sources have been registered.</param>
        /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
        /// <exception cref="InvalidOperationException"><see cref="ConfigurationPathVariable"/> is missing.</exception>
        /// <exception cref="FileNotFoundException">The named file does not exist.</exception>
        /// <remarks>
        /// Environment and command-line sources stay in place, but the smoke file is added last so its keys win.
        /// Production JSON cannot leave a leftover bind-address entry in the merged configuration.
        /// </remarks>
        internal static void UseIsolatedConfiguration(IConfigurationBuilder configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var path = Environment.GetEnvironmentVariable(ConfigurationPathVariable);
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException(
                    ConfigurationPathVariable + " must name the isolated startup smoke configuration file.");
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Startup smoke configuration file was not found.", path);
            }

            for (var index = configuration.Sources.Count - 1; index >= 0; index--)
            {
                if (configuration.Sources[index] is FileConfigurationSource)
                {
                    configuration.Sources.RemoveAt(index);
                }
            }

            configuration.AddJsonFile(Path.GetFullPath(path), optional: false, reloadOnChange: false);
        }

        /// <summary>
        /// Resolves bound storage options and writes the checkpoint.
        /// </summary>
        /// <param name="host">Built host. Disposed before this method returns, including when resolution fails.</param>
        /// <param name="output">Checkpoint destination. Production passes <see cref="Console.Out"/>.</param>
        /// <returns>0 after the checkpoint is written.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="host"/> or <paramref name="output"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Bound options lack a usable identity.</exception>
        internal static int Complete(IHost host, TextWriter output)
        {
            ArgumentNullException.ThrowIfNull(host);
            ArgumentNullException.ThrowIfNull(output);
            using (host)
            {
                var options = host.Services.GetRequiredService<IOptions<StorageServerOptions>>().Value;
                if (options.ServerId is not { } serverId || string.IsNullOrWhiteSpace(options.Fqdn))
                {
                    throw new InvalidOperationException(
                        "Startup smoke did not bind a usable StorageServer ServerId and Fqdn.");
                }

                output.WriteLine(
                    "{0} serverId={1} fqdn={2}",
                    CheckpointPrefix,
                    serverId,
                    options.Fqdn);
                output.Flush();
                return 0;
            }
        }
    }
}
