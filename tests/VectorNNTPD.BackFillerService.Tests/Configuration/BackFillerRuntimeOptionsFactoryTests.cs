using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Tests.Fixtures;

namespace VectorNNTPD.BackFillerService.Tests.Configuration
{
    /// <summary>
    /// Asserts BackFillerRuntimeOptionsFactory snapshots normalized runtime fields and path resolution.
    /// </summary>
    [Collection("WorkingDirectory")]
    public sealed class BackFillerRuntimeOptionsFactoryTests
    {
        /// <summary>
        /// Contract: Create projects normalized identity and paths.
        /// </summary>

        [Fact]
        public void Create_projects_normalized_identity_and_paths()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.DnsSuffix = "Usenet.Ninja.";
            var rabbit = BackFillerTestOptions.CreateValidRabbitMq();
            rabbit.Hosts = [" 127.0.0.1 ", "localhost", "LOCALHOST", "127.0.0.1"];
            rabbit.WorkRequestMaxPayloadBytes = 2048;
            rabbit.PublishConfirmTimeoutSeconds = 7;
            rabbit.ConsumerPrefetchCount = 3;

            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb(),
                BackFillerTestOptions.CreateValidAcme(),
                rabbit);

            Assert.Equal("usenet.ninja", runtime.DnsSuffix);
            Assert.Equal("backfiller01.usenet.ninja", runtime.Fqdn);
            Assert.Equal(1, runtime.ServerId);
            Assert.Equal(1190, runtime.BindPortTls);
            Assert.Equal(2048, runtime.RabbitMq.WorkRequestMaxPayloadBytes);
            Assert.Equal(7, runtime.RabbitMq.PublishConfirmTimeoutSeconds);
            Assert.Equal((ushort)3, runtime.RabbitMq.ConsumerPrefetchCount);
            Assert.Equal(3, runtime.RabbitMq.QuorumInitialGroupSize);
            Assert.Equal(10, runtime.RabbitMq.DeliveryLimit);
            Assert.True(Path.IsPathRooted(runtime.LogDirectory));
            Assert.True(Path.IsPathRooted(runtime.CertificateDirectory));
            Assert.Equal("127.0.0.1", runtime.NntpDb.Server);
            Assert.Equal("nntp", runtime.NntpDb.Database);
            Assert.Equal(TimeSpan.FromSeconds(30), runtime.Shutdown.GracePeriod);
            Assert.True(runtime.Shutdown.DrainQueuedWork);
            Assert.True(runtime.Shutdown.FinishActiveArticles);
            rabbit.WorkRequestMaxPayloadBytes = 512;
            Assert.Equal("backfiller01.usenet.ninja", runtime.Fqdn);
            Assert.Equal(2048, runtime.RabbitMq.WorkRequestMaxPayloadBytes);
            Assert.Equal(4L * 1024 * 1024 * 1024, runtime.ArticleRetention.MaximumRetainedPayloadBytes);
            Assert.Equal(16, runtime.ArticleRetention.MaxOpenableRequestIdsPerArticle);
            Assert.Equal(TimeSpan.FromSeconds(60), runtime.AccountRefreshInterval);
            options.BackFillerAccountRefreshIntervalSeconds = 90;
            Assert.Equal(TimeSpan.FromSeconds(60), runtime.AccountRefreshInterval);
        }

        /// <summary>
        /// Contract: Runtime certificate names are only the backfiller fqdn.
        /// </summary>

        [Fact]
        public void Runtime_certificate_names_are_only_the_backfiller_fqdn()
        {
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb(),
                BackFillerTestOptions.CreateValidAcme());

            Assert.Equal(["backfiller01.usenet.ninja"], runtime.CertificateDomainNames);
            Assert.DoesNotContain("news.usenet.ninja", runtime.CertificateDomainNames);
        }

        /// <summary>
        /// Contract: Non-identity runtime fields stay immutable via record <c>with</c>; DNS identity is updated in place.
        /// </summary>

        [Fact]
        public void Runtime_options_share_mutable_dns_identity_across_with_copies()
        {
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb());

            Assert.True(runtime.GetType().IsSealed);
            var withInterval = runtime with { AccountRefreshInterval = TimeSpan.FromSeconds(12) };
            Assert.NotSame(runtime, withInterval);
            Assert.Same(runtime.DnsIdentity, withInterval.DnsIdentity);
            Assert.Equal("backfiller01.usenet.ninja", runtime.Fqdn);

            runtime.ApplySharedDnsIdentity("other.example", "backfiller01.other.example", includeNewsHostnameInCertificate: false);
            Assert.Equal("backfiller01.other.example", runtime.Fqdn);
            Assert.Equal("backfiller01.other.example", withInterval.Fqdn);
            Assert.Null(runtime.GetType().GetProperty("Name"));
        }

        /// <summary>
        /// Contract: Shutdown flags are snapshotted independently and do not follow later option mutation.
        /// </summary>

        [Fact]
        public void Shutdown_flags_are_snapshotted_independently_and_do_not_follow_later_option_mutation()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.Shutdown.DrainQueuedWork = false;
            options.Shutdown.FinishActiveArticles = false;
            options.Shutdown.GracePeriodSeconds = 45;
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb());

            options.Shutdown.DrainQueuedWork = true;
            options.Shutdown.FinishActiveArticles = true;
            options.Shutdown.GracePeriodSeconds = 90;

            Assert.False(runtime.Shutdown.DrainQueuedWork);
            Assert.False(runtime.Shutdown.FinishActiveArticles);
            Assert.Equal(TimeSpan.FromSeconds(45), runtime.Shutdown.GracePeriod);
        }

        /// <summary>
        /// Contract: Relative directories resolve against the content root not the working directory.
        /// </summary>

        [Fact]
        public void Relative_directories_resolve_against_the_content_root_not_the_working_directory()
        {
            var previous = Environment.CurrentDirectory;
            var contentRoot = Directory.CreateTempSubdirectory("bf-content-").FullName;
            var otherCwd = Directory.CreateTempSubdirectory("bf-cwd-").FullName;
            try
            {
                Environment.CurrentDirectory = otherCwd;
                var options = BackFillerTestOptions.CreateValid();
                options.Logging.File.LogDir = "logs";
                options.CertificateDirectory = "certs";
                options.AcmeStateDir = "certs";
                var runtime = BackFillerRuntimeOptionsFactory.Create(
                    options,
                    BackFillerTestOptions.CreateValidNntpDb(),
                    contentRoot);

                Assert.Equal(Path.GetFullPath(Path.Join(contentRoot, "logs")), runtime.LogDirectory);
                Assert.Equal(Path.GetFullPath(Path.Join(contentRoot, "certs")), runtime.CertificateDirectory);
                Assert.NotEqual(Path.GetFullPath(Path.Join(otherCwd, "certs")), runtime.CertificateDirectory);
            }
            finally
            {
                Environment.CurrentDirectory = previous;
                TryDelete(contentRoot);
                TryDelete(otherCwd);
            }
        }

        /// <summary>
        /// Contract: Absolute directories remain absolute.
        /// </summary>

        [Fact]
        public void Absolute_directories_remain_absolute()
        {
            var contentRoot = Directory.CreateTempSubdirectory("bf-content-abs-").FullName;
            var absoluteLogs = Directory.CreateTempSubdirectory("bf-abs-logs-").FullName;
            var absoluteCerts = Directory.CreateTempSubdirectory("bf-abs-certs-").FullName;
            try
            {
                var options = BackFillerTestOptions.CreateValid();
                options.Logging.File.LogDir = absoluteLogs;
                options.CertificateDirectory = absoluteCerts;
                options.AcmeStateDir = absoluteCerts;
                var runtime = BackFillerRuntimeOptionsFactory.Create(
                    options,
                    BackFillerTestOptions.CreateValidNntpDb(),
                    contentRoot);

                Assert.Equal(Path.GetFullPath(absoluteLogs), runtime.LogDirectory);
                Assert.Equal(Path.GetFullPath(absoluteCerts), runtime.CertificateDirectory);
            }
            finally
            {
                TryDelete(contentRoot);
                TryDelete(absoluteLogs);
                TryDelete(absoluteCerts);
            }
        }

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
            catch (UnauthorizedAccessException)
            {
                /* A directory that cannot be deleted must not fail the test. */
            }
        }
    }
}
