using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Hosting;

namespace VectorNNTPD.BackFillerService.Tests.Configuration
{
    /// <summary>
    /// Exercises BackFillerOptionsValidator, related validators, and binding edge cases for secrets and legacy keys.
    /// </summary>

    public sealed class BackFillerOptionsValidatorTests
    {
        /// <summary>
        /// Valid baseline configuration passes validation.
        /// </summary>

        [Fact]
        public void Validate_succeeds_for_complete_valid_options()
        {
            var options = BackFillerTestOptions.CreateValid();
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Succeeded);
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(3651)]
        public void Validate_fails_when_log_retention_days_is_out_of_range(int days)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.Logging.LogRetentionDays = days;
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                static failure => failure.Contains("BackFiller:Logging:LogRetentionDays", StringComparison.Ordinal));
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Theory]
        [InlineData("Trace")]
        [InlineData("Critical")]
        [InlineData("nope")]
        [InlineData("")]
        public void Validate_fails_when_log_level_is_not_a_serilog_level(string level)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.Logging.LogLevel = level;
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                static failure => failure.Contains(
                    "BackFiller:Logging:LogLevel must be one of Verbose, Debug, Information, Warning, Error, Fatal.",
                    StringComparison.Ordinal));
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Fact]
        public void Validate_fails_when_server_id_is_missing()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.ServerId = null;
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static f => f.Contains("ServerId", StringComparison.Ordinal));
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(256)]
        public void Validate_fails_for_server_id_outside_1_to_255(int serverId)
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

        [Fact]
        public void Validate_does_not_require_a_configurable_name()
        {
            var options = BackFillerTestOptions.CreateValid();
            Assert.Null(typeof(BackFillerOptions).GetProperty("Name"));
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Succeeded);
            Assert.Equal("backfiller01.usenet.ninja", options.Fqdn);
        }

        /// <summary>
        /// Valid baseline configuration passes validation.
        /// </summary>

        [Fact]
        public void Validate_does_not_require_common_cleartext_bind_port()
        {
            var acme = BackFillerTestOptions.CreateValidAcme();
            acme.BindPort = 0;
            var result = new AcmeCloudflareOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
                .Validate(null, acme);
            Assert.True(result.Succeeded);
            Assert.DoesNotContain(
                result.Failures ?? [],
                static f => f.Contains("BindPort", StringComparison.Ordinal)
                            && !f.Contains("BindPortTls", StringComparison.Ordinal));
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Fact]
        public void Validate_fails_when_nested_bind_port_tls_is_missing()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.BindPortTls = null;
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static f => f.Contains("BindPortTls", StringComparison.Ordinal));
            Assert.Contains(result.Failures!, static f => f.Contains("TLS-only", StringComparison.Ordinal));
        }

        /// <summary>
        /// Valid baseline configuration passes validation.
        /// </summary>

        [Fact]
        public void Validate_does_not_require_application_section_acme_email()
        {
            var options = BackFillerTestOptions.CreateValid();
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Succeeded);
            Assert.DoesNotContain(
                result.Failures ?? [],
                static f => f.Contains("AcmeEmail", StringComparison.Ordinal));
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Fact]
        public void Validate_fails_when_drain_timeout_exceeds_grace_period()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.Shutdown.GracePeriodSeconds = 30;
            var rabbit = BackFillerTestOptions.CreateValidRabbitMq();
            rabbit.MaximumShutdownDrainTimeoutSeconds = 31;
            var result = BackFillerTestOptions.CreateValidator(rabbitMq: rabbit).Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                static f => f.Contains("MaximumShutdownDrainTimeoutSeconds", StringComparison.Ordinal)
                            && f.Contains("GracePeriodSeconds", StringComparison.Ordinal));
        }

        /// <summary>
        /// BackFillerOptionsValidator enforces the documented constraint.
        /// </summary>

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void Validate_accepts_independent_drain_and_finish_shutdown_flags(bool drainQueuedWork, bool finishActiveArticles)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.Shutdown.DrainQueuedWork = drainQueuedWork;
            options.Shutdown.FinishActiveArticles = finishActiveArticles;
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.False(result.Failed);
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(257)]
        public void Validate_fails_for_max_openable_request_ids_outside_1_to_256(int value)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.ArticleRetention.MaxOpenableRequestIdsPerArticle = value;
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                static f => f.Contains("MaxOpenableRequestIdsPerArticle", StringComparison.Ordinal));
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Fact]
        public void Validate_fails_when_retention_exceeds_physical_memory_ceiling()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.ArticleRetention.MaximumRetainedPayloadGigabytes = 8;
            var memory = new FakePhysicalMemoryProvider(4L * 1024 * 1024 * 1024);
            var result = BackFillerTestOptions.CreateValidator(memory: memory).Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static f => f.Contains("physical-memory", StringComparison.Ordinal));
        }

        /// <summary>
        /// Valid baseline configuration passes validation.
        /// </summary>

        [Fact]
        public void Validate_accepts_configured_four_gib_below_the_physical_memory_ceiling()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.ArticleRetention.MaximumRetainedPayloadGigabytes = 4;
            var result = BackFillerTestOptions
                .CreateValidator(memory: new FakePhysicalMemoryProvider(16L * 1024 * 1024 * 1024))
                .Validate(null, options);
            Assert.True(result.Succeeded);
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Fact]
        public void Validate_fails_when_physical_memory_cannot_be_determined()
        {
            var options = BackFillerTestOptions.CreateValid();
            var result = BackFillerTestOptions
                .CreateValidator(memory: new FailingPhysicalMemoryProvider())
                .Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                static f => f.Contains("total physical memory could not be determined", StringComparison.Ordinal));
            Assert.DoesNotContain(
                result.Failures!,
                static f => f.Contains("80% physical-memory ceiling", StringComparison.Ordinal));
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Fact]
        public void Validate_fails_when_explicit_bind_address_is_not_local()
        {
            var acme = BackFillerTestOptions.CreateValidAcme();
            acme.BindAddress = ["198.51.100.10"];
            var result = new AcmeCloudflareOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: false))
                .Validate(null, acme);
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static f => f.Contains("BindAddress", StringComparison.Ordinal));
        }

        /// <summary>
        /// Valid baseline configuration passes validation.
        /// </summary>

        [Fact]
        public void Validate_allows_omitted_bind_address()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.BindAddress = null;
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Succeeded);
        }

        /// <summary>
        /// Valid baseline configuration passes validation.
        /// </summary>

        [Theory]
        [InlineData(1)]
        [InlineData(8)]
        [InlineData(1024)]
        public void Validate_accepts_valid_max_concurrent_session_establishments(int value)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.Nntp.MaxConcurrentSessionEstablishments = value;
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Succeeded);
        }

        /// <summary>
        /// Valid baseline configuration passes validation.
        /// </summary>

        [Fact]
        public void Validate_uses_default_max_concurrent_session_establishments_when_omitted()
        {
            var options = BackFillerTestOptions.CreateValid();
            Assert.Equal(
                BackFillerNntpOptions.DefaultMaxConcurrentSessionEstablishments,
                options.Nntp.MaxConcurrentSessionEstablishments);
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Succeeded);
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(1025)]
        [InlineData(int.MaxValue)]
        public void Validate_fails_for_invalid_max_concurrent_session_establishments(int value)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.Nntp.MaxConcurrentSessionEstablishments = value;
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                static f => f.Contains("BackFiller:Nntp:MaxConcurrentSessionEstablishments", StringComparison.Ordinal));
        }

        /// <summary>
        /// Contract: Runtime options project max concurrent session establishments.
        /// </summary>

        [Fact]
        public void Runtime_options_project_max_concurrent_session_establishments()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.Nntp.MaxConcurrentSessionEstablishments = 4;
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb());
            Assert.Equal(4, runtime.Nntp.MaxConcurrentSessionEstablishments);
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Theory]
        [InlineData(4)]
        [InlineData(3601)]
        public void Validate_fails_when_account_refresh_interval_is_out_of_range(int seconds)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.BackFillerAccountRefreshIntervalSeconds = seconds;
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                static f => f.Contains("BackFillerAccountRefreshIntervalSeconds", StringComparison.Ordinal));
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Fact]
        public void Validate_fails_when_nntp_db_is_missing()
        {
            var result = new NntpDbOptionsValidator()
                .Validate(null, new NntpDbOptions());
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static f => f.Contains("ConnectionStrings:NntpDB", StringComparison.Ordinal));
        }

        /// <summary>
        /// Validation failure messages must not echo configured secrets.
        /// </summary>

        [Fact]
        public void Validate_failures_do_not_include_secret_values()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.ServerId = null;
            var rabbit = BackFillerTestOptions.CreateValidRabbitMq();
            rabbit.Password = BackFillerTestOptions.SecretPassword;
            var result = BackFillerTestOptions.CreateValidator(rabbitMq: rabbit).Validate(null, options);
            Assert.True(result.Failed);
            Assert.All(
                result.Failures!,
                static failure =>
                {
                    Assert.DoesNotContain(BackFillerTestOptions.SecretPassword, failure, StringComparison.Ordinal);
                    Assert.DoesNotContain(BackFillerTestOptions.SecretToken, failure, StringComparison.Ordinal);
                    Assert.DoesNotContain(BackFillerTestOptions.SecretPfx, failure, StringComparison.Ordinal);
                    Assert.DoesNotContain("db-secret-xyz", failure, StringComparison.Ordinal);
                });
        }

        /// <summary>
        /// Contract: Bind uses top level rabbitmq path.
        /// </summary>

        [Fact]
        public void Bind_uses_top_level_rabbitmq_path()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BackFiller:RabbitMQ:Username"] = "nested-user",
                    ["BackFiller:RabbitMQ:Password"] = "nested-password",
                    ["RabbitMQ:Username"] = "canonical-user",
                    ["RabbitMQ:Password"] = BackFillerTestOptions.SecretPassword,
                })
                .Build();
            var options = new BackFillerOptions();
            configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
            var rabbit = new VectorNNTPD.Common.Messaging.RabbitMq.RabbitMqOptions();
            configuration.GetSection("RabbitMQ").Bind(rabbit);
            Assert.Null(typeof(BackFillerOptions).GetProperty("RabbitMQ"));
            Assert.Equal("canonical-user", rabbit.Username);
            Assert.Equal(BackFillerTestOptions.SecretPassword, rabbit.Password);
        }

        /// <summary>
        /// Contract: Bind does not map nested backfiller rabbitmq onto backfiller options.
        /// </summary>

        [Fact]
        public void Bind_does_not_map_nested_backfiller_rabbitmq_onto_backfiller_options()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BackFiller:RabbitMQ:Username"] = "nested-user",
                    ["BackFiller:RabbitMQ:Password"] = BackFillerTestOptions.SecretPassword,
                })
                .Build();
            var options = new BackFillerOptions();
            configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
            Assert.Null(typeof(BackFillerOptions).GetProperty("RabbitMQ"));
        }

        /// <summary>
        /// Contract: Bind uses canonical nntp db connection string path.
        /// </summary>

        [Fact]
        public void Bind_uses_canonical_nntp_db_connection_string_path()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:NntpDB"] = "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz",
                })
                .Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddNntpDbOptions();
            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<NntpDbOptions>>().Value;
            Assert.Equal("Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz", options.ConnectionString);
            Assert.True(new NntpDbOptionsValidator().Validate(null, options).Succeeded);
        }

        /// <summary>
        /// Contract: Bind uses server id not legacy id key.
        /// </summary>

        [Fact]
        public void Bind_uses_server_id_not_legacy_id_key()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BackFiller:Id"] = "7",
                    ["BackFiller:ServerId"] = "3",
                })
                .Build();
            var options = new BackFillerOptions();
            configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
            Assert.Equal(3, options.ServerId);
        }

        /// <summary>
        /// Valid baseline configuration passes validation.
        /// </summary>

        [Fact]
        public void Validate_ignores_syslog_and_rabbit_fields_while_those_targets_are_disabled()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.Logging.Syslog.Protocol = "Tls";
            options.Logging.Syslog.Port = 0;
            options.Logging.Syslog.Host = "";
            options.Logging.RabbitMq.Exchange = "";
            options.Logging.RabbitMq.RoutingKey = "";
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Succeeded);
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Fact]
        public void Validate_rejects_enabled_syslog_without_a_host()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.Logging.Syslog.Enabled = true;
            options.Logging.Syslog.Host = " ";
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.Contains(result.Failures!, static failure => failure.Contains("Syslog:Host", StringComparison.Ordinal));
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Theory]
        [InlineData(0)]
        [InlineData(65536)]
        public void Validate_rejects_enabled_syslog_port_outside_1_to_65535(int port)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.Logging.Syslog.Enabled = true;
            options.Logging.Syslog.Host = "127.0.0.1";
            options.Logging.Syslog.Port = port;
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.Contains(result.Failures!, static failure => failure.Contains("Syslog:Port", StringComparison.Ordinal));
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Theory]
        [InlineData("Tls")]
        [InlineData("")]
        public void Validate_rejects_enabled_syslog_protocol_other_than_udp_or_tcp(string protocol)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.Logging.Syslog.Enabled = true;
            options.Logging.Syslog.Host = "127.0.0.1";
            options.Logging.Syslog.Protocol = protocol;
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.Contains(result.Failures!, static failure => failure.Contains("Syslog:Protocol", StringComparison.Ordinal));
        }

        /// <summary>
        /// Valid baseline configuration passes validation.
        /// </summary>

        [Theory]
        [InlineData("Udp")]
        [InlineData("tcp")]
        public void Validate_accepts_enabled_syslog_udp_and_tcp(string protocol)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.Logging.Syslog.Enabled = true;
            options.Logging.Syslog.Host = "127.0.0.1";
            options.Logging.Syslog.Protocol = protocol;
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Succeeded);
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Fact]
        public void Validate_requires_rabbit_exchange_and_routing_key_only_when_enabled()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.Logging.RabbitMq.Enabled = true;
            options.Logging.RabbitMq.Exchange = " ";
            options.Logging.RabbitMq.RoutingKey = "";
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.Contains(result.Failures!, static failure => failure.Contains("RabbitMQ:Exchange", StringComparison.Ordinal));
            Assert.Contains(result.Failures!, static failure => failure.Contains("RabbitMQ:RoutingKey", StringComparison.Ordinal));
        }

        /// <summary>
        /// Valid baseline configuration passes validation.
        /// </summary>

        [Fact]
        public void Validate_accepts_empty_file_log_directory_when_file_logging_is_disabled()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.Logging.File.Enabled = false;
            options.Logging.File.LogDir = " ";
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Succeeded);
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Fact]
        public void Validate_rejects_empty_file_log_directory_when_file_logging_is_enabled()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.Logging.File.Enabled = true;
            options.Logging.File.LogDir = "";
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.Contains(result.Failures!, static failure => failure.Contains("File:LogDir", StringComparison.Ordinal));
        }

        /// <summary>
        /// BackFillerOptionsValidator enforces the documented constraint.
        /// </summary>

        [Fact]
        public void Validate_defaults_file_logging_to_enabled_with_logs()
        {
            var options = new BackFillerOptions();
            Assert.True(options.Logging.File.Enabled);
            Assert.Equal("logs", options.Logging.File.LogDir);
            Assert.False(options.Logging.RabbitMq.Enabled);
        }
    }
}
