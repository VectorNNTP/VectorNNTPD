using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Configuration
{
    /// <summary>
    /// Proves BackFiller application-local paths resolve against
    /// <see cref="AppContext.BaseDirectory"/>, not the process working directory
    /// or an IDE/project content root.
    /// </summary>
    [Collection("WorkingDirectory")]

    public sealed class BackFillerApplicationLocalPathTests
    {
        /// <summary>
        /// Contract: Relative certs resolves beneath application base.
        /// </summary>

        [Fact]
        public void Relative_certs_resolves_beneath_application_base()
        {
            using var host = CreateHost(decoyContentRoot: CreateTempDir("bf-decoy-content-"));
            var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
            var acme = host.Services.GetRequiredService<IOptions<AcmeCloudflareOptions>>().Value;
            Assert.Equal(Expected("certs/"), runtime.CertificateDirectory);
            Assert.Equal(Expected("certs/"), acme.AcmeStateDir);
            Assert.StartsWith(
                Normalized(AppContext.BaseDirectory),
                runtime.CertificateDirectory,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Contract: Resolved paths are not based on process working directory.
        /// </summary>

        [Fact]
        public void Resolved_paths_are_not_based_on_process_working_directory()
        {
            var previous = Environment.CurrentDirectory;
            var cwd = CreateTempDir("bf-cwd-certs-");
            try
            {
                Environment.CurrentDirectory = cwd;
                using var host = CreateHost();
                var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
                Assert.Equal(Expected("certs/"), runtime.CertificateDirectory);
                Assert.Equal(Expected("logs"), runtime.LogDirectory);
                Assert.NotEqual(Path.GetFullPath("certs/"), runtime.CertificateDirectory);
                Assert.NotEqual(Path.GetFullPath("logs"), runtime.LogDirectory);
                Assert.DoesNotContain(Normalized(cwd), runtime.CertificateDirectory, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(Normalized(cwd), runtime.LogDirectory, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.CurrentDirectory = previous;
                TryDelete(cwd);
            }
        }

        /// <summary>
        /// Contract: Changing working directory does not change resolved paths.
        /// </summary>

        [Fact]
        public void Changing_working_directory_does_not_change_resolved_paths()
        {
            var previous = Environment.CurrentDirectory;
            var first = CreateTempDir("bf-cwd-a-");
            var second = CreateTempDir("bf-cwd-b-");
            try
            {
                Environment.CurrentDirectory = first;
                using var hostA = CreateHost();
                var runtimeA = hostA.Services.GetRequiredService<BackFillerRuntimeOptions>();
                Environment.CurrentDirectory = second;
                using var hostB = CreateHost();
                var runtimeB = hostB.Services.GetRequiredService<BackFillerRuntimeOptions>();
                Assert.Equal(runtimeA.CertificateDirectory, runtimeB.CertificateDirectory);
                Assert.Equal(runtimeA.LogDirectory, runtimeB.LogDirectory);
                Assert.Equal(Expected("certs/"), runtimeB.CertificateDirectory);
                Assert.Equal(Expected("logs"), runtimeB.LogDirectory);
            }
            finally
            {
                Environment.CurrentDirectory = previous;
                TryDelete(first);
                TryDelete(second);
            }
        }

        /// <summary>
        /// Contract: Explicit absolute paths remain absolute.
        /// </summary>

        [Fact]
        public void Explicit_absolute_paths_remain_absolute()
        {
            var absoluteCerts = CreateTempDir("bf-abs-certs-");
            var absoluteLogs = CreateTempDir("bf-abs-logs-");
            try
            {
                var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
                pairs["BackFiller:AcmeStateDir"] = absoluteCerts;
                pairs["BackFiller:Logging:File:LogDir"] = absoluteLogs;
                using var host = CreateHost(pairs: pairs);
                var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
                Assert.Equal(Expected(absoluteCerts), runtime.CertificateDirectory);
                Assert.Equal(Expected(absoluteLogs), runtime.LogDirectory);
            }
            finally
            {
                TryDelete(absoluteCerts);
                TryDelete(absoluteLogs);
            }
        }

        /// <summary>
        /// Contract: Explicit relative paths resolve beneath application base.
        /// </summary>

        [Fact]
        public void Explicit_relative_paths_resolve_beneath_application_base()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["BackFiller:AcmeStateDir"] = "nested-certs/";
            pairs["BackFiller:Logging:File:LogDir"] = "nested-logs";
            using var host = CreateHost(pairs: pairs);
            var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
            Assert.Equal(Expected("nested-certs/"), runtime.CertificateDirectory);
            Assert.Equal(Expected("nested-logs"), runtime.LogDirectory);
        }

        /// <summary>
        /// Invalid path or argument is rejected before resolution proceeds.
        /// </summary>

        [Fact]
        public void Relative_parent_segments_cannot_leave_the_application_base()
        {
            var root = CreateTempDir("bf-base-");
            var sibling = Path.GetFileName(root) + "-sibling";
            try
            {
                var error = Assert.Throws<ArgumentException>(
                    () => ApplicationLocalPath.ResolveApplicationLocalPath(
                        ".." + Path.DirectorySeparatorChar + sibling,
                        root));
                Assert.Equal("path", error.ParamName);
                Assert.Throws<ArgumentException>(
                    () => ApplicationLocalPath.ResolveApplicationLocalPath("certs/../../" + sibling, root));
            }
            finally
            {
                TryDelete(root);
            }
        }

        /// <summary>
        /// Contract: Relative dot segments that remain inside the base are kept.
        /// </summary>

        [Fact]
        public void Relative_dot_segments_that_remain_inside_the_base_are_kept()
        {
            var root = CreateTempDir("bf-inside-");
            try
            {
                var resolved = ApplicationLocalPath.ResolveApplicationLocalPath(
                    "nested" + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "certs",
                    root);
                Assert.Equal(
                    ApplicationLocalPath.ResolveApplicationLocalPath("certs", root),
                    resolved);
            }
            finally
            {
                TryDelete(root);
            }
        }

        /// <summary>
        /// Contract: Rooted paths may leave the application base.
        /// </summary>

        [Fact]
        public void Rooted_paths_may_leave_the_application_base()
        {
            var root = CreateTempDir("bf-root-boundary-");
            var absolute = CreateTempDir(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) + "-sibling-");
            try
            {
                Assert.Equal(
                    Normalized(absolute),
                    ApplicationLocalPath.ResolveApplicationLocalPath(absolute, root));

                if (OperatingSystem.IsWindows())
                {
                    var driveRoot = Path.GetPathRoot(root)!;
                    var drivePath = Path.Join(driveRoot, "bf-drive-rooted");
                    Assert.Equal(
                        Normalized(drivePath),
                        ApplicationLocalPath.ResolveApplicationLocalPath(drivePath, root));
                }

                var unixRooted = "/bf-unix-rooted";
                Assert.True(Path.IsPathRooted(unixRooted));
                Assert.Equal(
                    Normalized(Path.GetFullPath(unixRooted)),
                    ApplicationLocalPath.ResolveApplicationLocalPath(unixRooted, root));
            }
            finally
            {
                TryDelete(root);
                TryDelete(absolute);
            }
        }

        /// <summary>
        /// Contract: Backfiller uses the same common path resolution as nntpd.
        /// </summary>

        [Fact]
        public void Backfiller_uses_the_same_common_path_resolution_as_nntpd()
        {
            using var host = CreateHost();
            var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
            Assert.Equal(
                AcmeCloudflareOptionsValidator.ResolveAcmeStateDir("certs/", AppContext.BaseDirectory),
                runtime.CertificateDirectory);
            Assert.Equal(
                ApplicationLocalPath.ResolveApplicationLocalPath("certs/", AppContext.BaseDirectory),
                runtime.CertificateDirectory);
            Assert.Equal(
                ApplicationLocalPath.ResolveApplicationLocalPath("logs", AppContext.BaseDirectory),
                runtime.LogDirectory);
            Assert.Equal("VectorNNTPD.Common", typeof(ApplicationLocalPath).Assembly.GetName().Name);
            Assert.Null(typeof(BackFillerRuntimeOptions).Assembly.GetType(
                "VectorNNTPD.BackFillerService.Configuration.ApplicationLocalPath"));
            Assert.Contains(
                "ApplicationLocalPath.ResolveApplicationLocalPath",
                File.ReadAllText(FindRepoFile(RelativePath.Under("src", "VectorNNTPD.BackFillerService", "Hosting", "BackFillerServiceCollectionExtensions.cs"))),
                StringComparison.Ordinal);
            Assert.Contains(
                "ApplicationLocalPath.ResolveApplicationLocalPath",
                File.ReadAllText(FindRepoFile(RelativePath.Under("src", "VectorNNTPD.BackFillerService", "Configuration", "BackFillerRuntimeOptionsFactory.cs"))),
                StringComparison.Ordinal);
            Assert.Contains(
                "ApplicationLocalPath.ResolveApplicationLocalPath",
                File.ReadAllText(FindRepoFile(RelativePath.Under("src", "VectorNNTPD.BackFillerService", "Logging", "BackFillerFileLogging.cs"))),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Contract: Hosting does not create certs or logs in the working directory.
        /// </summary>

        [Fact]
        public void Hosting_does_not_create_certs_or_logs_in_the_working_directory()
        {
            var previous = Environment.CurrentDirectory;
            var cwd = CreateTempDir("bf-create-cwd-");
            try
            {
                Environment.CurrentDirectory = cwd;
                using var host = CreateHost();
                _ = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
                Assert.False(Directory.Exists(Path.Join(cwd, "certs")));
                Assert.False(Directory.Exists(Path.Join(cwd, "logs")));
            }
            finally
            {
                Environment.CurrentDirectory = previous;
                TryDelete(cwd);
            }
        }

        /// <summary>Resolves <paramref name="configured"/> against the test output directory, matching production path rules.</summary>
        /// <param name="configured">Path value from configuration.</param>
        private static string Expected(string configured) =>
            ApplicationLocalPath.ResolveApplicationLocalPath(configured, AppContext.BaseDirectory);

        /// <summary>Full path without a trailing separator, so assertions do not depend on a trailing slash.</summary>
        /// <param name="path">Path to normalize.</param>
        private static string Normalized(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        /// <summary>
        /// Host whose content root can be a decoy. Configuration comes from <paramref name="pairs"/> or the valid fixture pairs.
        /// </summary>
        /// <param name="decoyContentRoot">Content root that must not become the certificate or log directory. Null uses the test output directory.</param>
        /// <param name="pairs">Configuration overlay. Null uses <see cref="BackFillerTestOptions.CreateValidConfigurationPairs"/>.</param>
        private static IHost CreateHost(
            string? decoyContentRoot = null,
            Dictionary<string, string?>? pairs = null)
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = [],
                ContentRootPath = decoyContentRoot ?? AppContext.BaseDirectory,
            });
            builder.Configuration.AddInMemoryCollection(pairs ?? BackFillerTestOptions.CreateValidConfigurationPairs());
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            builder.Services.AddSingleton<IPhysicalMemoryProvider>(new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
            builder.Services.AddSingleton<IRabbitMqConnectionFactory>(new FakeBackFillerRabbitMqConnectionFactory());
            builder.ConfigureBackFillerPlatformHosting();
            builder.AddBackFillerHosting();
            return builder.Build();
        }

        /// <summary>Creates a temporary directory the test must delete.</summary>
        /// <param name="prefix">Directory name prefix.</param>
        private static string CreateTempDir(string prefix) =>
            Directory.CreateTempSubdirectory(prefix).FullName;

        /// <summary>Deletes a temporary directory. An in-use directory is left behind so cleanup cannot fail the test.</summary>
        /// <param name="path">Directory created by the test.</param>
        private static void TryDelete(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                /* A directory that is still in use must not fail the test. */
            }
        }

        /// <summary>Walks parents of the test output directory until <paramref name="relativePath"/> exists.</summary>
        /// <param name="relativePath">Path under the repository root, using separators <c>RelativePath.Under</c> accepts.</param>
        /// <exception cref="FileNotFoundException">No parent contains that path.</exception>
        private static string FindRepoFile(string relativePath)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = RelativePath.Under(directory.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException($"Could not locate {relativePath}.");
        }
    }
}
