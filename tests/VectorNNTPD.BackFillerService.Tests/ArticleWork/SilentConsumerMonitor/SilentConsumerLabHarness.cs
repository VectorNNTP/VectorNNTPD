using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using RabbitMQ.Client;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.Stress;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// Lab-only helpers: live RabbitMQ consumer sessions, StressRunId publish/cleanup, Management snapshots.
    /// Not used by ordinary CI runs (callers must gate on <see cref="SilentConsumerLabOptions.IsEnabled"/>).
    /// </summary>
    internal static class SilentConsumerLabHarness
    {
        /// <summary>Giganews Article Work queue used for lab scenarios.</summary>
        internal const string GiganewsQueue = "backfiller.giganews";

        /// <summary>Backbone label matching <see cref="GiganewsQueue"/>.</summary>
        internal const string Backbone = "Giganews";

        /// <summary>Creates a named HttpClient factory for <see cref="ManagementQueueClient"/>.</summary>
        internal static ServiceProvider CreateHttpServices()
        {
            var services = new ServiceCollection();
            services.AddHttpClient(ManagementQueueClient.HttpClientName);
            return services.BuildServiceProvider();
        }

        /// <summary>
        /// Starts <see cref="RabbitMqService"/> against the lab broker with a unique client-provided name.
        /// </summary>
        /// <param name="rabbitOptions">Live options (hosts/credentials).</param>
        /// <param name="connectionName">AMQP client-provided name (visible in Management).</param>
        /// <param name="cancellationToken">Cancels start.</param>
        /// <returns>Started service; caller disposes.</returns>
        internal static async Task<RabbitMqService> StartLiveRabbitAsync(
            RabbitMqOptions rabbitOptions,
            string connectionName,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(rabbitOptions);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
            var factory = new RabbitMqClientConnectionFactory(NullLogger<RabbitMqClientConnectionFactory>.Instance);
            var service = new RabbitMqService(
                factory,
                Options.Create(rabbitOptions),
                new DelegateRabbitMqConnectionNameProvider(() => connectionName),
                NullLogger<RabbitMqService>.Instance);
            await service.StartAsync(cancellationToken).ConfigureAwait(false);
            return service;
        }

        /// <summary>
        /// Builds a MaxSessions=1 Giganews consumer with injectable handler and shared fake clock.
        /// </summary>
        /// <param name="connections">Live or fake RabbitMQ service.</param>
        /// <param name="handler">Article handler (typically <see cref="ControllableArticleWorkHandler"/>).</param>
        /// <param name="time">Clock for stall predicates and reconcile.</param>
        /// <param name="autoReconcile">
        /// When false (default for lab), the background reconcile loop parks until cancel so FakeTimeProvider
        /// advances do not auto-retire sessions; callers invoke <see cref="ArticleWorkConsumerService.ReconcileAsync"/> explicitly.
        /// </param>
        /// <returns>Unstarted consumer service.</returns>
        internal static ArticleWorkConsumerService CreateConsumer(
            IRabbitMqService connections,
            IArticleWorkHandler handler,
            TimeProvider time,
            bool autoReconcile = false)
        {
            ArgumentNullException.ThrowIfNull(connections);
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentNullException.ThrowIfNull(time);
            var catalog = new StaticBackFillerProviderCatalog(
            [
                new BackFillerProviderDefinition(
                    Backbone,
                    "127.0.0.1",
                    119,
                    false,
                    "nntp-user",
                    "p",
                    MinSessions: 0,
                    MaxSessions: 1),
            ]);
            var capacity = new BackboneUsableCapacityState();
            capacity.PublishSnapshot(
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [Backbone] = 1 });
            Func<CancellationToken, Task>? loopBody = autoReconcile
                ? null
                : static async ct =>
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Parked until host stop.
                    }
                };
            return new ArticleWorkConsumerService(
                connections,
                RabbitMqServiceTests.CreateFastRuntime(),
                handler,
                new RecordingArticleWorkResponsePublisher { CompletesSuccessPublication = true },
                NullLogger<ArticleWorkConsumerService>.Instance,
                catalog,
                capacity,
                time,
                loopBody);
        }

        /// <summary>
        /// Observes Inactive once (starts resolve clock), advances <paramref name="resolveWindow"/>, observes again.
        /// </summary>
        internal static ObservationResult ResolveInstanceCondition(
            SilentConsumerMonitorEngine engine,
            IReadOnlyList<ArticleWorkConsumerSession> sessions,
            int desired,
            FakeTimeProvider clock,
            SilentConsumerCondition condition,
            TimeSpan resolveWindow)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(sessions);
            ArgumentNullException.ThrowIfNull(clock);
            ObservationResult First()
            {
                var projected = sessions
                    .Select(
                        s => SilentConsumerSessionObservation.FromSession(
                            s,
                            ArticleWorkConsumerService.ProcessingStallThreshold))
                    .ToArray();
                return engine.ObserveInstance(projected, desired).Single(r => r.Key.Condition == condition);
            }

            var first = First();
            Assert.True(
                first.Transition is ObservationTransition.Continued or ObservationTransition.None,
                $"Expected resolve-clock start, got {first.Transition} signal={first.Signal}");
            clock.Advance(resolveWindow);
            return First();
        }

        /// <summary>Waits until the consumer has one Running session with an open channel.</summary>
        /// <param name="consumer">Started consumer.</param>
        /// <param name="timeout">Safety bound.</param>
        /// <returns>The running session.</returns>
        internal static async Task<ArticleWorkConsumerSession> WaitForRunningSessionAsync(
            ArticleWorkConsumerService consumer,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(consumer);
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var session = consumer.Sessions.FirstOrDefault(
                    static s => s.State == ArticleWorkConsumerState.Running && s.Channel is { IsOpen: true });
                if (session is not null)
                {
                    return session;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for a Running Article Work consumer session.");
        }

        /// <summary>
        /// Publishes one canonical Article Work JSON body with a unique request id and <c>StressRunId</c> header.
        /// </summary>
        /// <param name="stress">Broker credentials/host.</param>
        /// <param name="stressRunId">Owned run id for cleanup.</param>
        /// <param name="cancellationToken">Cancels publish.</param>
        /// <returns>The correlation id used on the message.</returns>
        internal static async Task<string> PublishOwnedWorkAsync(
            StressHarnessOptions stress,
            string stressRunId,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stress);
            ArgumentException.ThrowIfNullOrWhiteSpace(stressRunId);
            var factory = new ConnectionFactory
            {
                HostName = stress.RabbitMqHost,
                Port = stress.RabbitMqPort,
                VirtualHost = stress.RabbitMqVirtualHost,
                UserName = stress.RabbitMqUsername,
                Password = stress.RabbitMqPassword,
                RequestedHeartbeat = TimeSpan.FromSeconds(30),
                AutomaticRecoveryEnabled = false,
                ClientProvidedName = "VectorNNTPD.BackFillerService.Tests:silent-consumer-lab-pub",
            };
            await using var connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var requestId = Guid.NewGuid();
            var correlationId = Guid.NewGuid().ToString("D");
            var json =
                $"{{\"version\":1,\"requestId\":\"{requestId:D}\",\"messageId\":\"<12345@example.invalid>\",\"backbone\":\"Giganews\"}}";
            var props = new BasicProperties
            {
                ContentType = ArticleWorkRequestParser.JsonContentType,
                CorrelationId = correlationId,
                ReplyTo = "silent-consumer-lab.unused-reply",
                DeliveryMode = DeliveryModes.Persistent,
                Headers = new Dictionary<string, object?>
                {
                    ["RequestId"] = requestId.ToString("D"),
                    [StressOwnedWorkMessageCleanup.StressRunIdHeader] = stressRunId,
                },
            };
            await channel.BasicPublishAsync(
                    exchange: GiganewsQueue,
                    routingKey: GiganewsQueue,
                    mandatory: false,
                    basicProperties: props,
                    body: Encoding.UTF8.GetBytes(json),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return correlationId;
        }

        /// <summary>Best-effort StressRunId-scoped cleanup (never purges foreign messages).</summary>
        /// <param name="stress">Broker options.</param>
        /// <param name="stressRunId">Owned run id.</param>
        /// <param name="cancellationToken">Cancels cleanup.</param>
        /// <returns>Owned messages discarded.</returns>
        internal static Task<int> CleanupOwnedAsync(
            StressHarnessOptions stress,
            string stressRunId,
            CancellationToken cancellationToken) =>
            StressOwnedWorkMessageCleanup.RemoveOwnedAsync(
                stress,
                Backbone,
                stressRunId,
                cancellationToken);

        /// <summary>Reads Management gauges for <see cref="GiganewsQueue"/> (no secrets logged).</summary>
        /// <param name="stress">Broker host/credentials.</param>
        /// <param name="cancellationToken">Cancels HTTP.</param>
        /// <returns>Ready, unacked, and consumer counts.</returns>
        internal static async Task<(long Ready, long Unacked, long Consumers)> SampleQueueAsync(
            StressHarnessOptions stress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stress);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var token = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{stress.RabbitMqUsername}:{stress.RabbitMqPassword}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            var url =
                $"http://{stress.RabbitMqHost}:15672/api/queues/%2F/{Uri.EscapeDataString(GiganewsQueue)}";
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = doc.RootElement;
            var ready = root.TryGetProperty("messages_ready", out var r) ? r.GetInt64() : -1;
            var unacked = root.TryGetProperty("messages_unacknowledged", out var u) ? u.GetInt64() : -1;
            var consumers = root.TryGetProperty("consumers", out var c) ? c.GetInt64() : -1;
            return (ready, unacked, consumers);
        }

        /// <summary>
        /// Projects sessions and advances the monitor clock through the open window for one condition evaluate pass.
        /// </summary>
        /// <param name="engine">Monitor engine under test.</param>
        /// <param name="sessions">Live sessions.</param>
        /// <param name="desired">Desired running session count.</param>
        /// <param name="clock">Shared fake clock (must match session clocks for processing stall).</param>
        /// <param name="openWindow">Open window to advance after the priming observe.</param>
        /// <returns>Results from the second observe (after open window).</returns>
        internal static IReadOnlyList<ObservationResult> ObserveInstanceAfterOpenWindow(
            SilentConsumerMonitorEngine engine,
            IReadOnlyList<ArticleWorkConsumerSession> sessions,
            int desired,
            FakeTimeProvider clock,
            TimeSpan openWindow)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(sessions);
            ArgumentNullException.ThrowIfNull(clock);
            var projected = sessions
                .Select(
                    s => SilentConsumerSessionObservation.FromSession(
                        s,
                        ArticleWorkConsumerService.ProcessingStallThreshold))
                .ToArray();
            _ = engine.ObserveInstance(projected, desired);
            clock.Advance(openWindow);
            projected = sessions
                .Select(
                    s => SilentConsumerSessionObservation.FromSession(
                        s,
                        ArticleWorkConsumerService.ProcessingStallThreshold))
                .ToArray();
            return engine.ObserveInstance(projected, desired);
        }

        /// <summary>Builds runtime options used only for monitor HostedService FQDN stamping in lab tests.</summary>
        internal static BackFillerRuntimeOptions CreateMonitorRuntime() =>
            RabbitMqServiceTests.CreateFastRuntime();
    }
}
