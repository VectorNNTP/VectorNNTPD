using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Configuration
{
    /// <summary>
    /// BackFiller <c>ServerId</c> cases mirrored from NNTPD
    /// <c>NntpdOptionsValidatorTests</c>.
    /// </summary>
    public sealed class BackFillerServerIdTests
    {
        /// <summary>
        /// Contract: MissingServerId FailsValidationAndIsDistinctFromZero.
        /// </summary>

        [Fact]
        public void MissingServerId_FailsValidationAndIsDistinctFromZero()
        {
            var missing = BackFillerTestOptions.CreateValid();
            missing.ServerId = null;
            var missingResult = BackFillerTestOptions.CreateValidator().Validate(null, missing);
            Assert.True(missingResult.Failed);
            Assert.Contains(missingResult.Failures!, static f => f.Contains("required", StringComparison.OrdinalIgnoreCase));

            var zero = BackFillerTestOptions.CreateValid();
            zero.ServerId = 0;
            var zeroResult = BackFillerTestOptions.CreateValidator().Validate(null, zero);
            Assert.True(zeroResult.Failed);
            Assert.Contains(zeroResult.Failures!, static f => f.Contains("1–255", StringComparison.Ordinal));

            Assert.Null(missing.ServerId);
            Assert.Equal(0, zero.ServerId);
        }

        /// <summary>
        /// Valid baseline configuration passes validation.
        /// </summary>

        [Theory]
        [InlineData(1)]
        [InlineData(99)]
        [InlineData(100)]
        [InlineData(255)]
        public void ServerId_ValidBoundaries_Succeed(int serverId)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.ServerId = serverId;
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Succeeded);
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(256)]
        public void ServerId_OutOfRange_Fails(int serverId)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.ServerId = serverId;
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static f => f.Contains("1–255", StringComparison.Ordinal));
        }

        /// <summary>
        /// Valid baseline configuration passes validation.
        /// </summary>

        [Theory]
        [InlineData(1, "backfiller01.usenet.ninja")]
        [InlineData(8, "backfiller08.usenet.ninja")]
        [InlineData(9, "backfiller09.usenet.ninja")]
        [InlineData(10, "backfiller10.usenet.ninja")]
        [InlineData(99, "backfiller99.usenet.ninja")]
        public void Fqdn_UsesExactFormat_WithoutDotBeforeId(int serverId, string expected)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.ServerId = serverId;
            options.DnsSuffix = "usenet.ninja";

            Assert.Equal(expected, options.Fqdn);
            Assert.Equal(
                ApplicationFqdn.Build(BackFillerOptions.ApplicationPrefix, serverId, "usenet.ninja"),
                options.Fqdn);
            Assert.NotEqual(
                ApplicationFqdn.Build("nntpd", serverId, "usenet.ninja"),
                options.Fqdn);
            Assert.DoesNotContain("backfiller.", options.Fqdn, StringComparison.Ordinal);
            Assert.StartsWith($"backfiller{serverId:00}.", options.Fqdn, StringComparison.Ordinal);

            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Succeeded);

            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb(),
                BackFillerTestOptions.CreateValidAcme(options));
            Assert.Equal(serverId, runtime.ServerId);
            Assert.Equal(expected, runtime.Fqdn);
        }

        /// <summary>
        /// Contract: Bind ConsumesBackFillerServerId NotRootOrEnvironmentAlias.
        /// </summary>

        [Fact]
        public void Bind_ConsumesBackFillerServerId_NotRootOrEnvironmentAlias()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["ServerId"] = "99";
            pairs["VECTOR__SERVERID"] = "77";
            pairs["BackFiller:ServerId"] = "8";

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(pairs)
                .Build();
            var options = new BackFillerOptions();
            configuration.GetSection(BackFillerOptions.SectionName).Bind(options);

            Assert.Equal(8, options.ServerId);
            Assert.Equal("backfiller08.usenet.ninja", options.Fqdn);
        }

        /// <summary>
        /// Host build or options resolution fails validation before the process runs.
        /// </summary>

        [Fact]
        public void MissingServerId_FromConfiguration_FailsStartup()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs.Remove("BackFiller:ServerId");

            var ex = Assert.Throws<OptionsValidationException>(() =>
            {
                using var host = CreateIsolatedHost(pairs);
                _ = host.Services.GetRequiredService<IOptions<BackFillerOptions>>().Value;
            });
            Assert.Contains("ServerId", ex.Message, StringComparison.Ordinal);
            Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BACKFILLER__SERVERID", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("VECTOR__SERVERID", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Host build or options resolution fails validation before the process runs.
        /// </summary>

        [Fact]
        public void ServerId_NullFromConfiguration_FailsStartup()
        {
            var json = """
                       {
                         "BackFiller": {
                           "ServerId": null,
                           "DnsSuffix": "usenet.ninja",
                           "CloudFlareZoneId": "0123456789abcdef0123456789abcdef",
                           "BindAddress": [ "127.0.0.1" ],
                           "BindPortTls": 1190,
                           "Logging": { "File": { "LogDir": "logs" } }
                         },
                         "RabbitMQ": {
                           "Hosts": [ "127.0.0.1" ],
                           "Username": "nntparticles",
                           "Password": "unit-test-password",
                           "EnableSsl": false
                         },
                         "ConnectionStrings": {
                           "NntpDB": "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz"
                         }
                       }
                       """;

            var builder = CreateIsolatedBuilder();
            using var jsonStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            builder.Configuration.AddJsonStream(jsonStream);
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CloudFlareApiKey"] = BackFillerTestOptions.SecretToken,
                    ["AcmeCertificatePassword"] = BackFillerTestOptions.SecretPfx,
                    [AcmeCloudflareOptions.AcmeAccountConfigurationKey] = "security@usenet.ninja",
                });
            RegisterHostFakes(builder);
            builder.AddBackFillerHosting();

            var ex = Assert.Throws<OptionsValidationException>(() =>
            {
                using var host = builder.Build();
            });
            Assert.Contains("ServerId", ex.Message, StringComparison.Ordinal);
            Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Contract: Host runtime uses section server id for fqdn.
        /// </summary>

        [Fact]
        public void Host_runtime_uses_section_server_id_for_fqdn()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["BackFiller:ServerId"] = "12";
            pairs["ServerId"] = "99";

            using var host = CreateHost(pairs);
            var identity = host.Services.GetRequiredService<IOptions<BackFillerOptions>>().Value;
            var acme = host.Services.GetRequiredService<IOptions<AcmeCloudflareOptions>>().Value;
            var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();

            Assert.Equal(12, identity.ServerId);
            Assert.Equal("backfiller12.usenet.ninja", identity.Fqdn);
            Assert.Equal("backfiller12.usenet.ninja", acme.Fqdn);
            Assert.Equal(12, runtime.ServerId);
            Assert.Equal("backfiller12.usenet.ninja", runtime.Fqdn);
        }

        /// <summary>
        /// Contract: Production configuration declares server id under backfiller.
        /// </summary>

        [Fact]
        public void Production_configuration_declares_server_id_under_backfiller()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            string? path = null;
            while (directory is not null)
            {
                var candidate = RelativePath.Under(directory.FullName, "src", "VectorNNTPD.BackFillerService", "VectorNNTPD.BackFillerService.json");
                if (File.Exists(candidate))
                {
                    path = candidate;
                    break;
                }

                directory = directory.Parent;
            }

            Assert.NotNull(path);
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var section = root.GetProperty("BackFiller");

            Assert.False(root.TryGetProperty("ServerId", out _));
            Assert.Equal(1, section.GetProperty("ServerId").GetInt32());
            Assert.False(section.TryGetProperty("Name", out _));
        }

        /// <summary>
        /// Contract: Shared rules match nntpd bounds.
        /// </summary>

        [Fact]
        public void Shared_rules_match_nntpd_bounds()
        {
            Assert.Equal(1, ServerIdRules.MinimumInclusive);
            Assert.Equal(255, ServerIdRules.MaximumInclusive);
            Assert.Equal(ServerIdRules.MinimumInclusive, BackFillerIdentity.MinimumServerId);
            Assert.Equal(ServerIdRules.MaximumInclusive, BackFillerIdentity.MaximumServerId);
            Assert.Equal(ServerIdValidationStatus.Missing, ServerIdRules.Classify(null));
            Assert.Equal(ServerIdValidationStatus.OutOfRange, ServerIdRules.Classify(0));
            Assert.Equal(ServerIdValidationStatus.Valid, ServerIdRules.Classify(1));
            Assert.Equal(ServerIdValidationStatus.Valid, ServerIdRules.Classify(99));
            Assert.Equal(ServerIdValidationStatus.Valid, ServerIdRules.Classify(100));
            Assert.Equal(ServerIdValidationStatus.Valid, ServerIdRules.Classify(255));
            Assert.Equal(ServerIdValidationStatus.OutOfRange, ServerIdRules.Classify(256));
        }

        /// <summary>
        /// Builds a hosted BackFiller instance from in-memory configuration pairs with test infrastructure fakes registered.
        /// </summary>
        /// <param name="pairs">Configuration key/value pairs (including secrets when required).</param>
        private static IHost CreateHost(Dictionary<string, string?> pairs) =>
            CreateIsolatedHost(pairs);

        /// <summary>
        /// Creates an empty application builder, binds configuration, registers fakes, and returns a built host.
        /// </summary>
        /// <param name="pairs">Configuration key/value pairs merged into the host configuration.</param>
        private static IHost CreateIsolatedHost(Dictionary<string, string?> pairs)
        {
            var builder = CreateIsolatedBuilder();
            builder.Configuration.AddInMemoryCollection(pairs);
            RegisterHostFakes(builder);
            builder.AddBackFillerHosting();
            return builder.Build();
        }

        /// <summary>
        /// Empty Generic Host builder named for the test assembly in the Development environment.
        /// </summary>

        private static HostApplicationBuilder CreateIsolatedBuilder() =>
            Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
            {
                ApplicationName = "VectorNNTPD.BackFillerService.Tests",
                EnvironmentName = Environments.Development,
            });

        /// <summary>
        /// Registers no-op or deterministic doubles for external dependencies so host startup tests avoid real infrastructure.
        /// </summary>
        /// <param name="builder">Host builder whose service collection receives the fakes.</param>
        private static void RegisterHostFakes(HostApplicationBuilder builder)
        {
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            builder.Services.AddSingleton<VectorNNTPD.Common.Cloudflare.ICloudflareDnsReconciler>(
                new NoOpCloudflareDnsReconciler());
            builder.Services.AddSingleton<IPhysicalMemoryProvider>(
                new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
            builder.Services.AddSingleton<VectorNNTPD.Common.Messaging.RabbitMq.IRabbitMqConnectionFactory>(
                new FakeBackFillerRabbitMqConnectionFactory());
            builder.Services.AddSingleton<VectorNNTPD.BackFillerService.Accounts.IProviderAccountSource>(
                new FakeProviderAccountSource());
        }
    }
}
