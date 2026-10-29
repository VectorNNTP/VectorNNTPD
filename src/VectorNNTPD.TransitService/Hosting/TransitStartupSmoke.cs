using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.Configuration;

namespace VectorNNTPD.TransitService.Hosting
{
    /// <summary>
    /// Builds the real Transit host and stops after configuration binding, without starting hosted services.
    /// </summary>
    /// <remarks>
    /// Set <see cref="EnvironmentVariable"/> to <see cref="EnabledValue"/> and
    /// <see cref="ConfigurationPathVariable"/> to an isolated JSON file. Does not call
    /// <c>IHost.RunAsync</c>, so Redis, MySQL, RabbitMQ, DNS, ACME, and listeners are not started.
    /// </remarks>
    internal static class TransitStartupSmoke
    {
        /// <summary>Environment variable that selects startup smoke. The value must be <see cref="EnabledValue"/>.</summary>
        internal const string EnvironmentVariable = "VECTORNNTP_TRANSIT_STARTUP_SMOKE";

        /// <summary>The only value of <see cref="EnvironmentVariable"/> that selects startup smoke.</summary>
        internal const string EnabledValue = "1";

        /// <summary>Environment variable naming the isolated JSON file that replaces application file configuration.</summary>
        internal const string ConfigurationPathVariable = "VECTORNNTP_TRANSIT_STARTUP_SMOKE_CONFIG";

        /// <summary>Stdout prefix written after configuration binding succeeds.</summary>
        private const string CheckpointPrefix = "TransitService startup smoke: configuration";

        /// <summary>Returns whether <paramref name="value"/> requests startup smoke.</summary>
        internal static bool IsRequested(string? value) =>
            string.Equals(value, EnabledValue, StringComparison.Ordinal);

        /// <summary>
        /// Removes file configuration sources and appends the JSON file named by <see cref="ConfigurationPathVariable"/>.
        /// </summary>
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

        /// <summary>Resolves bound Nntpd options and writes the checkpoint.</summary>
        internal static int Complete(IHost host, TextWriter output)
        {
            ArgumentNullException.ThrowIfNull(host);
            ArgumentNullException.ThrowIfNull(output);
            using (host)
            {
                var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
                if (options.ServerId is not { } serverId || string.IsNullOrWhiteSpace(options.Fqdn))
                {
                    throw new InvalidOperationException(
                        "Startup smoke did not bind a usable Nntpd ServerId and Fqdn.");
                }

                if (!string.Equals(options.HostApplicationPrefix, NntpdOptions.TransitApplicationPrefix, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Startup smoke expected HostApplicationPrefix '" + NntpdOptions.TransitApplicationPrefix
                        + "' and found '" + options.HostApplicationPrefix + "'.");
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
