using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.NntpDb;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// Stub-HTTP coverage for <see cref="ManagementQueueClient"/> URI construction, Basic auth, JSON parsing,
    /// poll classification, cancellation, and live <see cref="RabbitMqOptions"/> credential refresh.
    /// Does not contact a real RabbitMQ cluster.
    /// </summary>
    public sealed class ManagementQueueClientTests
    {
        /// <summary>
        /// <see cref="ManagementQueueClient.BuildQueueUri"/> percent-encodes virtual host and queue name in the management API path.
        /// </summary>
        [Fact]
        public void BuildQueueUri_encodes_vhost_and_queue()
        {
            var uri = ManagementQueueClient.BuildQueueUri(
                "http://198.18.0.3:15672",
                "/",
                "backfiller.giganews");
            Assert.Equal(
                "http://198.18.0.3:15672/api/queues/%2F/backfiller.giganews",
                uri.AbsoluteUri);
        }

        /// <summary>
        /// A well-formed management JSON document maps to <see cref="ManagementPollKind.Success"/> with parsed gauges and rates.
        /// </summary>
        /// <returns>A task that completes when assertions pass.</returns>
        [Fact]
        public async Task GetQueueAsync_parses_successful_document()
        {
            var json = """
                {
                  "messages_ready": 4,
                  "messages_unacknowledged": 1,
                  "messages": 5,
                  "consumers": 1,
                  "state": "running",
                  "type": "classic",
                  "message_stats": {
                    "ack": 10,
                    "ack_details": { "rate": 0.0 },
                    "deliver_get": 11,
                    "deliver_get_details": { "rate": 0.0 }
                  }
                }
                """;
            var client = CreateClient(new StubHandler(HttpStatusCode.OK, json));
            var result = await client.GetQueueAsync("/", "backfiller.giganews", CancellationToken.None);
            Assert.Equal(ManagementPollKind.Success, result.Kind);
            Assert.NotNull(result.Snapshot);
            Assert.Equal(4, result.Snapshot.MessagesReady);
            Assert.Equal(1, result.Snapshot.MessagesUnacknowledged);
            Assert.Equal(1, result.Snapshot.Consumers);
            Assert.Equal(0.0, result.Snapshot.AckRate);
            Assert.True(result.Snapshot.HasRequiredGauges);
        }

        /// <summary>
        /// Missing optional JSON fields remain null on the snapshot and <see cref="ManagementQueueSnapshot.HasRequiredGauges"/> is false.
        /// </summary>
        /// <returns>A task that completes when assertions pass.</returns>
        [Fact]
        public async Task GetQueueAsync_missing_gauges_are_null_not_zero()
        {
            var client = CreateClient(new StubHandler(HttpStatusCode.OK, """{"state":"running"}"""));
            var result = await client.GetQueueAsync("/", "q", CancellationToken.None);
            Assert.True(result.IsSuccess);
            Assert.Null(result.Snapshot!.MessagesReady);
            Assert.Null(result.Snapshot.Consumers);
            Assert.False(result.Snapshot.HasRequiredGauges);
        }

        /// <summary>
        /// HTTP 401 maps to <see cref="ManagementPollKind.AuthenticationFailure"/> with no snapshot.
        /// </summary>
        /// <returns>A task that completes when assertions pass.</returns>
        [Fact]
        public async Task GetQueueAsync_auth_failure()
        {
            var client = CreateClient(new StubHandler(HttpStatusCode.Unauthorized, "{}"));
            var result = await client.GetQueueAsync("/", "q", CancellationToken.None);
            Assert.Equal(ManagementPollKind.AuthenticationFailure, result.Kind);
            Assert.Null(result.Snapshot);
        }

        /// <summary>
        /// Non-success HTTP status codes (other than 401/404) classify as <see cref="ManagementPollKind.TransientFailure"/>.
        /// </summary>
        /// <returns>A task that completes when assertions pass.</returns>
        [Fact]
        public async Task GetQueueAsync_http_error()
        {
            var client = CreateClient(new StubHandler(HttpStatusCode.InternalServerError, "err"));
            var result = await client.GetQueueAsync("/", "q", CancellationToken.None);
            Assert.Equal(ManagementPollKind.TransientFailure, result.Kind);
        }

        /// <summary>
        /// HTTP 404 maps to <see cref="ManagementPollKind.QueueMissing"/>.
        /// </summary>
        /// <returns>A task that completes when assertions pass.</returns>
        [Fact]
        public async Task GetQueueAsync_queue_missing()
        {
            var client = CreateClient(new StubHandler(HttpStatusCode.NotFound, "{}"));
            var result = await client.GetQueueAsync("/", "q", CancellationToken.None);
            Assert.Equal(ManagementPollKind.QueueMissing, result.Kind);
        }

        /// <summary>
        /// Invalid JSON on HTTP 200 yields transient failure with a reason mentioning JSON parsing.
        /// </summary>
        /// <returns>A task that completes when assertions pass.</returns>
        [Fact]
        public async Task GetQueueAsync_malformed_json()
        {
            var client = CreateClient(new StubHandler(HttpStatusCode.OK, "{not-json"));
            var result = await client.GetQueueAsync("/", "q", CancellationToken.None);
            Assert.Equal(ManagementPollKind.TransientFailure, result.Kind);
            Assert.Contains("Json", result.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// An in-flight request cancelled via <see cref="CancellationToken"/> returns <see cref="ManagementPollKind.Cancelled"/>.
        /// </summary>
        /// <returns>A task that completes when assertions pass.</returns>
        [Fact]
        public async Task GetQueueAsync_respects_cancellation()
        {
            var handler = new StubHandler(HttpStatusCode.OK, "{}") { Delay = TimeSpan.FromSeconds(30) };
            var client = CreateClient(handler);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var result = await client.GetQueueAsync("/", "q", cts.Token);
            Assert.Equal(ManagementPollKind.Cancelled, result.Kind);
        }

        /// <summary>
        /// Null management base URL fails before HTTP with transient failure and a reason referencing BaseUrl.
        /// </summary>
        /// <returns>A task that completes when assertions pass.</returns>
        [Fact]
        public async Task GetQueueAsync_missing_management_base_url_is_transient_failure()
        {
            var factory = new StubHttpClientFactory(new StubHandler(HttpStatusCode.OK, "{}"));
            var rabbit = Options.Create(new RabbitMqOptions
            {
                Username = "user",
                Password = "pass",
                Management = new RabbitMqManagementOptions { BaseUrl = null },
            });
            var client = new ManagementQueueClient(
                factory,
                rabbit,
                new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z")));
            var result = await client.GetQueueAsync("/", "q", CancellationToken.None);
            Assert.Equal(ManagementPollKind.TransientFailure, result.Kind);
            Assert.Contains("BaseUrl", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Each poll reads current username/password from the live <see cref="IOptions{RabbitMqOptions}"/> instance,
        /// including after <see cref="NntpSharedConfiguration.CopyRabbitMqCredentialsTo"/>.
        /// </summary>
        /// <returns>A task that completes when assertions pass.</returns>
        [Fact]
        public async Task GetQueueAsync_uses_live_options_credentials_and_picks_up_account_refresh()
        {
            var rabbit = new RabbitMqOptions
            {
                Username = "before-user",
                Password = "before-pass",
                Management = new RabbitMqManagementOptions
                {
                    BaseUrl = "http://127.0.0.1:15672",
                    RequestTimeoutSeconds = 2,
                },
            };
            var capturing = new CapturingHandler(HttpStatusCode.OK, """{"messages_ready":0,"messages_unacknowledged":0,"consumers":0}""");
            var client = new ManagementQueueClient(
                new StubHttpClientFactory(capturing),
                Options.Create(rabbit),
                new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z")));

            _ = await client.GetQueueAsync("/", "backfiller.giganews", CancellationToken.None);
            Assert.Equal(
                Convert.ToBase64String(Encoding.ASCII.GetBytes("before-user:before-pass")),
                Assert.Single(capturing.AuthorizationParameters));

            // Same path as ProviderAccountConfigurationService → CopyRabbitMqCredentialsTo on the live options instance.
            new NntpSharedConfiguration(
                MaxArticleBytes: 1,
                SiteName: "site",
                PrometheusUrl: "http://p",
                AcmeDirectoryUrl: "https://acme",
                AcmeRenewalThresholdDays: 30,
                CloudFlareZoneId: "zone",
                DnsSuffix: "example.net",
                AcmeAccount: "a@example.net",
                AcmeCertificatePassword: "x",
                CloudFlareApiKey: "k",
                RabbitMqPassword: "after-pass",
                RabbitMqUsername: "after-user").CopyRabbitMqCredentialsTo(rabbit);

            _ = await client.GetQueueAsync("/", "backfiller.giganews", CancellationToken.None);
            Assert.Equal(2, capturing.AuthorizationParameters.Count);
            Assert.Equal(
                Convert.ToBase64String(Encoding.ASCII.GetBytes("after-user:after-pass")),
                capturing.AuthorizationParameters[1]);
            Assert.Equal("after-user", rabbit.Username);
            Assert.Equal("after-pass", rabbit.Password);
            Assert.Equal("http://127.0.0.1:15672", rabbit.Management.BaseUrl);
        }

        /// <summary>
        /// Shared-queue management auth failure does not block instance-level dispatch-stall observation on the engine.
        /// </summary>
        /// <returns>A task that completes when assertions pass.</returns>
        [Fact]
        public async Task Management_auth_failure_does_not_prevent_subsequent_instance_observation()
        {
            var client = CreateClient(new StubHandler(HttpStatusCode.Unauthorized, "{}"));
            var poll = await client.GetQueueAsync("/", "backfiller.giganews", CancellationToken.None);
            Assert.Equal(ManagementPollKind.AuthenticationFailure, poll.Kind);

            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, time);
            _ = engine.ObserveSharedQueue("backfiller.giganews", poll, expectedActive: true);
            _ = engine.ObserveInstance(
                [
                    new SilentConsumerSessionObservation(
                        "Giganews",
                        1,
                        ArticleWorkConsumerState.Running,
                        QueuedCount: 2,
                        ActiveCount: 0,
                        HasOpenChannel: true,
                        IsActiveProcessingStalled: false),
                ],
                desiredRunningSessions: 1);
            time.Advance(SilentConsumerMonitorDefaults.DispatchStall.OpenWindow);
            var opened = engine.ObserveInstance(
                [
                    new SilentConsumerSessionObservation(
                        "Giganews",
                        1,
                        ArticleWorkConsumerState.Running,
                        QueuedCount: 2,
                        ActiveCount: 0,
                        HasOpenChannel: true,
                        IsActiveProcessingStalled: false),
                ],
                1)
                .Single(r => r.Key.Condition == SilentConsumerCondition.DispatchStall);
            Assert.Equal(ObservationTransition.Opened, opened.Transition);
            Assert.Equal(ObservationScope.Instance, opened.Key.Scope);
        }

        /// <summary>
        /// Non-numeric <c>ack_details.rate</c> in JSON leaves <see cref="ManagementQueueSnapshot.AckRate"/> null without failing the parse.
        /// </summary>
        [Fact]
        public void Parse_ignores_invalid_rate()
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                """{"messages_ready":1,"messages_unacknowledged":1,"consumers":1,"message_stats":{"ack_details":{"rate":"x"}}}""");
            var snap = ManagementQueueClient.Parse(
                doc.RootElement,
                "/",
                "q",
                DateTimeOffset.UtcNow);
            Assert.Null(snap.AckRate);
            Assert.Equal(1, snap.MessagesReady);
        }

        /// <summary>
        /// Builds a <see cref="ManagementQueueClient"/> with stub HTTP, fixed RabbitMQ options, and a fake clock.
        /// </summary>
        /// <param name="handler">Handler that simulates management HTTP responses.</param>
        /// <returns>Configured client under test.</returns>
        private static ManagementQueueClient CreateClient(HttpMessageHandler handler)
        {
            var factory = new StubHttpClientFactory(handler);
            var rabbit = Options.Create(new RabbitMqOptions
            {
                Username = "user",
                Password = "pass",
                Management = new RabbitMqManagementOptions
                {
                    BaseUrl = "http://127.0.0.1:15672",
                    RequestTimeoutSeconds = 2,
                },
            });
            return new ManagementQueueClient(
                factory,
                rabbit,
                new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z")));
        }

        /// <summary>
        /// Returns a new <see cref="HttpClient"/> per factory call wrapping the same test handler (not production pooling).
        /// </summary>
        /// <param name="handler">Shared handler instance returned on every <see cref="CreateClient"/> call.</param>
        private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
        {
            /// <summary>Creates a client with infinite timeout so test handlers control delay/cancellation.</summary>
            /// <param name="name">Ignored; production uses named clients.</param>
            /// <returns>HttpClient that does not dispose the shared handler.</returns>
            public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        /// <summary>Deterministic HTTP handler returning fixed status and JSON body; optionally delays to exercise cancellation.</summary>
        private sealed class StubHandler : HttpMessageHandler
        {
            /// <summary>Fixed HTTP status returned by every <see cref="SendAsync"/>.</summary>
            private readonly HttpStatusCode _status;

            /// <summary>Fixed response body (JSON text) returned by every <see cref="SendAsync"/>.</summary>
            private readonly string _body;

            /// <summary>Fixes response status and body for every request.</summary>
            /// <param name="status">HTTP status code to return.</param>
            /// <param name="body">Response entity body (UTF-8 JSON).</param>
            public StubHandler(HttpStatusCode status, string body)
            {
                _status = status;
                _body = body;
            }

            /// <summary>When positive, awaits this delay inside <see cref="SendAsync"/> before responding.</summary>
            public TimeSpan Delay { get; init; }

            /// <inheritdoc />
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Assert.NotNull(request.Headers.Authorization);
                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, cancellationToken);
                }

                return new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                };
            }
        }

        /// <summary>
        /// Like <see cref="StubHandler"/> but records each Basic auth parameter for credential refresh assertions.
        /// </summary>
        /// <param name="status">Fixed HTTP status for every response.</param>
        /// <param name="body">Fixed JSON response body.</param>
        private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
        {
            /// <summary>Base64 Basic credentials parameter from each captured request, in order.</summary>
            public List<string> AuthorizationParameters { get; } = [];

            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Assert.NotNull(request.Headers.Authorization);
                Assert.Equal("Basic", request.Headers.Authorization.Scheme);
                AuthorizationParameters.Add(request.Headers.Authorization.Parameter ?? string.Empty);
                return Task.FromResult(
                    new HttpResponseMessage(status)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    });
            }
        }
    }
}
