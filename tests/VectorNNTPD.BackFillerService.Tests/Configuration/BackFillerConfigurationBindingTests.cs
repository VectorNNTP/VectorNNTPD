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
    /// Proves VECTOR and BackFiller configuration paths bind only canonical keys and reject obsolete prefixes.
    /// </summary>

    public sealed class BackFillerConfigurationBindingTests
    {
        /// <summary>
        /// Contract: Shared acme cloudflare environment variables are vector names.
        /// </summary>

        [Fact]
        public void Shared_acme_cloudflare_environment_variables_are_vector_names()
        {
            Assert.Equal("CLOUDFLAREAPIKEY", StripVectorPrefix("VECTOR__CLOUDFLAREAPIKEY"));
            Assert.Equal("ACMECERTIFICATEPASSWORD", StripVectorPrefix("VECTOR__ACMECERTIFICATEPASSWORD"));
            Assert.Equal("CLOUDFLAREZONEID", StripVectorPrefix("VECTOR__CLOUDFLAREZONEID"));
            Assert.Equal("BINDPORT", StripVectorPrefix("VECTOR__BINDPORT"));

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CloudFlareApiKey"] = BackFillerTestOptions.SecretToken,
                    ["AcmeCertificatePassword"] = BackFillerTestOptions.SecretPfx,
                    ["CloudFlareZoneId"] = "0123456789abcdef0123456789abcdef",
                })
                .Build();

            var options = new AcmeCloudflareOptions();
            configuration.Bind(options);
            Assert.Equal(BackFillerTestOptions.SecretToken, options.CloudFlareApiKey);
            Assert.Equal(BackFillerTestOptions.SecretPfx, options.AcmeCertificatePassword);
            Assert.Equal("0123456789abcdef0123456789abcdef", options.CloudFlareZoneId);
        }

        /// <summary>
        /// Contract: Canonical environment variable names map to root configuration paths.
        /// </summary>

        [Fact]
        public void Canonical_environment_variable_names_map_to_root_configuration_paths()
        {
            Assert.Equal(VectorEnvironment.Prefix, BackFillerOptions.EnvironmentVariablePrefix);
            Assert.Equal("BackFiller", BackFillerOptions.SectionName);
            Assert.Equal("NntpDB", NntpDbOptions.ConnectionStringName);
            Assert.Equal("ConnectionStrings__NntpDB", NntpDbOptions.ConnectionStringEnvironmentVariable);
            Assert.Null(typeof(BackFillerOptions).GetField(
                "GrabberDbEnvironmentVariable",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));

            Assert.Null(typeof(BackFillerOptions).GetField(
                "NameEnvironmentVariable",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
            Assert.Null(typeof(BackFillerOptions).GetProperty("Name"));
            Assert.Equal("RABBITMQ:USERNAME", ToConfigurationPath(BackFillerOptions.RabbitMqUsernameEnvironmentVariable));
            Assert.Equal("RABBITMQ:PASSWORD", ToConfigurationPath(BackFillerOptions.RabbitMqPasswordEnvironmentVariable));
            Assert.True(VectorEnvironment.IsCanonicalName(BackFillerOptions.RabbitMqUsernameEnvironmentVariable));
        }

        /// <summary>
        /// Contract: Bind populates options from canonical prefixed paths.
        /// </summary>

        [Fact]
        public void Bind_populates_options_from_canonical_prefixed_paths()
        {
            var configuration = new ConfigurationBuilder()
                .AddConfiguration(ConfigurationFromEnvironmentVariables(
                    (BackFillerOptions.RabbitMqUsernameEnvironmentVariable, "canonical-user"),
                    (BackFillerOptions.RabbitMqPasswordEnvironmentVariable, BackFillerTestOptions.SecretPassword),
                    (AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable, BackFillerTestOptions.SecretToken),
                    (AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable, BackFillerTestOptions.SecretPfx),
                    (NntpDbOptions.ConnectionStringEnvironmentVariable, "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz")))
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BackFiller:ServerId"] = "8",
                    ["BackFiller:DnsSuffix"] = "usenet.ninja",
                })
                .Build();

            var options = new BackFillerOptions();
            configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
            var rabbit = new VectorNNTPD.Common.Messaging.RabbitMq.RabbitMqOptions();
            configuration.GetSection("RabbitMQ").Bind(rabbit);
            var acme = new AcmeCloudflareOptions();
            configuration.Bind(acme);
            var nntpDb = BindNntpDb(configuration);

            Assert.Equal(8, options.ServerId);
            Assert.Equal("backfiller08.usenet.ninja", options.Fqdn);
            Assert.Equal(
                ApplicationFqdn.Build(BackFillerOptions.ApplicationPrefix, 8, "usenet.ninja"),
                options.Fqdn);
            Assert.Equal("canonical-user", rabbit.Username);
            Assert.Equal(BackFillerTestOptions.SecretPassword, rabbit.Password);
            Assert.Equal(BackFillerTestOptions.SecretToken, acme.CloudFlareApiKey);
            Assert.Equal(BackFillerTestOptions.SecretPfx, acme.AcmeCertificatePassword);
            Assert.Equal("Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz", nntpDb.ConnectionString);
        }

        /// <summary>
        /// Contract: Bind does not accept obsolete application prefixes.
        /// </summary>

        [Theory]
        [InlineData("nntpd__RabbitMQ__Username", "nntpd__RabbitMQ__Password")]
        [InlineData("backfiller__RabbitMQ__Username", "backfiller__RabbitMQ__Password")]
        [InlineData("backfiller__LetsEncrypt__CloudFlareApiToken", "backfiller__LetsEncrypt__PfxExportPassword")]
        public void Bind_does_not_accept_obsolete_application_prefixes(string usernameOrToken, string passwordOrPfx)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [usernameOrToken.Replace("__", ":", StringComparison.Ordinal)] = "short-form-user-or-token",
                    [passwordOrPfx.Replace("__", ":", StringComparison.Ordinal)] = BackFillerTestOptions.SecretPassword,
                })
                .Build();

            var options = new BackFillerOptions();
            configuration.Bind(options);
            var acme = new AcmeCloudflareOptions();
            configuration.Bind(acme);

            Assert.Null(typeof(BackFillerOptions).GetProperty("RabbitMQ"));
            Assert.True(string.IsNullOrWhiteSpace(acme.CloudFlareApiKey));
            Assert.True(string.IsNullOrWhiteSpace(acme.AcmeCertificatePassword));
        }

        /// <summary>
        /// Contract: Bind does not map legacy id key to server id.
        /// </summary>

        [Fact]
        public void Bind_does_not_map_legacy_id_key_to_server_id()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BackFiller:Id"] = "7",
                })
                .Build();

            var options = new BackFillerOptions();
            configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
            Assert.Null(options.ServerId);
        }

        /// <summary>
        /// Validation failure messages must not echo configured secrets.
        /// </summary>

        [Fact]
        public void Nntp_db_validation_failures_do_not_include_the_connection_string()
        {
            var options = new NntpDbOptions
            {
                ConnectionString = "Server=127.0.0.1;Database=nntp;User ID=nntpd;Password=\"db-secret-xyz;",
            };

            var result = new NntpDbOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
            var raw = options.ConnectionString;
            Assert.All(
                result.Failures!,
                failure =>
                {
                    Assert.DoesNotContain("db-secret-xyz", failure, StringComparison.Ordinal);
                    Assert.DoesNotContain(raw, failure, StringComparison.Ordinal);
                });
        }

        /// <summary>
        /// Contract: Prefixed environment variables reach runtime options.
        /// </summary>

        [Fact]
        public void Prefixed_environment_variables_reach_runtime_options()
        {
            using var environment = new IsolatedEnvironment(
                (BackFillerOptions.RabbitMqUsernameEnvironmentVariable, "env-user"),
                (BackFillerOptions.RabbitMqPasswordEnvironmentVariable, BackFillerTestOptions.SecretPassword),
                (AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable, BackFillerTestOptions.SecretToken),
                (AcmeCloudflareOptions.CloudFlareZoneIdEnvironmentVariable, "0123456789abcdef0123456789abcdef"),
                (AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable, BackFillerTestOptions.SecretPfx),
                (NntpDbOptions.ConnectionStringEnvironmentVariable, "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz"));

            var configuration = new ConfigurationBuilder()
                .AddConfiguration(environment.BuildHostConfiguration())
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BackFiller:ServerId"] = "8",
                })
                .Build();
            var options = new BackFillerOptions();
            configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
            var rabbit = new VectorNNTPD.Common.Messaging.RabbitMq.RabbitMqOptions();
            configuration.GetSection("RabbitMQ").Bind(rabbit);
            var acme = new AcmeCloudflareOptions();
            configuration.Bind(acme);
            var nntpDb = BindNntpDb(configuration);

            Assert.Equal(8, options.ServerId);
            Assert.Equal("env-user", rabbit.Username);
            Assert.Equal(BackFillerTestOptions.SecretPassword, rabbit.Password);
            Assert.Equal(BackFillerTestOptions.SecretToken, acme.CloudFlareApiKey);
            Assert.Equal(BackFillerTestOptions.SecretPfx, acme.AcmeCertificatePassword);
            Assert.Equal("Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz", nntpDb.ConnectionString);
        }

        /// <summary>
        /// Valid baseline configuration passes validation.
        /// </summary>

        [Fact]
        public void Leftover_name_environment_variable_does_not_bind_or_change_fqdn()
        {
            using var environment = new IsolatedEnvironment(
                ("BACKFILLER__NAME", "cache"));

            var configuration = environment.BuildHostConfiguration();
            var options = BackFillerTestOptions.CreateValid();
            configuration.GetSection(BackFillerOptions.SectionName).Bind(options);

            Assert.Null(typeof(BackFillerOptions).GetProperty("Name"));
            Assert.Equal("backfiller01.usenet.ninja", options.Fqdn);
            var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(result.Succeeded);
        }

        /// <summary>
        /// The operation throws InvalidOperationException with the expected contract.
        /// </summary>

        [Fact]
        public void Malformed_server_id_from_the_section_fails_at_bind()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BackFiller:ServerId"] = "not-an-integer",
                })
                .Build();
            var options = BackFillerTestOptions.CreateValid();
            var ex = Assert.Throws<InvalidOperationException>(
                () => configuration.GetSection(BackFillerOptions.SectionName).Bind(options));
            Assert.Contains("ServerId", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(BackFillerTestOptions.SecretPassword, ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Contract: Obsolete prefixes do not bind through the vector provider.
        /// </summary>

        [Fact]
        public void Obsolete_prefixes_do_not_bind_through_the_vector_provider()
        {
            using var environment = new IsolatedEnvironment(
                ("nntpd__RabbitMQ__Username", "obsolete-user"),
                ("backfiller__RabbitMQ__Password", BackFillerTestOptions.SecretPassword));

            var configuration = environment.BuildPrefixedConfiguration();
            Assert.NotEqual("obsolete-user", configuration["RabbitMQ:Username"]);
            Assert.NotEqual(BackFillerTestOptions.SecretPassword, configuration["RabbitMQ:Password"]);
            Assert.NotEqual("obsolete-user", configuration["BackFiller:RabbitMQ:Username"]);
            Assert.NotEqual(BackFillerTestOptions.SecretPassword, configuration["BackFiller:RabbitMQ:Password"]);
        }

        /// <summary>
        /// Contract: Vector prefix does not bind application identity.
        /// </summary>

        [Fact]
        public void Vector_prefix_does_not_bind_application_identity()
        {
            using var environment = new IsolatedEnvironment(
                ("VECTOR__NAME", "vector-must-not-bind"),
                ("VECTOR__SERVERID", "77"));

            var configuration = environment.BuildHostConfiguration();
            var options = new BackFillerOptions();
            configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
            Assert.Null(typeof(BackFillerOptions).GetProperty("Name"));
            Assert.Null(options.ServerId);
        }

        /// <summary>Builds in-memory configuration from canonical environment variable names and values.</summary>
        /// <param name="variables">Environment variable name/value pairs to map into configuration keys.</param>
        private static IConfiguration ConfigurationFromEnvironmentVariables(params (string Name, string Value)[] variables)
        {
            var pairs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in variables)
            {
                pairs[ToAnyConfigurationPath(name)] = value;
            }

            return new ConfigurationBuilder()
                .AddInMemoryCollection(pairs)
                .Build();
        }

        /// <summary>Maps a VECTOR-prefixed environment variable to a colon-separated configuration path.</summary>
        /// <param name="environmentVariable">Canonical VECTOR name (asserted before mapping).</param>
        private static string ToConfigurationPath(string environmentVariable)
        {
            Assert.True(VectorEnvironment.IsCanonicalName(environmentVariable));
            return environmentVariable[VectorEnvironment.Prefix.Length..]
                .Replace("__", ":", StringComparison.Ordinal);
        }

        /// <summary>Maps a BACKFILLER__ variable to a BackFiller: configuration path for negative binding tests.</summary>
        /// <param name="environmentVariable">Must start with BACKFILLER__.</param>
        private static string ToApplicationConfigurationPath(string environmentVariable)
        {
            Assert.StartsWith("BACKFILLER__", environmentVariable, StringComparison.Ordinal);
            var remainder = environmentVariable["BACKFILLER__".Length..];
            return "BackFiller:" + remainder;
        }

        /// <summary>Resolves <see cref="NntpDbOptions"/> the same way the host registers them.</summary>
        /// <param name="configuration">Configuration root containing ConnectionStrings:NntpDB.</param>
        private static NntpDbOptions BindNntpDb(IConfiguration configuration)
        {
            var services = new ServiceCollection();
            services.AddSingleton(configuration);
            services.AddNntpDbOptions();
            using var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IOptions<NntpDbOptions>>().Value;
        }

        /// <summary>Maps VECTOR, ConnectionStrings__NntpDB, or BACKFILLER__ names into configuration keys.</summary>
        /// <param name="environmentVariable">Source environment variable name.</param>
        private static string ToAnyConfigurationPath(string environmentVariable)
        {
            if (VectorEnvironment.IsCanonicalName(environmentVariable))
            {
                return ToConfigurationPath(environmentVariable);
            }

            if (string.Equals(
                    environmentVariable,
                    NntpDbOptions.ConnectionStringEnvironmentVariable,
                    StringComparison.Ordinal))
            {
                return $"ConnectionStrings:{NntpDbOptions.ConnectionStringName}";
            }

            return ToApplicationConfigurationPath(environmentVariable);
        }

        /// <summary>Strips the VECTOR prefix and returns the configuration path used by binding tests.</summary>
        /// <param name="environmentVariable">Canonical VECTOR environment variable.</param>
        private static string StripVectorPrefix(string environmentVariable) =>
            ToConfigurationPath(environmentVariable);

        /// <summary>
        /// Temporarily sets process environment variables and restores previous values on dispose.
        /// </summary>
        private sealed class IsolatedEnvironment : IDisposable
        {
            /// <summary>Environment values restored on dispose.</summary>
            private readonly (string Name, string? Previous)[] _previous;

            /// <summary>Captures prior values and applies <paramref name="variables"/> to the process environment.</summary>
            /// <param name="variables">Variables to set for the duration of the test.</param>
            internal IsolatedEnvironment(params (string Name, string Value)[] variables)
            {
                _previous = variables
                    .Select(static pair => (pair.Name, Environment.GetEnvironmentVariable(pair.Name)))
                    .ToArray();
                foreach (var (name, value) in variables)
                {
                    Environment.SetEnvironmentVariable(name, value);
                }
            }

            /// <summary>Configuration with only <see cref="VectorEnvironment"/> variables loaded.</summary>
            internal IConfiguration BuildPrefixedConfiguration() =>
                VectorEnvironment.AddVectorEnvironmentVariables(new ConfigurationBuilder()).Build();

            /// <summary>Configuration matching host order: environment variables then VECTOR overlay.</summary>
            internal IConfiguration BuildHostConfiguration() =>
                VectorEnvironment.AddVectorEnvironmentVariables(
                    new ConfigurationBuilder().AddEnvironmentVariables())
                    .Build();

            /// <summary>Restores each environment variable to its captured previous value.</summary>
            public void Dispose()
            {
                foreach (var (name, previous) in _previous)
                {
                    Environment.SetEnvironmentVariable(name, previous);
                }
            }
        }
    }
}
