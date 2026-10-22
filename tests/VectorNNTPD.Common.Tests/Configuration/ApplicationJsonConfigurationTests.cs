using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Tests.Configuration
{
    /// <summary>
    /// Locks entry-assembly JSON file names and the host configuration sources that replace <c>appsettings</c>.
    /// </summary>
    public sealed class ApplicationJsonConfigurationTests
    {
        /// <summary>
        /// Asserts that the primary and Development JSON names are the entry assembly's simple name plus <c>.json</c> or <c>.Development.json</c>, and that the primary name does not contain <c>.exe</c>.
        /// </summary>
        [Fact]
        public void File_names_are_derived_from_the_entry_assembly_short_name()
        {
            var name = Assembly.GetEntryAssembly()?.GetName()?.Name;
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.Equal(name + ".json", ApplicationJsonConfiguration.PrimaryJsonFileName());
            Assert.Equal(name + ".Development.json", ApplicationJsonConfiguration.EnvironmentJsonFileName("Development"));
            Assert.DoesNotContain(".exe", ApplicationJsonConfiguration.PrimaryJsonFileName(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Asserts that a Development host loads the entry-assembly primary and environment JSON, keeps the primary probe and Development shared values, and does not retain <c>appsettings</c> or <c>.settings.</c> file sources.
        /// </summary>
        [Fact]
        public void Host_loads_entry_assembly_json_and_does_not_fall_back_to_appsettings()
        {
            var root = Directory.CreateTempSubdirectory("vnntp-host-json-");
            try
            {
                var name = Assembly.GetEntryAssembly()!.GetName().Name!;
                Write(root.FullName, "appsettings.json", "legacy");
                Write(root.FullName, name + ".json", "primary", "base");
                Write(root.FullName, name + ".Development.json", shared: "development");
                Write(root.FullName, name + ".settings.json", "settings");
                Write(root.FullName, name + ".settings.Development.json", shared: "settings-development");

                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
                {
                    ContentRootPath = root.FullName,
                    EnvironmentName = Environments.Development,
                });
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(
                    builder.Configuration,
                    builder.Environment.EnvironmentName);

                Assert.Equal("primary", builder.Configuration["VectorNntpConfigProbe"]);
                Assert.Equal("development", builder.Configuration["VectorNntpConfigShared"]);
                Assert.DoesNotContain(
                    builder.Configuration.Sources.OfType<FileConfigurationSource>(),
                    source => source.Path is "appsettings.json"
                        or "appsettings.Development.json"
                        || (source.Path?.Contains(".settings.", StringComparison.OrdinalIgnoreCase) ?? false));
            }
            finally
            {
                TryDelete(root.FullName);
            }
        }

        /// <summary>
        /// Asserts that missing entry-assembly JSON is optional and that a preloaded <c>appsettings.json</c> probe is absent after <see cref="ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles"/>.
        /// </summary>
        [Fact]
        public void Missing_entry_assembly_json_stays_optional_and_ignores_a_leftover_appsettings_file()
        {
            var root = Directory.CreateTempSubdirectory("vnntp-host-json-missing-");
            try
            {
                Write(root.FullName, "appsettings.json", "legacy");
                var builder = new ConfigurationBuilder()
                    .SetBasePath(root.FullName)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false);
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Development");

                var configuration = builder.Build();
                Assert.Null(configuration["VectorNntpConfigProbe"]);
            }
            finally
            {
                TryDelete(root.FullName);
            }
        }

        /// <summary>
        /// Asserts that a builder with no JSON sources receives optional reloadable entry-assembly files before environment variables, and that a second call does not add duplicates.
        /// </summary>
        [Fact]
        public void Missing_host_json_sources_are_inserted_once()
        {
            var builder = new ConfigurationBuilder();
            builder.AddEnvironmentVariables();

            ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Production");
            ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Production");
            ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(builder, "Production", "VectorNNTPD.RabbitMQ.json");
            ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(builder, "Production", "VectorNNTPD.RabbitMQ.json");

            var files = builder.Sources.OfType<FileConfigurationSource>().ToArray();
            Assert.Equal(
                new[]
                {
                    ApplicationJsonConfiguration.PrimaryJsonFileName(),
                    ApplicationJsonConfiguration.EnvironmentJsonFileName("Production"),
                    "VectorNNTPD.RabbitMQ.json",
                },
                files.Select(static source => source.Path).ToArray());
            Assert.All(files, static source => Assert.True(source.Optional));
            Assert.All(files, static source => Assert.True(source.ReloadOnChange));
            Assert.Equal("EnvironmentVariablesConfigurationSource", builder.Sources[^1].GetType().Name);
        }

        /// <summary>
        /// Asserts that the environment JSON value overrides the primary file for the same key, and that optional and reload flags are copied from the replaced host sources.
        /// </summary>
        [Fact]
        public void Environment_json_overrides_primary_and_reload_flags_are_copied()
        {
            var root = Directory.CreateTempSubdirectory("vnntp-json-order-");
            try
            {
                var name = Assembly.GetEntryAssembly()!.GetName().Name!;
                Write(root.FullName, name + ".json", "primary");
                Write(root.FullName, name + ".Production.json", "environment");
                var builder = new ConfigurationBuilder()
                    .SetBasePath(root.FullName)
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                    .AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: true);
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Production");

                var files = builder.Sources.OfType<FileConfigurationSource>().ToArray();
                Assert.False(files[0].Optional);
                Assert.False(files[0].ReloadOnChange);
                Assert.True(files[1].Optional);
                Assert.True(files[1].ReloadOnChange);
                Assert.Equal("environment", builder.Build()["VectorNntpConfigProbe"]);
            }
            finally
            {
                TryDelete(root.FullName);
            }
        }

        /// <summary>
        /// Asserts that only the supplied RabbitMQ filename is registered, that a present <c>RabbitMq.json</c> is not read, and that a blank filename is rejected.
        /// </summary>
        [Fact]
        public void Supplied_rabbitmq_filename_is_exclusive_and_blank_names_are_rejected()
        {
            var root = Directory.CreateTempSubdirectory("vnntp-rabbit-no-fallback-");
            try
            {
                Write(root.FullName, "RabbitMq.json", "legacy");
                File.WriteAllText(
                    Path.Join(root.FullName, "VectorNNTPD.RabbitMQ.json"),
                    """{"RabbitMQ":{"Port":5672,"Hosts":["198.18.0.4"]}}""");
                var builder = new ConfigurationBuilder()
                    .SetBasePath(root.FullName)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                    .AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: true);
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Production");
                Assert.Throws<ArgumentException>(() =>
                    ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(builder, "Production", " "));
                ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(
                    builder,
                    "Production",
                    "VectorNNTPD.RabbitMQ.json");

                var paths = builder.Sources.OfType<FileConfigurationSource>().Select(static source => source.Path).ToArray();
                Assert.Equal(
                    new[]
                    {
                        ApplicationJsonConfiguration.PrimaryJsonFileName(),
                        ApplicationJsonConfiguration.EnvironmentJsonFileName("Production"),
                        "VectorNNTPD.RabbitMQ.json",
                    },
                    paths);
                Assert.DoesNotContain("RabbitMq.json", paths);
                var configuration = builder.Build();
                Assert.Equal("198.18.0.4", configuration["RabbitMQ:Hosts:0"]);
                Assert.Null(configuration["VectorNntpConfigProbe"]);
            }
            finally
            {
                TryDelete(root.FullName);
            }
        }

        /// <summary>
        /// Asserts that a required primary file that is missing fails with <see cref="FileNotFoundException"/>, and that malformed JSON fails with <see cref="InvalidDataException"/>.
        /// </summary>
        [Fact]
        public void Missing_required_primary_and_invalid_json_fail_the_build()
        {
            var missing = Directory.CreateTempSubdirectory("vnntp-json-missing-required-");
            var invalid = Directory.CreateTempSubdirectory("vnntp-json-invalid-");
            try
            {
                var required = new ConfigurationBuilder()
                    .SetBasePath(missing.FullName)
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                    .AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false);
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(required, "Production");
                Assert.Throws<FileNotFoundException>(() => required.Build());

                File.WriteAllText(
                    Path.Join(invalid.FullName, ApplicationJsonConfiguration.PrimaryJsonFileName()),
                    "{");
                var malformed = new ConfigurationBuilder()
                    .SetBasePath(invalid.FullName)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                    .AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false);
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(malformed, "Production");
                var exception = Assert.Throws<InvalidDataException>(() => malformed.Build());
                Assert.IsType<FormatException>(exception.InnerException);
            }
            finally
            {
                TryDelete(missing.FullName);
                TryDelete(invalid.FullName);
            }
        }

        /// <summary>
        /// Writes one JSON object containing the optional probe and shared marker properties used by these facts.
        /// </summary>
        /// <param name="directory">Directory that receives the file.</param>
        /// <param name="fileName">File name, including extension, relative to <paramref name="directory"/>.</param>
        /// <param name="marker">When set, the <c>VectorNntpConfigProbe</c> value to write.</param>
        /// <param name="shared">When set, the <c>VectorNntpConfigShared</c> value to write.</param>
        private static void Write(string directory, string fileName, string? marker = null, string? shared = null)
        {
            var markerLine = marker is null ? string.Empty : $""" "VectorNntpConfigProbe": "{marker}" """;
            var sharedLine = shared is null ? string.Empty : $""" "VectorNntpConfigShared": "{shared}" """;
            var body = marker is null
                ? $$"""{ {{sharedLine}} }"""
                : shared is null
                    ? $$"""{ {{markerLine}} }"""
                    : $$"""{ {{markerLine}}, {{sharedLine}} }""";
            File.WriteAllText(Path.Join(directory, fileName), body);
        }

        /// <summary>
        /// Deletes <paramref name="path"/> recursively. <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/> are ignored.
        /// </summary>
        /// <param name="path">Temporary directory to delete.</param>
        private static void TryDelete(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
