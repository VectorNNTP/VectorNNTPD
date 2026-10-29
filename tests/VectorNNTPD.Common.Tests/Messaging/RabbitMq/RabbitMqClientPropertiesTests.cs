using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.Common.Tests.Messaging.RabbitMq
{
    /// <summary>
    /// Locks RabbitMQ client properties, connection naming, and hostname fallback without placing credentials in those properties.
    /// </summary>
    public sealed class RabbitMqClientPropertiesTests
    {
        /// <summary>
        /// Asserts that the client factory stamps application, machine, process, runtime, and connection-time properties from the entry assembly, keeps the library product properties, disables automatic and topology recovery, and does not copy the username or password into any string property.
        /// </summary>
        [Fact]
        public void CreateClientFactory_SetsReferenceClientPropertiesFromEntryAssembly()
        {
            var before = DateTimeOffset.UtcNow;
            var runtime = RabbitMqOptionsValidatorTests.CreateValidConnectivityOnly();
            runtime.Username = "nntparticles";
            runtime.Password = "unit-test-rabbitmq-password-not-real";
            var options = runtime.ToRuntimeOptions();
            const string connectionName = "VectorNNTP.NNTPD:nntpd01.usenet.ninja";
            var libraryVersion = new RabbitMQ.Client.ConnectionFactory().ClientProperties["version"];

            var factory = RabbitMqClientConnectionFactory.CreateClientFactory(options, connectionName);
            var after = DateTimeOffset.UtcNow;
            var properties = factory.ClientProperties;

            var entry = Assembly.GetEntryAssembly();
            Assert.NotNull(entry);
            var entryName = entry.GetName().Name;
            Assert.False(string.IsNullOrWhiteSpace(entryName));
            var machine = RabbitMqClientConnectionFactory.ResolveClientMachineName(static () => Environment.MachineName);
            var processId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            var application = RequireString(properties, "application");

            Assert.Equal($"{entryName}-{machine}-{processId}".ToUpperInvariant(), application);
            Assert.Equal(entry.GetName().Version?.ToString() ?? "0.0.0", RequireString(properties, "application_version"));
            Assert.Equal(machine, RequireString(properties, "machine"));
            Assert.Equal(Environment.OSVersion.VersionString, RequireString(properties, "os_version"));
            Assert.Equal(Environment.OSVersion.Platform.ToString(), RequireString(properties, "platform"));
            Assert.Equal(processId, RequireString(properties, "process_id"));
            Assert.Equal(RuntimeInformation.FrameworkDescription, RequireString(properties, "runtime"));
            Assert.DoesNotContain("TASKEXECUTIONER", application, StringComparison.Ordinal);
            Assert.DoesNotContain("+", RequireString(properties, "application_version"), StringComparison.Ordinal);

            var connectedAt = DateTimeOffset.ParseExact(
                RequireString(properties, "connected_at"),
                "o",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            Assert.Equal(TimeSpan.Zero, connectedAt.Offset);
            Assert.InRange(connectedAt, before.AddSeconds(-1), after.AddSeconds(1));

            Assert.Equal(libraryVersion, properties["version"]);
            Assert.True(properties.ContainsKey("product"));
            Assert.True(properties.ContainsKey("copyright"));
            Assert.True(properties.ContainsKey("information"));
            Assert.Equal(connectionName, factory.ClientProvidedName);
            Assert.False(factory.AutomaticRecoveryEnabled);
            Assert.False(factory.TopologyRecoveryEnabled);

            foreach (var pair in properties)
            {
                var text = pair.Value as string;
                if (text is null)
                {
                    continue;
                }

                Assert.DoesNotContain("nntparticles", text, StringComparison.Ordinal);
                Assert.DoesNotContain("unit-test-rabbitmq-password-not-real", text, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Asserts that a null, empty, or whitespace machine name becomes <see cref="RabbitMqClientConnectionFactory.UnknownHostName"/>.
        /// </summary>
        /// <param name="reported">Machine name returned by the injected reader.</param>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ResolveClientMachineName_UsesUnknownHostWhenMissing(string? reported)
        {
            Assert.Equal(
                RabbitMqClientConnectionFactory.UnknownHostName,
                RabbitMqClientConnectionFactory.ResolveClientMachineName(() => reported));
        }

        /// <summary>
        /// Asserts that an <see cref="InvalidOperationException"/> from the machine-name reader becomes <see cref="RabbitMqClientConnectionFactory.UnknownHostName"/>.
        /// </summary>
        [Fact]
        public void ResolveClientMachineName_UsesUnknownHostWhenTheOperatingSystemThrows()
        {
            Assert.Equal(
                RabbitMqClientConnectionFactory.UnknownHostName,
                RabbitMqClientConnectionFactory.ResolveClientMachineName(static () =>
                    throw new InvalidOperationException("hostname unavailable")));
        }

        /// <summary>
        /// Asserts that a non-blank reported host is returned unchanged.
        /// </summary>
        [Fact]
        public void ResolveClientMachineName_PreservesReportedHost()
        {
            Assert.Equal(
                "cache01",
                RabbitMqClientConnectionFactory.ResolveClientMachineName(static () => "cache01"));
        }

        /// <summary>
        /// Asserts that <paramref name="key"/> is present and that its value is a string.
        /// </summary>
        /// <param name="properties">Client property map under test.</param>
        /// <param name="key">Property name that must exist.</param>
        /// <returns>The string value stored at <paramref name="key"/>.</returns>
        private static string RequireString(IDictionary<string, object?> properties, string key)
        {
            Assert.True(properties.TryGetValue(key, out var value));
            return Assert.IsType<string>(value);
        }
    }
}
