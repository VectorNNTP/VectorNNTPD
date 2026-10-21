using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Hosting;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.Common.Tests.Messaging.RabbitMq
{
    /// <summary>
    /// Locks <see cref="RabbitMqOptions"/> defaults, connectivity-only validation, and the runtime snapshot projection.
    /// </summary>
    public sealed class RabbitMqOptionsValidatorTests
    {
        /// <summary>
        /// Asserts the default payload limit, lease and RPC timeouts, empty host list, root virtual host, enabled SSL, port 5672, and a management section with no base URL.
        /// </summary>
        [Fact]
        public void Defaults_MatchSharedContract()
        {
            var options = new RabbitMqOptions();

            Assert.Equal(1024, options.WorkRequestMaxPayloadBytes);
            Assert.Equal(10, options.ArticleWorkDeliveryLimit);
            Assert.Equal(60, options.ChannelLeaseTimeoutSeconds);
            Assert.Equal(30, options.RpcTimeoutSeconds);
            Assert.Equal(30, options.ConnectionBlockedTimeoutSeconds);
            Assert.Equal(30, options.SocketTimeoutSeconds);
            Assert.Empty(options.Hosts!);
            Assert.Equal("/", options.VirtualHost);
            Assert.True(options.EnableSsl);
            Assert.Equal(5672, options.Port);
            Assert.NotNull(options.Management);
            Assert.Null(options.Management.BaseUrl);
        }

        /// <summary>
        /// ConnectionBlockedTimeoutSeconds below the validated minimum fails startup validation.
        /// </summary>
        [Fact]
        public void Validate_Fails_WhenConnectionBlockedTimeoutBelowMinimum()
        {
            var options = CreateValidConnectivityOnly();
            options.ConnectionBlockedTimeoutSeconds = 4;
            options.RpcTimeoutSeconds = 1;
            var result = new RabbitMqOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                static failure => failure.Contains("ConnectionBlockedTimeoutSeconds", StringComparison.Ordinal)
                    && failure.Contains("between 5 and 3600", StringComparison.Ordinal));
        }

        /// <summary>
        /// ConnectionBlockedTimeoutSeconds must remain at least RpcTimeoutSeconds.
        /// </summary>
        [Fact]
        public void Validate_Fails_WhenConnectionBlockedTimeoutLessThanRpcTimeout()
        {
            var options = CreateValidConnectivityOnly();
            options.RpcTimeoutSeconds = 60;
            options.ConnectionBlockedTimeoutSeconds = 30;
            var result = new RabbitMqOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                static failure => failure.Contains(
                    "ConnectionBlockedTimeoutSeconds must be greater than or equal to RpcTimeoutSeconds",
                    StringComparison.Ordinal));
        }

        /// <summary>
        /// Asserts that connectivity-only options with a host and SSL disabled pass validation when management is unset.
        /// </summary>
        [Fact]
        public void Validate_Succeeds_WithoutManagement()
        {
            var result = new RabbitMqOptionsValidator().Validate(null, CreateValidConnectivityOnly());
            Assert.True(result.Succeeded);
        }

        /// <summary>
        /// More than seven unique hosts fails validation rather than clamping quorum initial group size.
        /// </summary>
        [Fact]
        public void Validate_Fails_WhenHostsExceedQuorumInitialGroupSizeLimit()
        {
            var options = CreateValidConnectivityOnly();
            options.Hosts =
            [
                "127.0.0.1",
                "127.0.0.2",
                "127.0.0.3",
                "127.0.0.4",
                "127.0.0.5",
                "127.0.0.6",
                "127.0.0.7",
                "127.0.0.8",
            ];
            var result = new RabbitMqOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                static failure => failure.Contains("at most 7 unique entries", StringComparison.Ordinal));
        }

        /// <summary>
        /// Topology derivation maps unique host counts to classic or quorum without using live connectivity.
        /// </summary>
        [Theory]
        [InlineData(1, "Classic", null)]
        [InlineData(2, "Quorum", 2)]
        [InlineData(3, "Quorum", 3)]
        [InlineData(7, "Quorum", 7)]
        public void FromConfiguredHosts_derives_topology_from_unique_host_count(
            int hostCount,
            string expectedKindName,
            int? expectedGroupSize)
        {
            var hosts = Enumerable.Range(0, hostCount).Select(static i => $"127.0.0.{i + 1}").ToArray();
            var topology = ArticleWorkQueueTopology.FromConfiguredHosts(hosts);
            Assert.Equal(expectedKindName, topology.Kind.ToString());
            Assert.Equal(hostCount, topology.ConfiguredHostCount);
            Assert.Equal(expectedGroupSize, topology.QuorumInitialGroupSize);
            Assert.Equal(expectedKindName == "Quorum", topology.RequiresQuorumHealth);
        }

        /// <summary>
        /// Empty host lists cannot derive Article Work topology.
        /// </summary>
        [Fact]
        public void FromConfiguredHosts_throws_when_hosts_empty()
        {
            Assert.Throws<InvalidOperationException>(static () => ArticleWorkQueueTopology.FromConfiguredHosts([]));
            Assert.Throws<InvalidOperationException>(static () => ArticleWorkQueueTopology.FromConfiguredHosts(null));
        }

        /// <summary>
        /// Article Work delivery limit outside <c>1–1000</c> fails validation.
        /// </summary>
        [Fact]
        public void Validate_Fails_WhenArticleWorkDeliveryLimitOutOfRange()
        {
            var options = CreateValidConnectivityOnly();
            options.ArticleWorkDeliveryLimit = 1001;
            var result = new RabbitMqOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                static failure => failure.Contains("ArticleWorkDeliveryLimit", StringComparison.Ordinal)
                    && failure.Contains("between 1 and 1000", StringComparison.Ordinal));
        }

        /// <summary>
        /// Asserts that default options fail validation with a failure that mentions <c>Hosts</c>.
        /// </summary>
        [Fact]
        public void Validate_Fails_WhenHostsMissing()
        {
            var result = new RabbitMqOptionsValidator().Validate(null, new RabbitMqOptions());
            Assert.True(result.Failed);
            Assert.Contains(result.Failures, static failure => failure.Contains("Hosts", StringComparison.Ordinal));
        }

        /// <summary>
        /// Asserts that port 0 fails validation.
        /// </summary>
        [Fact]
        public void Validate_Fails_WhenPortOutOfRange()
        {
            var options = CreateValidConnectivityOnly();
            options.Port = 0;
            var result = new RabbitMqOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
        }

        /// <summary>
        /// Asserts that a management base URL with a path fails and that the failure says the URL must not include a path.
        /// </summary>
        [Fact]
        public void Validate_Fails_WhenManagementBaseUrlHasPath()
        {
            var options = CreateValidConnectivityOnly();
            options.Management = new RabbitMqManagementOptions
            {
                BaseUrl = "http://127.0.0.1:15672/api",
                RequestTimeoutSeconds = 5,
            };
            var result = new RabbitMqOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(result.Failures, static failure => failure.Contains("must not include a path", StringComparison.Ordinal));
        }

        /// <summary>
        /// Asserts that the runtime snapshot copies the host, port, virtual host, and disabled SSL from connectivity-only options.
        /// </summary>
        [Fact]
        public void ToRuntimeOptions_ProjectsValidatedSnapshot()
        {
            var runtime = CreateValidConnectivityOnly().ToRuntimeOptions();
            Assert.Equal(["127.0.0.1"], runtime.Hosts);
            Assert.Equal(5672, runtime.Port);
            Assert.Equal("/", runtime.VirtualHost);
            Assert.False(runtime.EnableSsl);
        }

        /// <summary>
        /// Asserts that the default connection name is the application prefix, a colon, and the FQDN.
        /// </summary>
        [Fact]
        public void GetDefaultConnectionName_UsesApplicationPrefix()
        {
            Assert.Equal(
                "VectorNNTP.StorageServer:cache01.usenet.ninja",
                RabbitMqRuntimeOptions.GetDefaultConnectionName("VectorNNTP.StorageServer", "cache01.usenet.ninja"));
        }

        /// <summary>
        /// Returns options with host <c>127.0.0.1</c> and SSL disabled, and with every other field left at the type default.
        /// </summary>
        /// <returns>Options that pass connectivity validation without a management URL.</returns>
        internal static RabbitMqOptions CreateValidConnectivityOnly() =>
            new()
            {
                Hosts = ["127.0.0.1"],
                EnableSsl = false,
            };
    }

    /// <summary>
    /// Locks RabbitMQ infrastructure registration so the singletons exist before any connection is opened.
    /// </summary>
    public sealed class RabbitMqInfrastructureRegistrationTests
    {
        /// <summary>
        /// Asserts that <see cref="RabbitMqServiceCollectionExtensions.AddRabbitMqInfrastructure"/> registers one <see cref="IRabbitMqService"/> implemented by <see cref="RabbitMqService"/> and does not call the connection factory.
        /// </summary>
        /// <returns>A task that completes when the singleton and connect-count assertions finish.</returns>
        [Fact]
        public async Task AddRabbitMqInfrastructure_RegistersSingletons_WithoutConnecting()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Options.Create(RabbitMqOptionsValidatorTests.CreateValidConnectivityOnly()));
            services.AddSingleton<IRabbitMqConnectionNameProvider>(
                new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.Common.Tests:unit"));
            var factory = new CountingFakeRabbitMqConnectionFactory();
            services.AddSingleton<IRabbitMqConnectionFactory>(factory);
            services.AddRabbitMqInfrastructure();

            await using var provider = services.BuildServiceProvider();
            var service = provider.GetRequiredService<IRabbitMqService>();
            Assert.Same(provider.GetRequiredService<RabbitMqService>(), service);
            Assert.Equal(0, factory.ConnectCount);
        }

        /// <summary>
        /// Connection factory that counts connect attempts and then throws so registration tests cannot open a broker connection.
        /// </summary>
        private sealed class CountingFakeRabbitMqConnectionFactory : IRabbitMqConnectionFactory
        {
            /// <summary>Number of <see cref="ConnectAsync"/> calls observed.</summary>
            public int ConnectCount { get; private set; }

            /// <summary>
            /// Increments <see cref="ConnectCount"/> and throws. No connection is returned.
            /// </summary>
            /// <param name="options">Requested options. Ignored.</param>
            /// <param name="connectionName">Requested connection name. Ignored.</param>
            /// <param name="cancellationToken">Cancellation token. Not observed.</param>
            /// <returns>This method does not return.</returns>
            /// <exception cref="InvalidOperationException">Thrown after the call is counted.</exception>
            public Task<IRabbitMqConnection> ConnectAsync(
                RabbitMqOptions options,
                string connectionName,
                CancellationToken cancellationToken)
            {
                ConnectCount++;
                throw new InvalidOperationException("Tests must not connect during registration.");
            }
        }
    }
}
