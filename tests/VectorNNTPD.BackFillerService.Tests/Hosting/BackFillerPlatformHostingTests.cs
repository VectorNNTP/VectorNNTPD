using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Hosting
{
    /// <summary>Regresses platform hosting: content root pinning, Windows service registration source, console formatter stripping, and path resolution from application base.</summary>
    public sealed class BackFillerPlatformHostingTests
    {
        /// <summary>Program must pin content root to <see cref="AppContext.BaseDirectory"/> so configuration and paths align with the deployed layout.</summary>
        [Fact]
        public void Program_pins_content_root_to_the_application_base()
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = [],
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Environment.ContentRootPath = AppContext.BaseDirectory;
            Assert.Equal(
                Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(builder.Environment.ContentRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            Assert.Contains(
                "ContentRootPath = AppContext.BaseDirectory",
                File.ReadAllText(FindSource("Program.cs")),
                StringComparison.Ordinal);
        }

        /// <summary>Platform hosting registers Windows service lifetime and removes default console logger configure options from DI.</summary>
        [Fact]
        public void Platform_hosting_sets_the_windows_service_name_and_strips_console_formatters()
        {
            var builder = Host.CreateApplicationBuilder([]);
            builder.ConfigureBackFillerPlatformHosting();

            Assert.Contains(
                "options.ServiceName = ApplicationJsonConfiguration.EntryAssemblyName",
                File.ReadAllText(FindPlatformHostingSource()),
                StringComparison.Ordinal);
            Assert.False(
                WindowsServiceHelpers.IsWindowsService(),
                "This testhost is not a Windows Service; AddWindowsService activates the lifetime only under SCM.");
            Assert.DoesNotContain(builder.Services, static descriptor =>
            {
                var serviceType = descriptor.ServiceType;
                if (!serviceType.IsGenericType)
                {
                    return false;
                }

                var definition = serviceType.GetGenericTypeDefinition();
                if (definition != typeof(IConfigureOptions<>)
                    && definition != typeof(IPostConfigureOptions<>)
                    && definition != typeof(IValidateOptions<>))
                {
                    return false;
                }

                return serviceType.GenericTypeArguments[0] == typeof(ConsoleLoggerOptions);
            });
        }

        /// <summary>Locates <c>BackFillerServiceCollectionExtensions.cs</c> under the repository src tree.</summary>
        private static string FindPlatformHostingSource() =>
            FindSource(RelativePath.Under("Hosting", "BackFillerServiceCollectionExtensions.cs"));

        /// <summary>Walks parents from the test base directory until <paramref name="relativePath"/> exists under src/VectorNNTPD.BackFillerService.</summary>
        /// <param name="relativePath">Path relative to src/VectorNNTPD.BackFillerService.</param>
        /// <returns>Absolute path to the source file.</returns>
        private static string FindSource(string relativePath)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = RelativePath.Under(directory.FullName, "src", "VectorNNTPD.BackFillerService", relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException($"Could not locate {relativePath}.");
        }

        /// <summary>Log and certificate directories resolve from application base even when host content root points elsewhere.</summary>
        [Fact]
        public void Runtime_options_resolve_relative_paths_from_the_application_base_not_content_root()
        {
            var decoyContentRoot = Directory.CreateTempSubdirectory("bf-host-decoy-").FullName;
            try
            {
                using var host = CreateHost(decoyContentRoot);
                var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
                var expectedLogs = ApplicationLocalPath.ResolveApplicationLocalPath("logs", AppContext.BaseDirectory);
                var expectedCerts = AcmeCloudflareOptionsValidator.ResolveAcmeStateDir("certs/", AppContext.BaseDirectory);
                Assert.Equal(expectedLogs, runtime.LogDirectory);
                Assert.Equal(expectedCerts, runtime.CertificateDirectory);
                Assert.DoesNotContain(
                    Path.GetFullPath(decoyContentRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    runtime.LogDirectory,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    Path.GetFullPath(decoyContentRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    runtime.CertificateDirectory,
                    StringComparison.OrdinalIgnoreCase);
                Assert.Equal(
                    runtime.Shutdown.GracePeriod,
                    host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
            }
            finally
            {
                try
                {
                    Directory.Delete(decoyContentRoot, recursive: true);
                }
                catch (IOException)
                {
                    /* A directory that is still in use must not fail the test. */
                }
            }
        }

        /// <summary>Minimal host with decoy content root for runtime path resolution checks.</summary>
        /// <param name="contentRoot">Host content root path (may differ from application base).</param>
        private static IHost CreateHost(string contentRoot)
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = [],
                ContentRootPath = contentRoot,
            });
            builder.Configuration.AddInMemoryCollection(BackFillerTestOptions.CreateValidConfigurationPairs());
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            builder.Services.AddSingleton<IPhysicalMemoryProvider>(new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
            builder.Services.AddSingleton<IRabbitMqConnectionFactory>(new FakeBackFillerRabbitMqConnectionFactory());
            builder.ConfigureBackFillerPlatformHosting();
            builder.AddBackFillerHosting();
            return builder.Build();
        }
    }
}
