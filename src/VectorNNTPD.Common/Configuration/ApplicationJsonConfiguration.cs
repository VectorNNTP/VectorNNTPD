using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace VectorNNTPD.Common.Configuration
{
    /// <summary>
    /// Names the primary JSON configuration file from the entry assembly short name.
    /// </summary>
    /// <remarks>
    /// The file is <c>{assembly name}.json</c>. The environment overlay is
    /// <c>{assembly name}.{environment}.json</c>. Both use
    /// <see cref="Assembly.GetEntryAssembly"/>, not the process executable path.
    /// Generic Host defaults (<c>appsettings.json</c> and <c>{application}.settings.json</c>)
    /// are removed and are not a fallback. The RabbitMQ file name is supplied by the caller.
    /// There is no default RabbitMQ filename and no fallback to <c>RabbitMq.json</c>.
    /// </remarks>
    public static class ApplicationJsonConfiguration
    {
        /// <summary>Gets the entry assembly's short name.</summary>
        /// <returns>The <see cref="AssemblyName.Name"/> of the entry assembly.</returns>
        /// <exception cref="InvalidOperationException">The entry assembly or its name is missing.</exception>
        internal static string EntryAssemblyName
        {
            get
            {
                var name = Assembly.GetEntryAssembly()?.GetName()?.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidOperationException(
                        "The entry assembly name is required to locate application configuration.");
                }

                return name;
            }
        }

        /// <summary>Gets <c>{entry assembly name}.json</c>.</summary>
        /// <returns>The primary application JSON configuration filename.</returns>
        public static string PrimaryJsonFileName() => EntryAssemblyName + ".json";

        /// <summary>Gets <c>{entry assembly name}.{environment}.json</c>.</summary>
        /// <param name="environmentName">Host environment name, for example <c>Development</c>.</param>
        /// <returns>The environment-specific JSON configuration filename.</returns>
        /// <exception cref="ArgumentException"><paramref name="environmentName"/> is empty.</exception>
        internal static string EnvironmentJsonFileName(string environmentName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
            return EntryAssemblyName + "." + environmentName + ".json";
        }

        /// <summary>
        /// Removes Generic Host JSON defaults and registers the entry-assembly JSON files.
        /// </summary>
        /// <param name="configuration">Host configuration builder, before it is read.</param>
        /// <param name="environmentName">Current host environment name.</param>
        /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="environmentName"/> is empty.</exception>
        /// <remarks>
        /// <c>appsettings.json</c>, <c>appsettings.{environment}.json</c>, and
        /// <c>{application}.settings.json</c> are removed when the host registered them.
        /// <see cref="PrimaryJsonFileName"/> and <see cref="EnvironmentJsonFileName"/> are then
        /// inserted at that position when they are not already present. Optional and
        /// reload-on-change are copied from the removed host source. When the host did not
        /// register one, the added source is optional and reloads on change, and it is inserted
        /// at the start of the source list. A second call does not add another copy.
        /// The host default names are not a fallback. This method does not change the builder base path.
        /// </remarks>
        internal static void UseEntryAssemblyJsonFiles(IConfigurationBuilder configuration, string environmentName)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

            var primary = PrimaryJsonFileName();
            var environmentFile = EnvironmentJsonFileName(environmentName);
            var legacyEnvironment = "appsettings." + environmentName + ".json";
            var insertAt = -1;
            FileConfigurationSource? primaryTemplate = null;
            FileConfigurationSource? environmentTemplate = null;

            for (var index = configuration.Sources.Count - 1; index >= 0; index--)
            {
                if (configuration.Sources[index] is not FileConfigurationSource file || string.IsNullOrEmpty(file.Path))
                {
                    continue;
                }

                if (IsHostNamedSettingsFile(file.Path))
                {
                    configuration.Sources.RemoveAt(index);
                    if (insertAt > index)
                    {
                        insertAt--;
                    }

                    continue;
                }

                var isLegacyPrimary = file.Path.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase);
                var isLegacyEnvironment = file.Path.Equals(legacyEnvironment, StringComparison.OrdinalIgnoreCase);
                if (!isLegacyPrimary && !isLegacyEnvironment)
                {
                    continue;
                }

                if (isLegacyPrimary)
                {
                    primaryTemplate = file;
                }
                else
                {
                    environmentTemplate = file;
                }

                if (insertAt < 0 || index < insertAt)
                {
                    insertAt = index;
                }

                configuration.Sources.RemoveAt(index);
                if (insertAt > index)
                {
                    insertAt--;
                }
            }

            if (insertAt < 0)
            {
                insertAt = 0;
            }

            if (!ContainsFile(configuration, primary))
            {
                configuration.Sources.Insert(insertAt, CreateJsonSource(primary, primaryTemplate ?? environmentTemplate));
                insertAt++;
            }

            if (!ContainsFile(configuration, environmentFile))
            {
                configuration.Sources.Insert(insertAt, CreateJsonSource(environmentFile, environmentTemplate ?? primaryTemplate));
            }
        }

        /// <summary>
        /// Inserts the shared RabbitMQ JSON file after the entry-assembly JSON sources
        /// and before later sources such as environment variables and command-line arguments.
        /// </summary>
        /// <param name="configuration">Host configuration builder after <see cref="UseEntryAssemblyJsonFiles"/>.</param>
        /// <param name="environmentName">Current host environment name.</param>
        /// <param name="sharedFileName">
        /// RabbitMQ JSON filename. BackFiller passes <c>VectorNNTPD.RabbitMQ.json</c>.
        /// The method registers this path only. It does not substitute <c>RabbitMq.json</c> or any other name.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="environmentName"/> or <paramref name="sharedFileName"/> is empty.
        /// </exception>
        /// <exception cref="InvalidOperationException">The entry-assembly JSON sources were not found.</exception>
        /// <remarks>
        /// Optional and reload-on-change match the last entry-assembly JSON source found.
        /// There is no environment-specific RabbitMQ file. The section name inside the file remains <c>RabbitMQ</c>.
        /// A second call with the same filename does nothing. A missing file follows that source's optional flag
        /// and does not cause another filename to be loaded.
        /// </remarks>
        internal static void AddSharedRabbitMqJsonFile(
            IConfigurationBuilder configuration,
            string environmentName,
            string sharedFileName)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedFileName);
            var rabbitFile = sharedFileName.Trim();

            var primary = PrimaryJsonFileName();
            var environmentFile = EnvironmentJsonFileName(environmentName);
            FileConfigurationSource? template = null;
            var insertAt = -1;
            for (var index = 0; index < configuration.Sources.Count; index++)
            {
                if (configuration.Sources[index] is not FileConfigurationSource file || string.IsNullOrEmpty(file.Path))
                {
                    continue;
                }

                if (file.Path.Equals(rabbitFile, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (file.Path.Equals(primary, StringComparison.OrdinalIgnoreCase)
                    || file.Path.Equals(environmentFile, StringComparison.OrdinalIgnoreCase))
                {
                    template = file;
                    insertAt = index + 1;
                }
            }

            if (template is null)
            {
                throw new InvalidOperationException(
                    "Entry-assembly JSON configuration sources were not found.");
            }

            var source = new JsonConfigurationSource
            {
                Path = rabbitFile,
                Optional = template.Optional,
                ReloadOnChange = template.ReloadOnChange,
                FileProvider = template.FileProvider,
                OnLoadException = template.OnLoadException,
            };
            source.ResolveFileProvider();
            configuration.Sources.Insert(insertAt, source);
        }

        /// <summary>
        /// Returns whether <paramref name="path"/> is a host settings file
        /// (<c>*.settings.json</c>, or another <c>.json</c> path containing <c>.settings.</c>).
        /// </summary>
        /// <param name="path">Configuration source path. Comparison is ordinal and case-insensitive.</param>
        /// <returns><see langword="true"/> when the path matches either host-settings pattern.</returns>
        private static bool IsHostNamedSettingsFile(string path) =>
            path.EndsWith(".settings.json", StringComparison.OrdinalIgnoreCase)
            || (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && path.Contains(".settings.", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Returns whether a file source with <paramref name="path"/> is already registered.
        /// </summary>
        /// <param name="configuration">Builder whose sources are scanned. Non-file sources are ignored.</param>
        /// <param name="path">Source path compared ordinal-ignore-case.</param>
        /// <returns><see langword="true"/> when a matching <see cref="FileConfigurationSource"/> is present.</returns>
        private static bool ContainsFile(IConfigurationBuilder configuration, string path)
        {
            foreach (var source in configuration.Sources)
            {
                if (source is FileConfigurationSource file
                    && string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Builds a JSON file source for <paramref name="path"/>, copying optional, reload, provider, and load-exception settings from <paramref name="template"/>.
        /// </summary>
        /// <param name="path">JSON file name or path inserted into the builder.</param>
        /// <param name="template">
        /// Existing file source to copy. When null, the source is optional and reloads on change, with no file provider until resolve.
        /// </param>
        /// <returns>A source whose file provider has been resolved.</returns>
        private static JsonConfigurationSource CreateJsonSource(string path, FileConfigurationSource? template)
        {
            var source = new JsonConfigurationSource
            {
                Path = path,
                Optional = template?.Optional ?? true,
                ReloadOnChange = template?.ReloadOnChange ?? true,
                FileProvider = template?.FileProvider,
                OnLoadException = template?.OnLoadException,
            };
            source.ResolveFileProvider();
            return source;
        }
    }
}
