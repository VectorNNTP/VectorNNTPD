using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Hosting;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.Configuration
{
    /// <summary>
    /// Verifies <see cref="ApplicationJsonConfiguration"/> replaces generic host JSON sources with entry-assembly file names.
    /// </summary>
    public sealed class ApplicationJsonConfigurationSourceTests
    {
        /// <summary>
        /// Committed JSON file names match production assembly naming; legacy <c>appsettings</c> files are absent from the project tree.
        /// </summary>
        [Fact]
        public void Production_assembly_names_the_configuration_files()
        {
            Assert.Equal("VectorNNTPD.Common", typeof(ApplicationJsonConfiguration).Assembly.GetName().Name);
            Assert.Null(typeof(BackFillerJsonFiles).Assembly.GetType(
                "VectorNNTPD.BackFillerService.Configuration.ApplicationJsonConfiguration"));
            Assert.Equal("VectorNNTPD.RabbitMQ.json", BackFillerJsonFiles.SharedRabbitMqFileName);

            var root = RepositoryFiles.Root();
            Assert.True(File.Exists(RelativePath.Under(root, "src", "VectorNNTPD.BackFillerService", "VectorNNTPD.BackFillerService.json")));
            Assert.True(File.Exists(RelativePath.Under(root, "src", "VectorNNTPD.BackFillerService", "VectorNNTPD.RabbitMQ.json")));
            Assert.False(File.Exists(RelativePath.Under(root, "src", "VectorNNTPD.BackFillerService", "VectorNNTP.RabbitMQ.json")));
            Assert.False(File.Exists(RelativePath.Under(root, "src", "VectorNNTPD.BackFillerService", "appsettings.json")));
            Assert.False(File.Exists(RelativePath.Under(root, "src", "VectorNNTPD.BackFillerService", "RabbitMq.json")));
        }

        /// <summary>
        /// After registration, only primary, environment, and shared RabbitMQ JSON sources remain before environment variables, with expected optional/reload flags.
        /// </summary>
        [Fact]
        public void Host_defaults_are_removed_and_entry_assembly_files_are_registered()
        {
            var builder = new ConfigurationBuilder();
            builder.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
            builder.AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: true);
            builder.AddJsonFile("VectorNNTPD.BackFillerService.settings.json", optional: true, reloadOnChange: true);
            builder.AddEnvironmentVariables();

            ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Production");
            ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(builder, "Production", BackFillerJsonFiles.SharedRabbitMqFileName);

            var primary = ApplicationJsonConfiguration.PrimaryJsonFileName();
            var environment = ApplicationJsonConfiguration.EnvironmentJsonFileName("Production");
            var files = FileSources(builder).ToArray();
            var actual = files.Select(static source => source.Path ?? string.Empty).ToArray();
            Assert.Equal(
                new[] { primary, environment, BackFillerJsonFiles.SharedRabbitMqFileName },
                actual);
            Assert.False(files[0].Optional);
            Assert.False(files[0].ReloadOnChange);
            Assert.True(files[1].Optional);
            Assert.True(files[1].ReloadOnChange);
            Assert.True(files[2].Optional);
            Assert.True(files[2].ReloadOnChange);
            Assert.IsType<EnvironmentVariablesConfigurationSource>(builder.Sources[^1]);
        }

        /// <summary>
        /// When no JSON sources exist yet, all three entry-assembly files are added as optional with reload enabled.
        /// </summary>
        [Fact]
        public void Entry_assembly_files_are_added_when_the_host_registered_no_json_sources()
        {
            var builder = new ConfigurationBuilder();
            builder.AddEnvironmentVariables();

            ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Production");
            ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(builder, "Production", BackFillerJsonFiles.SharedRabbitMqFileName);

            var files = FileSources(builder).ToArray();
            Assert.Equal(3, files.Length);
            Assert.All(files, static source => Assert.True(source.Optional));
            Assert.All(files, static source => Assert.True(source.ReloadOnChange));
            Assert.IsType<EnvironmentVariablesConfigurationSource>(builder.Sources[^1]);
        }

        /// <summary>
        /// Optional environment and RabbitMQ files may be absent without failing <see cref="IConfigurationBuilder.Build"/>. A present <c>RabbitMq.json</c> is not loaded in their place.
        /// </summary>
        [Fact]
        public void Missing_optional_files_do_not_fail_configuration_build()
        {
            var directory = Directory.CreateTempSubdirectory("bf-json-missing-");
            try
            {
                File.WriteAllText(Path.Join(directory.FullName, "RabbitMq.json"), """{"RabbitMQ":{"Hosts":["198.18.0.9"]}}""");
                var builder = new ConfigurationBuilder();
                builder.SetBasePath(directory.FullName);
                builder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
                builder.AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false);
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Production");
                ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(builder, "Production", BackFillerJsonFiles.SharedRabbitMqFileName);

                var paths = FileSources(builder).Select(static source => source.Path).ToArray();
                Assert.Contains(BackFillerJsonFiles.SharedRabbitMqFileName, paths);
                Assert.DoesNotContain("RabbitMq.json", paths);
                var configuration = builder.Build();
                Assert.Null(configuration["BackFiller:ServerId"]);
                Assert.Null(configuration["RabbitMQ:Hosts:0"]);
            }
            finally
            {
                directory.Delete(true);
            }
        }

        /// <summary>
        /// The primary BackFiller JSON file is required; build throws when it is missing from the base path.
        /// </summary>
        [Fact]
        public void Missing_required_primary_file_fails_configuration_build()
        {
            var directory = Directory.CreateTempSubdirectory("bf-json-required-");
            try
            {
                var builder = new ConfigurationBuilder();
                builder.SetBasePath(directory.FullName);
                builder.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
                builder.AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false);
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Production");

                Assert.Throws<FileNotFoundException>(() => builder.Build());
            }
            finally
            {
                directory.Delete(true);
            }
        }

        /// <summary>
        /// Malformed primary JSON surfaces as <see cref="InvalidDataException"/> with an inner format error.
        /// </summary>
        [Fact]
        public void Invalid_primary_json_fails_configuration_build()
        {
            var directory = Directory.CreateTempSubdirectory("bf-json-invalid-");
            try
            {
                File.WriteAllText(
                    Path.Join(directory.FullName, ApplicationJsonConfiguration.PrimaryJsonFileName()),
                    "{");
                var builder = new ConfigurationBuilder();
                builder.SetBasePath(directory.FullName);
                builder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
                builder.AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false);
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Production");

                var exception = Assert.Throws<InvalidDataException>(() => builder.Build());
                Assert.IsType<FormatException>(exception.InnerException);
            }
            finally
            {
                directory.Delete(true);
            }
        }

        /// <summary>
        /// Generic host configuration after registration excludes <c>appsettings*</c> and <c>*.settings.json</c> paths.
        /// </summary>
        [Fact]
        public void Generic_host_builder_does_not_keep_appsettings_sources()
        {
            var directory = Directory.CreateTempSubdirectory("bf-host-json-");
            try
            {
                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
                {
                    Args = [],
                    ContentRootPath = directory.FullName,
                });
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(
                    builder.Configuration,
                    builder.Environment.EnvironmentName);
                ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(
                    builder.Configuration,
                    builder.Environment.EnvironmentName,
                    BackFillerJsonFiles.SharedRabbitMqFileName);

                var paths = FileSources(builder.Configuration).Select(static source => source.Path).ToArray();
                Assert.DoesNotContain(
                    paths,
                    static path => string.Equals(path, "appsettings.json", StringComparison.OrdinalIgnoreCase)
                        || (path?.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase) ?? false)
                        || (path?.EndsWith(".settings.json", StringComparison.OrdinalIgnoreCase) ?? false));
                Assert.Contains(ApplicationJsonConfiguration.PrimaryJsonFileName(), paths);
                Assert.Contains(
                    ApplicationJsonConfiguration.EnvironmentJsonFileName(builder.Environment.EnvironmentName),
                    paths);
                Assert.Contains(BackFillerJsonFiles.SharedRabbitMqFileName, paths);
            }
            finally
            {
                directory.Delete(true);
            }
        }

        /// <summary>
        /// The shipped RabbitMQ file is copied into the service build output under <see cref="BackFillerJsonFiles.SharedRabbitMqFileName"/>, binds, and validates.
        /// </summary>
        [Fact]
        public void Shipped_rabbitmq_file_is_copied_and_loads()
        {
            const string obsoleteFileName = "VectorNNTP.RabbitMQ.json";
            var root = RepositoryFiles.Root();
            var projectDirectory = RelativePath.Under(root, "src", "VectorNNTPD.BackFillerService");
            var sourcePath = Path.Join(projectDirectory, BackFillerJsonFiles.SharedRabbitMqFileName);
            var project = File.ReadAllText(Path.Join(projectDirectory, "VectorNNTPD.BackFillerService.csproj"));
            Assert.Contains("<Content Update=\"VectorNNTPD.RabbitMQ.json\">", project, StringComparison.Ordinal);
            Assert.Contains("<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>", project, StringComparison.Ordinal);
            Assert.Contains("<CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>", project, StringComparison.Ordinal);
            Assert.DoesNotContain(obsoleteFileName, project, StringComparison.Ordinal);

            var outputDirectory = ServiceOutputDirectory(root);
            var outputPath = Path.Join(outputDirectory, BackFillerJsonFiles.SharedRabbitMqFileName);
            Assert.True(File.Exists(outputPath), outputPath);
            Assert.False(File.Exists(Path.Join(outputDirectory, obsoleteFileName)));
            Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(outputPath));

            var directory = Directory.CreateTempSubdirectory("bf-rabbit-json-");
            try
            {
                File.Copy(sourcePath, Path.Join(directory.FullName, BackFillerJsonFiles.SharedRabbitMqFileName));
                File.WriteAllText(
                    Path.Join(directory.FullName, ApplicationJsonConfiguration.PrimaryJsonFileName()),
                    "{}");
                var builder = new ConfigurationBuilder();
                builder.SetBasePath(directory.FullName);
                builder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
                builder.AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false);
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Production");
                ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(builder, "Production", BackFillerJsonFiles.SharedRabbitMqFileName);

                var configuration = builder.Build();
                Assert.Equal("5672", configuration["RabbitMQ:Port"]);
                Assert.Equal("198.18.0.3", configuration["RabbitMQ:Hosts:0"]);
                Assert.Equal(bool.FalseString, configuration["RabbitMQ:EnableSsl"]);
                Assert.Equal("/", configuration["RabbitMQ:VirtualHost"]);

                var rabbit = new RabbitMqOptions();
                configuration.GetSection(RabbitMqOptions.SectionName).Bind(rabbit);
                var validation = new RabbitMqOptionsValidator().Validate(null, rabbit);
                Assert.True(validation.Succeeded, string.Join("; ", validation.Failures ?? []));
                Assert.NotNull(rabbit.Hosts);
                Assert.Equal(["198.18.0.3"], rabbit.Hosts);
                Assert.Equal(5672, rabbit.Port);
                Assert.Equal(false, rabbit.EnableSsl);
                Assert.Equal("/", rabbit.VirtualHost);
                Assert.Equal(512, rabbit.ChannelPoolSize);
                Assert.Equal(1024, rabbit.WorkRequestMaxPayloadBytes);
                Assert.Equal("http://198.18.0.3:15672", rabbit.Management.BaseUrl);
                Assert.Equal(5, rabbit.Management.RequestTimeoutSeconds);
            }
            finally
            {
                directory.Delete(true);
            }
        }

        /// <summary>
        /// Shipped <c>VectorNNTPD.BackFillerService.json</c> retention matches the C# default and is valid on an 8 GiB host.
        /// </summary>
        [Fact]
        public void Shipped_backfiller_retention_matches_default_and_is_valid_on_minimum_physical_memory_host()
        {
            var path = RepositoryFiles.Under(
                "src",
                "VectorNNTPD.BackFillerService",
                "VectorNNTPD.BackFillerService.json");
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(path, optional: false, reloadOnChange: false)
                .Build();

            var shipped = new BackFillerOptions();
            configuration.GetSection(BackFillerOptions.SectionName).Bind(shipped);

            Assert.Equal(4, shipped.ArticleRetention.MaximumRetainedPayloadGigabytes);
            Assert.Equal(
                new BackFillerArticleRetentionOptions().MaximumRetainedPayloadGigabytes,
                shipped.ArticleRetention.MaximumRetainedPayloadGigabytes);
            Assert.True(
                shipped.ArticleRetention.MaximumRetainedPayloadGigabytes
                <= BackFillerOptionsValidator.ComputePhysicalMemoryRetentionCeilingGigabytes(
                    BackFillerArticleRetentionOptions.MinimumPhysicalMemoryBytes));

            var options = BackFillerTestOptions.CreateValid();
            options.ArticleRetention.MaximumRetainedPayloadGigabytes =
                shipped.ArticleRetention.MaximumRetainedPayloadGigabytes;
            var result = BackFillerTestOptions
                .CreateValidator(memory: new FakePhysicalMemoryProvider(
                    BackFillerArticleRetentionOptions.MinimumPhysicalMemoryBytes))
                .Validate(null, options);
            Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
        }

        /// <summary>
        /// Resolves the BackFiller build output for the same platform and configuration as this test assembly.
        /// </summary>
        /// <param name="repositoryRoot">Repository root that contains <c>src</c>.</param>
        /// <returns>The service project's <c>bin/x64/&lt;Configuration&gt;/net10.0</c> directory.</returns>
        private static string ServiceOutputDirectory(string repositoryRoot)
        {
            var configuration = "Release";
            var segments = AppContext.BaseDirectory.Split(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length - 2; index++)
            {
                if (segments[index].Equals("bin", StringComparison.OrdinalIgnoreCase)
                    && segments[index + 1].Equals("x64", StringComparison.OrdinalIgnoreCase))
                {
                    configuration = segments[index + 2];
                    break;
                }
            }

            return RelativePath.Under(
                repositoryRoot,
                "src",
                "VectorNNTPD.BackFillerService",
                "bin",
                "x64",
                configuration,
                "net10.0");
        }

        /// <summary>Returns file-backed configuration sources in registration order.</summary>
        /// <param name="configuration">Builder whose sources are filtered.</param>
        private static IEnumerable<FileConfigurationSource> FileSources(IConfigurationBuilder configuration)
            => configuration.Sources.OfType<FileConfigurationSource>();
    }
}
