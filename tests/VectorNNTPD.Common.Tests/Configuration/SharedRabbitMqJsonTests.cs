using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Tests.Configuration
{
    /// <summary>
    /// Locks the shipped <c>src/VectorNNTPD.Common/VectorNNTPD.RabbitMQ.json</c> sample, Common's load order for that file, and the absence of a top-level RabbitMQ section in host primary JSON that exists in this repository.
    /// </summary>
    /// <remarks>
    /// NNTPD and StorageServer are not projects here. BackFiller ships its own <c>VectorNNTPD.RabbitMQ.json</c>. The loader does not read <c>RabbitMq.json</c>. This test reads repository source files, not application <c>bin</c> directories.
    /// </remarks>
    public sealed class SharedRabbitMqJsonTests
    {
        /// <summary>
        /// Asserts that <c>src/VectorNNTPD.Common/VectorNNTPD.RabbitMQ.json</c> binds the shared contract through <see cref="ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile"/>, and that every executable host project in this repository has a primary JSON file with no top-level <see cref="RabbitMqOptions.SectionName"/> property.
        /// </summary>
        [Fact]
        public void Shipped_sample_binds_and_present_host_primary_json_omits_the_section()
        {
            var repoRoot = FindRepoRoot();
            var sharedPath = Path.Combine(repoRoot, "src", "VectorNNTPD.Common", "VectorNNTPD.RabbitMQ.json");
            Assert.True(File.Exists(sharedPath), sharedPath);

            var primaryFiles = FindHostPrimaryJsonFiles(repoRoot);
            Assert.NotEmpty(primaryFiles);
            foreach (var primaryPath in primaryFiles)
            {
                Assert.True(File.Exists(primaryPath), primaryPath);
                using var primary = JsonDocument.Parse(File.ReadAllText(primaryPath));
                Assert.False(
                    primary.RootElement.TryGetProperty(RabbitMqOptions.SectionName, out _),
                    primaryPath);
            }

            var root = Directory.CreateTempSubdirectory("vnntp-shared-rabbit-");
            try
            {
                File.Copy(sharedPath, Path.Combine(root.FullName, "VectorNNTPD.RabbitMQ.json"));
                File.WriteAllText(
                    Path.Combine(root.FullName, ApplicationJsonConfiguration.PrimaryJsonFileName()),
                    "{}");

                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
                {
                    Args = [],
                    ContentRootPath = root.FullName,
                    EnvironmentName = Environments.Production,
                });
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(
                    builder.Configuration,
                    builder.Environment.EnvironmentName);
                ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(
                    builder.Configuration,
                    builder.Environment.EnvironmentName,
                    "VectorNNTPD.RabbitMQ.json");

                var rabbitIndex = IndexOf(builder.Configuration, "VectorNNTPD.RabbitMQ.json");
                var rabbit = (FileConfigurationSource)builder.Configuration.Sources[rabbitIndex];
                Assert.True(rabbit.Optional);
                Assert.True(rabbit.ReloadOnChange);
                var environmentIndex = IndexOf(
                    builder.Configuration,
                    ApplicationJsonConfiguration.EnvironmentJsonFileName("Production"));
                Assert.True(rabbitIndex > environmentIndex);
                Assert.True(rabbitIndex < IndexOfEnvironmentVariablesAfter(builder.Configuration, rabbitIndex));

                for (var index = builder.Configuration.Sources.Count - 1; index >= 0; index--)
                {
                    var name = builder.Configuration.Sources[index].GetType().Name;
                    if (name is "EnvironmentVariablesConfigurationSource" or "CommandLineConfigurationSource")
                    {
                        builder.Configuration.Sources.RemoveAt(index);
                    }
                }

                var options = new RabbitMqOptions();
                builder.Configuration.GetSection(RabbitMqOptions.SectionName).Bind(options);
                AssertSharedValues(options);

                var validation = new RabbitMqOptionsValidator().Validate(null, options);
                Assert.True(validation.Succeeded, string.Join("; ", validation.Failures ?? []));
            }
            finally
            {
                try
                {
                    root.Delete(recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        /// <summary>
        /// Asserts that <c>RabbitMQ__Port</c> and a <c>--RabbitMQ:VirtualHost</c> argument override <c>VectorNNTPD.RabbitMQ.json</c> while a host from that file remains, then restores the previous environment variable.
        /// </summary>
        [Fact]
        public void Environment_variable_and_command_line_override_RabbitMq_json()
        {
            var root = Directory.CreateTempSubdirectory("vnntp-rabbit-json-");
            const string overriddenPort = "5999";
            var previous = Environment.GetEnvironmentVariable("RabbitMQ__Port");
            try
            {
                File.WriteAllText(
                    Path.Combine(root.FullName, "VectorNNTPD.RabbitMQ.json"),
                    """{"RabbitMQ":{"Port":5672,"Hosts":["198.18.0.3"]}}""");
                File.WriteAllText(
                    Path.Combine(root.FullName, ApplicationJsonConfiguration.PrimaryJsonFileName()),
                    """{"RabbitMQ":{"Port":1}}""");

                Environment.SetEnvironmentVariable("RabbitMQ__Port", overriddenPort);
                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
                {
                    Args = ["--RabbitMQ:VirtualHost=/from-args"],
                    ContentRootPath = root.FullName,
                    EnvironmentName = Environments.Production,
                });
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(
                    builder.Configuration,
                    builder.Environment.EnvironmentName);
                ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(
                    builder.Configuration,
                    builder.Environment.EnvironmentName,
                    "VectorNNTPD.RabbitMQ.json");

                var rabbitIndex = IndexOf(builder.Configuration, "VectorNNTPD.RabbitMQ.json");
                Assert.True(rabbitIndex < IndexOfCommandLineAfter(builder.Configuration, rabbitIndex));
                Assert.Equal(overriddenPort, builder.Configuration["RabbitMQ:Port"]);
                Assert.Equal("/from-args", builder.Configuration["RabbitMQ:VirtualHost"]);
                Assert.Equal("198.18.0.3", builder.Configuration["RabbitMQ:Hosts:0"]);
            }
            finally
            {
                Environment.SetEnvironmentVariable("RabbitMQ__Port", previous);
                try
                {
                    root.Delete(recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        /// <summary>
        /// Asserts the shared broker host, port, pool size, payload limit, management URL and timeout, virtual host, and disabled SSL expected from the shipped <c>VectorNNTPD.RabbitMQ.json</c>.
        /// </summary>
        /// <param name="options">Options bound from the shared RabbitMQ section.</param>
        private static void AssertSharedValues(RabbitMqOptions options)
        {
            Assert.NotNull(options.Hosts);
            Assert.Equal(["198.18.0.3"], options.Hosts);
            Assert.Equal(5672, options.Port);
            Assert.Equal(512, options.ChannelPoolSize);
            Assert.Equal(1024, options.WorkRequestMaxPayloadBytes);
            Assert.Equal(10, options.ArticleWorkDeliveryLimit);
            Assert.Equal("http://198.18.0.3:15672", options.Management?.BaseUrl);
            Assert.Equal(5, options.Management?.RequestTimeoutSeconds);
            Assert.Equal("/", options.VirtualHost);
            Assert.False(options.EnableSsl);
        }

        /// <summary>
        /// Returns the index of the file configuration source whose path equals <paramref name="path"/>, ignoring case.
        /// </summary>
        /// <param name="configuration">Builder whose sources are searched.</param>
        /// <param name="path">File path to find.</param>
        /// <returns>The zero-based source index.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no file source has that path.</exception>
        private static int IndexOf(IConfigurationBuilder configuration, string path)
        {
            for (var index = 0; index < configuration.Sources.Count; index++)
            {
                if (configuration.Sources[index] is FileConfigurationSource file
                    && string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            throw new InvalidOperationException("Configuration source was not found: " + path);
        }

        /// <summary>
        /// Returns the index of the first command-line configuration source after <paramref name="start"/>.
        /// </summary>
        /// <param name="configuration">Builder whose sources are searched.</param>
        /// <param name="start">Index after which the search begins.</param>
        /// <returns>The zero-based source index.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no later command-line source exists. The message lists the source type names.</exception>
        private static int IndexOfCommandLineAfter(IConfigurationBuilder configuration, int start)
        {
            for (var index = start + 1; index < configuration.Sources.Count; index++)
            {
                if (configuration.Sources[index].GetType().Name == "CommandLineConfigurationSource")
                {
                    return index;
                }
            }

            throw new InvalidOperationException(
                "Command-line configuration source was not found. Sources: "
                + string.Join(", ", configuration.Sources.Select(static source => source.GetType().Name)));
        }

        /// <summary>
        /// Returns the index of the first environment-variable configuration source after <paramref name="start"/>.
        /// </summary>
        /// <param name="configuration">Builder whose sources are searched.</param>
        /// <param name="start">Index after which the search begins.</param>
        /// <returns>The zero-based source index.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no later environment-variable source exists.</exception>
        private static int IndexOfEnvironmentVariablesAfter(IConfigurationBuilder configuration, int start)
        {
            for (var index = start + 1; index < configuration.Sources.Count; index++)
            {
                if (configuration.Sources[index].GetType().Name == "EnvironmentVariablesConfigurationSource")
                {
                    return index;
                }
            }

            throw new InvalidOperationException("Environment variable configuration source was not found.");
        }

        /// <summary>
        /// Walks parents of <see cref="AppContext.BaseDirectory"/> until <c>VectorNNTPD.slnx</c> is found.
        /// </summary>
        /// <returns>The repository root.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no parent contains the solution file.</exception>
        private static string FindRepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "VectorNNTPD.slnx")))
                {
                    return dir.FullName;
                }
            }

            throw new InvalidOperationException("Could not locate VectorNNTPD.slnx.");
        }

        /// <summary>
        /// Returns the primary JSON path for every executable project under <c>src</c>.
        /// </summary>
        /// <param name="repoRoot">Repository root that contains <c>src</c>.</param>
        /// <returns>Paths of <c>{assembly name}.json</c> beside each executable project. The file may be absent; the caller asserts that it exists.</returns>
        private static List<string> FindHostPrimaryJsonFiles(string repoRoot)
        {
            var files = new List<string>();
            var src = Path.Combine(repoRoot, "src");
            foreach (var project in Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories))
            {
                if (IsBuildOutputPath(project))
                {
                    continue;
                }

                var text = File.ReadAllText(project);
                if (!IsExecutableHostProject(text))
                {
                    continue;
                }

                var assemblyName = ReadAssemblyName(text) ?? Path.GetFileNameWithoutExtension(project);
                files.Add(Path.Combine(Path.GetDirectoryName(project)!, assemblyName + ".json"));
            }

            return files;
        }

        /// <summary>
        /// Reports whether <paramref name="path"/> is under a <c>bin</c> or <c>obj</c> directory.
        /// </summary>
        /// <param name="path">Project path returned by a recursive search.</param>
        /// <returns><see langword="true"/> when a directory segment is <c>bin</c> or <c>obj</c>.</returns>
        private static bool IsBuildOutputPath(string path)
        {
            var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return segments.Any(static segment =>
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Reports whether <paramref name="projectText"/> is an executable host project.
        /// </summary>
        /// <param name="projectText">Project file text.</param>
        /// <returns><see langword="true"/> for <c>Microsoft.NET.Sdk.Worker</c> or an explicit <c>Exe</c> output type.</returns>
        private static bool IsExecutableHostProject(string projectText) =>
            projectText.Contains("Sdk=\"Microsoft.NET.Sdk.Worker\"", StringComparison.Ordinal)
            || projectText.Contains("<OutputType>Exe</OutputType>", StringComparison.Ordinal);

        /// <summary>
        /// Reads the first <c>AssemblyName</c> element from <paramref name="projectText"/>.
        /// </summary>
        /// <param name="projectText">Project file text.</param>
        /// <returns>The assembly name, or <see langword="null"/> when the element is absent.</returns>
        private static string? ReadAssemblyName(string projectText)
        {
            const string start = "<AssemblyName>";
            const string end = "</AssemblyName>";
            var startIndex = projectText.IndexOf(start, StringComparison.Ordinal);
            if (startIndex < 0)
            {
                return null;
            }

            startIndex += start.Length;
            var endIndex = projectText.IndexOf(end, startIndex, StringComparison.Ordinal);
            if (endIndex < 0)
            {
                return null;
            }

            var name = projectText[startIndex..endIndex].Trim();
            return name.Length == 0 ? null : name;
        }
    }
}
