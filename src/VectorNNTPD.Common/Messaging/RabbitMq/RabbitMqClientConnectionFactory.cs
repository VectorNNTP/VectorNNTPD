using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using RabbitMQ.Client;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>
    /// Maps validated RabbitMQ options onto RabbitMQ.Client and opens one broker connection.
    /// </summary>
    /// <remarks>
    /// This type does not own the connection after <see cref="ConnectAsync"/> returns.
    /// Client automatic recovery and topology recovery stay disabled so
    /// <see cref="RabbitMqService"/> remains the only lifecycle owner.
    /// When TLS is enabled, each configured host is dialed as an <see cref="AmqpTcpEndpoint"/> with its own
    /// <see cref="SslOption.ServerName"/> so failover does not reuse <c>Hosts[0]</c> as SNI.
    /// </remarks>
    internal sealed class RabbitMqClientConnectionFactory : IRabbitMqConnectionFactory
    {
        /// <summary>Hostname used when the operating system does not report one.</summary>
        internal const string UnknownHostName = "unknown-host";

        /// <summary>Passed into each <see cref="RabbitMqClientConnection"/> for close-failure logs. Not used to log credentials.</summary>
        private readonly ILogger<RabbitMqClientConnectionFactory> _logger;

        /// <summary>
        /// Creates a factory that passes <paramref name="logger"/> into each opened <see cref="RabbitMqClientConnection"/>.
        /// </summary>
        /// <param name="logger">Logger used when that connection's close fails. This factory does not log credentials.</param>
        public RabbitMqClientConnectionFactory(ILogger<RabbitMqClientConnectionFactory> logger)
        {
            ArgumentNullException.ThrowIfNull(logger);
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<IRabbitMqConnection> ConnectAsync(
            RabbitMqOptions options,
            string connectionName,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

            var runtime = options.ToRuntimeOptions();
            var factory = CreateClientFactory(runtime, connectionName);
            // Per-endpoint SslOption.ServerName: string-host CreateConnectionAsync shares one factory Ssl
            // across endpoints, so failover would reuse Hosts[0] as SNI for every dial.
            var endpoints = CreateAmqpTcpEndpoints(runtime);
            var connection = await factory.CreateConnectionAsync(endpoints, cancellationToken).ConfigureAwait(false);
            IAsyncDisposable? pendingDispose = connection;
            try
            {
                var wrapped = new RabbitMqClientConnection(connection, runtime.VirtualHost, _logger);
                pendingDispose = null;
                return wrapped;
            }
            finally
            {
                if (pendingDispose is not null)
                {
                    await pendingDispose.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Builds a RabbitMQ.Client factory with application-managed recovery disabled.
        /// </summary>
        /// <remarks>
        /// When TLS is enabled, <see cref="ConnectionFactory.Ssl"/> carries protocol defaults only.
        /// Per-host SNI and certificate identity come from <see cref="CreateAmqpTcpEndpoints"/>;
        /// do not set a shared <see cref="SslOption.ServerName"/> on this factory.
        /// </remarks>
        internal static ConnectionFactory CreateClientFactory(
            RabbitMqRuntimeOptions options,
            string connectionName)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

            var factory = new ConnectionFactory
            {
                Port = options.Port,
                VirtualHost = options.VirtualHost,
                ClientProvidedName = connectionName,
                AutomaticRecoveryEnabled = false,
                TopologyRecoveryEnabled = false,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(options.NetworkRecoveryIntervalSeconds),
                RequestedHeartbeat = TimeSpan.FromSeconds(options.RequestedHeartbeatSeconds),
                // Handshake/dial timeout is independent of ConnectionBlockedTimeoutSeconds, which
                // RabbitMqService uses as the maximum continuous broker-blocked duration.
                RequestedConnectionTimeout = TimeSpan.FromSeconds(options.SocketTimeoutSeconds),
                ContinuationTimeout = TimeSpan.FromSeconds(options.RpcTimeoutSeconds),
                HandshakeContinuationTimeout = TimeSpan.FromSeconds(options.RpcTimeoutSeconds),
                SocketReadTimeout = TimeSpan.FromSeconds(options.SocketTimeoutSeconds),
                SocketWriteTimeout = TimeSpan.FromSeconds(options.SocketTimeoutSeconds),
                RequestedChannelMax = (ushort)options.RequestedChannelMax,
            };

            if (!string.IsNullOrWhiteSpace(options.Username))
            {
                factory.UserName = options.Username;
            }

            if (!string.IsNullOrWhiteSpace(options.Password))
            {
                factory.Password = options.Password;
            }

            factory.Ssl.Enabled = options.EnableSsl;
            if (options.EnableSsl)
            {
                factory.Ssl.Version = SslProtocols.Tls12 | SslProtocols.Tls13;
            }

            PopulateClientProperties(factory);
            return factory;
        }

        /// <summary>
        /// Builds one <see cref="AmqpTcpEndpoint"/> per configured host, with an independent TLS identity when SSL is enabled.
        /// </summary>
        /// <param name="options">Validated runtime options whose <see cref="RabbitMqRuntimeOptions.Hosts"/> are dialed in library order.</param>
        /// <returns>Endpoints whose TLS <see cref="SslOption.ServerName"/> matches the dialed hostname (never a shared factory name).</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
        /// <remarks>
        /// Each TLS-enabled endpoint owns a distinct <see cref="SslOption"/> instance so concurrent or sequential
        /// dials cannot race on <see cref="SslOption.ServerName"/>. The configured hostname is used for SNI and
        /// certificate validation; ports are not included. When SSL is disabled, endpoints use the library default
        /// (disabled) SSL options.
        /// </remarks>
        internal static IReadOnlyList<AmqpTcpEndpoint> CreateAmqpTcpEndpoints(RabbitMqRuntimeOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var endpoints = new AmqpTcpEndpoint[options.Hosts.Count];
            for (var i = 0; i < options.Hosts.Count; i++)
            {
                var host = options.Hosts[i];
                if (options.EnableSsl)
                {
                    // New SslOption per host: AmqpTcpEndpoint stores the reference; sharing would reintroduce
                    // the Hosts[0] SNI bug under failover and any concurrent dial of the same factory list.
                    var ssl = new SslOption
                    {
                        Enabled = true,
                        Version = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ServerName = host,
                    };
                    endpoints[i] = new AmqpTcpEndpoint(host, options.Port, ssl);
                }
                else
                {
                    endpoints[i] = new AmqpTcpEndpoint(host, options.Port);
                }
            }

            return endpoints;
        }

        /// <summary>
        /// Resolves a hostname, using <see cref="UnknownHostName"/> when the reported name is missing.
        /// </summary>
        /// <param name="readMachineName">Reads the operating-system hostname.</param>
        /// <returns>A non-empty hostname.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="readMachineName"/> is null.</exception>
        internal static string ResolveClientMachineName(Func<string?> readMachineName)
        {
            ArgumentNullException.ThrowIfNull(readMachineName);

            string? name;
            try
            {
                name = readMachineName();
            }
            catch (InvalidOperationException)
            {
                name = null;
            }

            return string.IsNullOrWhiteSpace(name) ? UnknownHostName : name;
        }

        /// <summary>
        /// Writes diagnostic AMQP client properties for the RabbitMQ management UI.
        /// </summary>
        /// <param name="factory">Factory whose <see cref="ConnectionFactory.ClientProperties"/> are updated.</param>
        /// <remarks>
        /// Library defaults <c>product</c>, <c>version</c>, <c>copyright</c>, and <c>information</c> stay in place.
        /// <c>platform</c> is replaced with <see cref="Environment.OSVersion"/>.
        /// The application name and compiled version come from <see cref="Assembly.GetEntryAssembly"/>.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The entry assembly has no short name.</exception>
        private static void PopulateClientProperties(ConnectionFactory factory)
        {
            var applicationName = ApplicationJsonConfiguration.EntryAssemblyName;
            var machineName = ResolveClientMachineName(static () => Environment.MachineName);
            var processId = Environment.ProcessId;
            factory.ClientProperties["application"] =
                $"{applicationName}-{machineName}-{processId}".ToUpperInvariant();
            factory.ClientProperties["application_version"] = ResolveEntryApplicationVersion();
            factory.ClientProperties["connected_at"] =
                DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            factory.ClientProperties["machine"] = machineName;
            factory.ClientProperties["os_version"] = Environment.OSVersion.VersionString;
            factory.ClientProperties["platform"] = Environment.OSVersion.Platform.ToString();
            factory.ClientProperties["process_id"] = processId.ToString(CultureInfo.InvariantCulture);
            factory.ClientProperties["runtime"] = RuntimeInformation.FrameworkDescription;
        }

        /// <summary>Returns the entry assembly's compiled version.</summary>
        /// <returns>
        /// <see cref="Version.ToString()"/> for the entry assembly, or <c>0.0.0</c> when that version is absent.
        /// </returns>
        private static string ResolveEntryApplicationVersion()
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            return version?.ToString() ?? "0.0.0";
        }
    }
}
