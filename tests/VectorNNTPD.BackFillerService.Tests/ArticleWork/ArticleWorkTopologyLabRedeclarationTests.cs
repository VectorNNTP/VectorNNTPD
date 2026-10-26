using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork
{
    /// <summary>
    /// Opt-in lab verification that application queue arguments redeclare existing provider queues without PRECONDITION_FAILED.
    /// </summary>
    /// <remarks>
    /// Disabled unless <c>VECTORNNTPD_ARTICLE_WORK_TOPOLOGY_LAB=1</c>. Does not purge, delete, or rewrite queue policy;
    /// it only redeclares with <see cref="BackFillerArticleWorkTopology.BuildQueueArguments"/>.
    /// </remarks>
    public sealed class ArticleWorkTopologyLabRedeclarationTests
    {
        /// <summary>Master gate. Must be <c>1</c>; any other value keeps this test a no-op.</summary>
        internal const string EnableEnvironmentVariable = "VECTORNNTPD_ARTICLE_WORK_TOPOLOGY_LAB";

        /// <summary>
        /// Redeclares <c>backfiller.eweka</c> and <c>backfiller.giganews</c> using runtime-projected application arguments.
        /// </summary>
        /// <remarks>
        /// Uses Hosts and Management from shipped <c>VectorNNTPD.RabbitMQ.json</c> only. Does not honor the
        /// single-host stress RabbitMQ host override, which targets a different classic-queue lab broker.
        /// </remarks>
        [Fact]
        public async Task Lab_redeclares_existing_provider_queues_using_application_arguments()
        {
            if (!IsEnabled())
            {
                return;
            }

            var username = RequireEnv("VECTOR__RABBITMQ__USERNAME");
            var password = RequireEnv("VECTOR__RABBITMQ__PASSWORD");
            var rabbit = BindShippedRabbitMqOptions();
            rabbit.Username = username;
            rabbit.Password = password;

            Assert.NotNull(rabbit.Hosts);
            Assert.True(rabbit.Hosts.Length >= 2, "Lab redeclaration requires multi-host quorum topology from shipped RabbitMQ JSON.");
            Assert.False(string.IsNullOrWhiteSpace(rabbit.Management?.BaseUrl));

            var runtime = BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb(),
                BackFillerTestOptions.CreateValidAcme(),
                rabbit);

            var intended = BackFillerArticleWorkTopology.BuildQueueArguments(runtime.RabbitMq);
            Assert.Equal(BackFillerArticleWorkTopology.QuorumQueueType, intended[BackFillerArticleWorkTopology.QueueTypeArgumentName]);
            Assert.Equal(3L, intended[BackFillerArticleWorkTopology.QuorumInitialGroupSizeArgumentName]);
            Assert.Equal(5L, intended[BackFillerArticleWorkTopology.DeliveryLimitArgumentName]);
            Assert.Equal(10000L, intended[BackFillerArticleWorkTopology.MaxLengthArgumentName]);
            Assert.Equal(60000L, intended[BackFillerArticleWorkTopology.MessageTtlArgumentName]);
            Assert.All(
                new[]
                {
                    BackFillerArticleWorkTopology.QuorumInitialGroupSizeArgumentName,
                    BackFillerArticleWorkTopology.DeliveryLimitArgumentName,
                    BackFillerArticleWorkTopology.MaxLengthArgumentName,
                    BackFillerArticleWorkTopology.MessageTtlArgumentName,
                },
                name => Assert.IsType<long>(intended[name]));

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);

            foreach (var queueName in new[] { "backfiller.eweka", "backfiller.giganews" })
            {
                var managementUri = $"{rabbit.Management.BaseUrl!.TrimEnd('/')}/api/queues/%2F/{Uri.EscapeDataString(queueName)}";
                using var response = await http.GetAsync(managementUri);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                using var document = await JsonDocument.ParseAsync(stream);
                var arguments = document.RootElement.GetProperty("arguments");
                Assert.Equal("quorum", arguments.GetProperty("x-queue-type").GetString());
                Assert.Equal(3, arguments.GetProperty("x-quorum-initial-group-size").GetInt32());
                Assert.Equal(5, arguments.GetProperty("x-delivery-limit").GetInt32());
                Assert.Equal(10000, arguments.GetProperty("x-max-length").GetInt32());
                Assert.Equal(60000, arguments.GetProperty("x-message-ttl").GetInt32());

                var factory = new ConnectionFactory
                {
                    HostName = rabbit.Hosts[0],
                    Port = rabbit.Port ?? 5672,
                    UserName = username,
                    Password = password,
                    VirtualHost = rabbit.VirtualHost ?? "/",
                    RequestedHeartbeat = TimeSpan.FromSeconds(30),
                };
                await using var connection = await factory.CreateConnectionAsync(
                    $"vectornntp-topology-lab-{queueName}");
                await using var channel = await connection.CreateChannelAsync();
                var declare = await channel.QueueDeclareAsync(
                    queue: queueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: intended,
                    cancellationToken: CancellationToken.None);
                Assert.Equal(queueName, declare.QueueName);
            }
        }

        /// <summary>Returns whether the lab redeclaration probe should execute.</summary>
        private static bool IsEnabled() =>
            string.Equals(
                Environment.GetEnvironmentVariable(EnableEnvironmentVariable),
                "1",
                StringComparison.Ordinal);

        /// <summary>Binds shipped <c>VectorNNTPD.RabbitMQ.json</c> without hard-coding broker hostnames in this test.</summary>
        private static RabbitMqOptions BindShippedRabbitMqOptions()
        {
            var path = RepositoryFiles.Under(
                "src",
                "VectorNNTPD.BackFillerService",
                "VectorNNTPD.RabbitMQ.json");
            Assert.True(File.Exists(path), path);
            var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddJsonFile(path, optional: false, reloadOnChange: false)
                .Build();
            var rabbit = new RabbitMqOptions();
            configuration.GetSection(RabbitMqOptions.SectionName).Bind(rabbit);
            return rabbit;
        }

        /// <summary>Reads a required environment variable or throws without echoing the value.</summary>
        private static string RequireEnv(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"{name} is required when {EnableEnvironmentVariable}=1.");
            }

            return value;
        }
    }
}
