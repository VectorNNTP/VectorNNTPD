using VectorNNTPD.BackFillerService.Tests.Stress;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// Opt-in controls for lab SilentConsumerMonitor runtime validation against the shared lab broker.
    /// Disabled unless <see cref="EnableEnvironmentVariable"/> is exactly <c>1</c>.
    /// </summary>
    internal static class SilentConsumerLabOptions
    {
        /// <summary>Master gate. Must be <c>1</c>; any other value (including unset) keeps tests no-ops.</summary>
        internal const string EnableEnvironmentVariable = "VECTORNNTPD_SILENT_CONSUMER_LAB";

        /// <summary>Optional RabbitMQ host override (defaults to stress/lab host <c>198.18.0.3</c>).</summary>
        internal const string RabbitHostEnvironmentVariable = "VECTORNNTPD_BACKFILLER_E2E_STRESS_RABBIT_HOST";

        /// <summary>Returns whether lab runtime scenarios should execute.</summary>
        internal static bool IsEnabled() =>
            string.Equals(
                Environment.GetEnvironmentVariable(EnableEnvironmentVariable),
                "1",
                StringComparison.Ordinal);

        /// <summary>
        /// Builds stress-compatible broker options from lab credentials without enabling the E2E stress master flag.
        /// </summary>
        /// <returns>Options suitable for <see cref="ArticleWorkStressClient"/> and owned-message cleanup.</summary>
        /// <exception cref="InvalidOperationException">Thrown when RabbitMQ credentials are missing.</exception>
        internal static StressHarnessOptions CreateStressBrokerOptions()
        {
            var username = Environment.GetEnvironmentVariable("VECTOR__RABBITMQ__USERNAME");
            var password = Environment.GetEnvironmentVariable("VECTOR__RABBITMQ__PASSWORD");
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException(
                    "VECTOR__RABBITMQ__USERNAME and VECTOR__RABBITMQ__PASSWORD are required for SilentConsumer lab runs.");
            }

            var host = Environment.GetEnvironmentVariable(RabbitHostEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(host))
            {
                host = "198.18.0.3";
            }

            return new StressHarnessOptions
            {
                RabbitMqHost = host.Trim(),
                RabbitMqPort = 5672,
                RabbitMqVirtualHost = "/",
                RabbitMqUsername = username,
                RabbitMqPassword = password,
                WorkReplyTimeout = TimeSpan.FromMinutes(5),
                ReplyQueueDisposeDrainTimeout = TimeSpan.FromSeconds(30),
            };
        }

        /// <summary>
        /// Live <see cref="RabbitMqOptions"/> for <see cref="RabbitMqService"/> / Management client (secrets never logged).
        /// </summary>
        /// <param name="stress">Broker identity and credentials.</param>
        /// <returns>Mutable options instance (BaseUrl can be cleared for Management-outage inject).</returns>
        internal static RabbitMqOptions CreateRabbitMqOptions(StressHarnessOptions stress)
        {
            ArgumentNullException.ThrowIfNull(stress);
            return new RabbitMqOptions
            {
                Hosts = [stress.RabbitMqHost],
                Port = stress.RabbitMqPort,
                VirtualHost = stress.RabbitMqVirtualHost,
                Username = stress.RabbitMqUsername,
                Password = stress.RabbitMqPassword,
                EnableSsl = false,
                MinConnections = 1,
                MaxConnections = 4,
                ChannelPoolSize = 64,
                PoolReconnectBaseDelayMs = 50,
                PoolReconnectMaxDelayMs = 250,
                Management = new RabbitMqManagementOptions
                {
                    BaseUrl = $"http://{stress.RabbitMqHost}:15672",
                    RequestTimeoutSeconds = 5,
                },
            };
        }
    }
}
