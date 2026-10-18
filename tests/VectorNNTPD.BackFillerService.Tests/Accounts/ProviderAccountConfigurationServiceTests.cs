using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.Nntp;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.NntpDb;

namespace VectorNNTPD.BackFillerService.Tests.Accounts
{
    /// <summary>
    /// Provider account refresh, catalog updates, and cancellation around FakeProviderAccountSource.
    /// </summary>

    public sealed class ProviderAccountConfigurationServiceTests
    {
        /// <summary>
        /// Contract: Start loads the initial snapshot and maps rows.
        /// </summary>

        [Fact]
        public async Task Start_loads_the_initial_snapshot_and_maps_rows()
        {
            var source = new FakeProviderAccountSource
            {
                Rows = [ProviderAccountTestRows.Create()],
            };
            await using var harness = CreateHarness(source);

            await harness.Service.StartAsync(CancellationToken.None);

            var provider = Assert.Single(harness.Service.PublishedProviders);
            Assert.Equal("Giganews", provider.Backbone);
            Assert.Equal("news.example.test", provider.Host);
            Assert.Equal(1, source.QueryCount);
            Assert.True(harness.Catalog.TryGetProvider("giganews", out var published));
            Assert.Equal(provider, published);
        }

        /// <summary>
        /// Contract: Start accepts a valid empty provider result.
        /// </summary>

        [Fact]
        public async Task Start_accepts_a_valid_empty_provider_result()
        {
            await using var harness = CreateHarness(new FakeProviderAccountSource());

            await harness.Service.StartAsync(CancellationToken.None);

            Assert.Empty(harness.Service.PublishedProviders);
            Assert.Empty(harness.Catalog.Providers);
        }

        /// <summary>
        /// Contract: Start fails when the initial query fails.
        /// </summary>

        [Fact]
        public async Task Start_fails_when_the_initial_query_fails()
        {
            var source = new FakeProviderAccountSource
            {
                QueryException = new InvalidOperationException("Provider account query failed against NntpDB."),
            };
            await using var harness = CreateHarness(source);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => harness.Service.StartAsync(CancellationToken.None));
            Assert.Equal("Provider account query failed against NntpDB.", ex.Message);
            Assert.DoesNotContain("Password", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(harness.Service.PublishedProviders);
        }

        /// <summary>
        /// Contract: Unchanged refresh does not churn the provider pool.
        /// </summary>

        [Fact]
        public async Task Unchanged_refresh_does_not_churn_the_provider_pool()
        {
            var source = new FakeProviderAccountSource
            {
                Rows = [ProviderAccountTestRows.Create()],
            };
            await using var harness = CreateHarness(source);
            NntpSessionPoolTests.EnqueueReadyServers(harness.Transport, count: 2);
            await harness.Service.StartAsync(CancellationToken.None);
            Assert.True(harness.Registry.TryGetPool("Giganews", out var original));
            await using (var lease = await original.AcquireAsync(CancellationToken.None))
            {
                Assert.Equal(NntpSessionState.Ready, lease.Session.State);
            }

            var connects = harness.Transport.ConnectAttempts.Count;
            Assert.True(await harness.Service.RefreshOnceAsync(CancellationToken.None));

            Assert.True(harness.Registry.TryGetPool("Giganews", out var again));
            Assert.Same(original, again);
            Assert.Equal(connects, harness.Transport.ConnectAttempts.Count);
            Assert.Contains(
                harness.Logger.Messages,
                static message => message.Contains("snapshot unchanged", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Contract: Meaningful row change replaces the provider definition.
        /// </summary>

        [Theory]
        [InlineData("other.example.test", 563, "y", "nntp-user", ProviderAccountTestRows.SecretPassword, 4)]
        [InlineData("news.example.test", 119, "y", "nntp-user", ProviderAccountTestRows.SecretPassword, 4)]
        [InlineData("news.example.test", 563, "n", "nntp-user", ProviderAccountTestRows.SecretPassword, 4)]
        [InlineData("news.example.test", 563, "y", "other-user", ProviderAccountTestRows.SecretPassword, 4)]
        [InlineData("news.example.test", 563, "y", "nntp-user", "replacement-secret", 4)]
        [InlineData("news.example.test", 563, "y", "nntp-user", ProviderAccountTestRows.SecretPassword, 8)]
        public async Task Meaningful_row_change_replaces_the_provider_definition(
            string hostname,
            int port,
            string useSsl,
            string username,
            string password,
            int maxConnections)
        {
            var source = new FakeProviderAccountSource
            {
                Rows = [ProviderAccountTestRows.Create()],
            };
            await using var harness = CreateHarness(source);
            await harness.Service.StartAsync(CancellationToken.None);
            var previous = Assert.Single(harness.Service.PublishedProviders);

            source.Rows =
            [
                ProviderAccountTestRows.Create(
                    hostname: hostname,
                    port: port,
                    useSsl: useSsl,
                    username: username,
                    password: password,
                    maxConnections: maxConnections),
            ];
            Assert.True(await harness.Service.RefreshOnceAsync(CancellationToken.None));

            var next = Assert.Single(harness.Service.PublishedProviders);
            Assert.NotEqual(previous, next);
            Assert.Equal(hostname, next.Host);
            Assert.Equal(port, next.Port);
            Assert.Equal(useSsl == "y", next.UseTls);
            Assert.Equal(username, next.Username);
            Assert.Equal(password, next.Password);
            Assert.Equal(maxConnections, next.MaxSessions);
            Assert.All(
                harness.Logger.Messages,
                message =>
                {
                    Assert.DoesNotContain(ProviderAccountTestRows.SecretPassword, message, StringComparison.Ordinal);
                    Assert.DoesNotContain("replacement-secret", message, StringComparison.Ordinal);
                });
        }

        /// <summary>
        /// Contract: Provider addition removal and reappearance update the catalog.
        /// </summary>

        [Fact]
        public async Task Provider_addition_removal_and_reappearance_update_the_catalog()
        {
            var source = new FakeProviderAccountSource
            {
                Rows = [ProviderAccountTestRows.Create()],
            };
            await using var harness = CreateHarness(source);
            await harness.Service.StartAsync(CancellationToken.None);

            source.Rows =
            [
                ProviderAccountTestRows.Create(),
                ProviderAccountTestRows.Create(backbone: "Eweka", hostname: "eweka.example.test"),
            ];
            Assert.True(await harness.Service.RefreshOnceAsync(CancellationToken.None));
            Assert.Equal(2, harness.Service.PublishedProviders.Count);
            Assert.True(harness.Catalog.TryGetProvider("Eweka", out _));

            source.Rows = [ProviderAccountTestRows.Create(backbone: "Eweka", hostname: "eweka.example.test")];
            Assert.True(await harness.Service.RefreshOnceAsync(CancellationToken.None));
            Assert.False(harness.Catalog.TryGetProvider("Giganews", out _));
            Assert.True(harness.Catalog.TryGetProvider("Eweka", out _));

            source.Rows =
            [
                ProviderAccountTestRows.Create(),
                ProviderAccountTestRows.Create(backbone: "Eweka", hostname: "eweka.example.test"),
            ];
            Assert.True(await harness.Service.RefreshOnceAsync(CancellationToken.None));
            Assert.True(harness.Catalog.TryGetProvider("Giganews", out var restored));
            Assert.Equal("news.example.test", restored.Host);
        }

        /// <summary>
        /// Contract: Shared configuration failure after startup retains both snapshots.
        /// </summary>

        [Fact]
        public async Task Shared_configuration_failure_after_startup_retains_both_snapshots()
        {
            var source = new FakeProviderAccountSource
            {
                Rows = [ProviderAccountTestRows.Create()],
            };
            await using var harness = CreateHarness(source);
            await harness.Service.StartAsync(CancellationToken.None);
            var knownGood = Assert.Single(harness.Service.PublishedProviders);
            var shared = harness.Service.Current;

            harness.Shared.Failure = new InvalidOperationException("nntpsharedconfig has no row.");
            Assert.False(await harness.Service.RefreshOnceAsync(CancellationToken.None));
            Assert.Same(knownGood, Assert.Single(harness.Service.PublishedProviders));
            Assert.Equal(shared, harness.Service.Current);
            Assert.Equal(NntpSharedConfigurationColumns.PlaceholderRabbitMqPassword, harness.Rabbit.Value.Password);
            Assert.Equal(NntpSharedConfigurationColumns.PlaceholderCloudFlareApiKey, harness.Acme.Value.CloudFlareApiKey);
        }

        /// <summary>
        /// Contract: Start copies shared rabbitmq acme and cloudflare credentials.
        /// </summary>

        [Fact]
        public async Task Start_copies_shared_rabbitmq_acme_and_cloudflare_credentials()
        {
            var source = new FakeProviderAccountSource
            {
                Rows = [ProviderAccountTestRows.Create()],
            };
            await using var harness = CreateHarness(source);
            await harness.Service.StartAsync(CancellationToken.None);

            Assert.Equal(NntpSharedConfigurationColumns.PlaceholderRabbitMqUsername, harness.Rabbit.Value.Username);
            Assert.Equal(NntpSharedConfigurationColumns.PlaceholderRabbitMqPassword, harness.Rabbit.Value.Password);
            Assert.Equal("/articles", harness.Rabbit.Value.VirtualHost);
            Assert.NotEqual("from-env-user", harness.Rabbit.Value.Username);
            Assert.Equal(NntpSharedConfigurationColumns.PlaceholderAcmeAccount, harness.Acme.Value.AcmeEmail);
            Assert.Equal(NntpSharedConfigurationColumns.PlaceholderAcmeCertificatePassword, harness.Acme.Value.AcmeCertificatePassword);
            Assert.Equal(NntpSharedConfigurationColumns.PlaceholderCloudFlareApiKey, harness.Acme.Value.CloudFlareApiKey);
            var logged = string.Join('\n', harness.Logger.Messages);
            Assert.DoesNotContain(NntpSharedConfigurationColumns.PlaceholderAcmeAccount, logged, StringComparison.Ordinal);
            Assert.DoesNotContain(NntpSharedConfigurationColumns.PlaceholderAcmeCertificatePassword, logged, StringComparison.Ordinal);
            Assert.DoesNotContain(NntpSharedConfigurationColumns.PlaceholderCloudFlareApiKey, logged, StringComparison.Ordinal);
            Assert.DoesNotContain(NntpSharedConfigurationColumns.PlaceholderRabbitMqPassword, logged, StringComparison.Ordinal);
            Assert.DoesNotContain(NntpSharedConfigurationColumns.PlaceholderRabbitMqUsername, logged, StringComparison.Ordinal);
            Assert.DoesNotContain("from-env-pass", logged, StringComparison.Ordinal);
            Assert.DoesNotContain("from-env-pfx", logged, StringComparison.Ordinal);
        }

        /// <summary>
        /// Contract: Refresh failure retains last known good and recovers.
        /// </summary>

        [Fact]
        public async Task Refresh_failure_retains_last_known_good_and_recovers()
        {
            var source = new FakeProviderAccountSource
            {
                Rows = [ProviderAccountTestRows.Create()],
            };
            await using var harness = CreateHarness(source);
            await harness.Service.StartAsync(CancellationToken.None);
            var knownGood = Assert.Single(harness.Service.PublishedProviders);

            source.QueryException = new InvalidOperationException("Provider account query failed against NntpDB.");
            Assert.False(await harness.Service.RefreshOnceAsync(CancellationToken.None));
            Assert.Same(knownGood, Assert.Single(harness.Service.PublishedProviders));
            Assert.True(harness.Catalog.TryGetProvider("Giganews", out var retained));
            Assert.Equal(knownGood, retained);
            Assert.Contains(
                harness.Logger.Messages,
                static message => message.Contains("retaining last known-good snapshot", StringComparison.Ordinal));

            source.QueryException = null;
            source.Rows = [ProviderAccountTestRows.Create(hostname: "recovered.example.test")];
            Assert.True(await harness.Service.RefreshOnceAsync(CancellationToken.None));
            Assert.Equal("recovered.example.test", Assert.Single(harness.Service.PublishedProviders).Host);
            Assert.Contains(
                harness.Logger.Messages,
                static message => message.Contains("refresh recovered", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Contract: Invalid and unknown rows are rejected without publishing.
        /// </summary>

        [Fact]
        public async Task Invalid_and_unknown_rows_are_rejected_without_publishing()
        {
            var source = new FakeProviderAccountSource
            {
                Rows =
                [
                    ProviderAccountTestRows.Create(backbone: "NotABackbone"),
                    ProviderAccountTestRows.Create(hostname: " "),
                    ProviderAccountTestRows.Create(backbone: "Eweka", hostname: "eweka.example.test"),
                ],
            };
            await using var harness = CreateHarness(source);
            await harness.Service.StartAsync(CancellationToken.None);

            Assert.Equal("Eweka", Assert.Single(harness.Service.PublishedProviders).Backbone);
            Assert.Contains(
                harness.Logger.Messages,
                static message => message.Contains("Rejected provider account row", StringComparison.Ordinal)
                                  && message.Contains("NotABackbone", StringComparison.Ordinal));
            Assert.All(
                harness.Logger.Messages,
                static message => Assert.DoesNotContain(
                    ProviderAccountTestRows.SecretPassword,
                    message,
                    StringComparison.Ordinal));
        }

        /// <summary>
        /// Contract: Cancellation during query is observed.
        /// </summary>

        [Fact]
        public async Task Cancellation_during_query_is_observed()
        {
            var source = new FakeProviderAccountSource
            {
                Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            await using var harness = CreateHarness(source);
            using var cts = new CancellationTokenSource();
            var refresh = harness.Service.RefreshOnceAsync(cts.Token);
            await WaitUntilAsync(() => source.QueryCount == 1);
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
            Assert.Empty(harness.Service.PublishedProviders);
        }

        /// <summary>
        /// Contract: Cancellation during refresh is observed.
        /// </summary>

        [Fact]
        public async Task Cancellation_during_refresh_is_observed()
        {
            var source = new FakeProviderAccountSource
            {
                Rows = [ProviderAccountTestRows.Create()],
            };
            await using var harness = CreateHarness(source);
            NntpSessionPoolTests.EnqueueReadyServers(harness.Transport, count: 1);
            await harness.Service.StartAsync(CancellationToken.None);
            Assert.True(harness.Registry.TryGetPool("Giganews", out var pool));
            var lease = await pool.AcquireAsync(CancellationToken.None);

            source.Rows = [ProviderAccountTestRows.Create(hostname: "replaced.example.test")];
            using var cts = new CancellationTokenSource();
            var refresh = harness.Service.RefreshOnceAsync(cts.Token);
            await WaitUntilAsync(() =>
                harness.Catalog.TryGetProvider("Giganews", out var published)
                && published.Host == "replaced.example.test");
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
            await lease.DisposeAsync();
        }

        /// <summary>
        /// Contract: Shutdown cancels the owned poll loop.
        /// </summary>

        [Fact]
        public async Task Shutdown_cancels_the_owned_poll_loop()
        {
            var source = new FakeProviderAccountSource
            {
                Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var harness = CreateHarness(
                source,
                refreshInterval: TimeSpan.FromHours(1));
            try
            {
                using var startCts = new CancellationTokenSource();
                var starting = harness.Service.StartAsync(startCts.Token);
                await WaitUntilAsync(() => source.QueryCount == 1);
                await startCts.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
            }
            finally
            {
                source.Block.TrySetResult();
                await harness.DisposeAsync();
            }
        }

        /// <summary>
        /// Contract: Shutdown while polling completes without a second refresh.
        /// </summary>

        [Fact]
        public async Task Shutdown_while_polling_completes_without_a_second_refresh()
        {
            var source = new FakeProviderAccountSource();
            await using var harness = CreateHarness(source, refreshInterval: TimeSpan.FromHours(1));
            await harness.Service.StartAsync(CancellationToken.None);
            Assert.Equal(1, source.QueryCount);

            await harness.Service.StopAsync(CancellationToken.None);
            Assert.Equal(1, source.QueryCount);
        }

        /// <summary>
        /// Contract: Dispose with completed poll task completes.
        /// </summary>

        [Fact]
        public async Task Dispose_with_completed_poll_task_completes()
        {
            await using var harness = CreateHarness(new FakeProviderAccountSource());
            SetPollTask(harness.Service, Task.CompletedTask);

            await harness.Service.DisposeAsync();
        }

        /// <summary>
        /// Contract: Dispose waits for pending poll task to complete.
        /// </summary>

        [Fact]
        public async Task Dispose_waits_for_pending_poll_task_to_complete()
        {
            await using var harness = CreateHarness(new FakeProviderAccountSource());
            var pollCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetPollTask(harness.Service, pollCompletion.Task);

            var disposing = harness.Service.DisposeAsync().AsTask();
            try
            {
                var pendingCheck = await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromMilliseconds(100)));
                Assert.NotSame(disposing, pendingCheck);

                pollCompletion.SetResult();

                var completionCheck = await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(2)));
                Assert.Same(disposing, completionCheck);
                await disposing;
            }
            finally
            {
                pollCompletion.TrySetResult();
                await disposing.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        /// <summary>
        /// Contract: Dispose ignores canceled poll task.
        /// </summary>

        [Fact]
        public async Task Dispose_ignores_canceled_poll_task()
        {
            await using var harness = CreateHarness(new FakeProviderAccountSource());
            using var canceledTokenSource = new CancellationTokenSource();
            canceledTokenSource.Cancel();
            SetPollTask(harness.Service, Task.FromCanceled(canceledTokenSource.Token));

            await harness.Service.DisposeAsync();
        }

        /// <summary>
        /// Contract: Dispose ignores operation canceled exception from poll task.
        /// </summary>

        [Fact]
        public async Task Dispose_ignores_operation_canceled_exception_from_poll_task()
        {
            await using var harness = CreateHarness(new FakeProviderAccountSource());
            SetPollTask(harness.Service, Task.FromException(new OperationCanceledException("poll canceled")));

            await harness.Service.DisposeAsync();
        }

        /// <summary>
        /// Contract: Dispose propagates non cancellation poll fault.
        /// </summary>

        [Fact]
        public async Task Dispose_propagates_non_cancellation_poll_fault()
        {
            await using var harness = CreateHarness(new FakeProviderAccountSource());
            SetPollTask(harness.Service, Task.FromException(new InvalidOperationException("poll fault")));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.DisposeAsync().AsTask());
            Assert.Equal("poll fault", ex.Message);
        }

        /// <summary>
        /// Contract: Dispose is idempotent and logs stopped once.
        /// </summary>

        [Fact]
        public async Task Dispose_is_idempotent_and_logs_stopped_once()
        {
            await using var harness = CreateHarness(new FakeProviderAccountSource());
            SetPollTask(harness.Service, Task.CompletedTask);

            await harness.Service.DisposeAsync();
            await harness.Service.DisposeAsync();

            Assert.Equal(1, harness.Logger.Entries.Count(entry => entry.EventId.Id == 5609));
        }

        /// <summary>
        /// Contract: Concurrent refresh is skipped.
        /// </summary>

        [Fact]
        public async Task Concurrent_refresh_is_skipped()
        {
            var source = new FakeProviderAccountSource
            {
                Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            await using var harness = CreateHarness(source);
            var first = harness.Service.RefreshOnceAsync(CancellationToken.None);
            await WaitUntilAsync(() => source.QueryCount == 1 && harness.Service.RefreshInProgress);

            Assert.False(await harness.Service.RefreshOnceAsync(CancellationToken.None));
            source.Block.TrySetResult();
            Assert.True(await first);
            Assert.Equal(1, source.QueryCount);
        }

        /// <summary>
        /// Contract: Poll does not start a second refresh while one is running.
        /// </summary>

        [Fact]
        public async Task Poll_does_not_start_a_second_refresh_while_one_is_running()
        {
            var source = new FakeProviderAccountSource
            {
                Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                BlockAfterQueryCount = 2,
            };
            await using var harness = CreateHarness(source, refreshInterval: TimeSpan.FromMilliseconds(20));
            await harness.Service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => source.QueryCount >= 2);

            Assert.False(await harness.Service.RefreshOnceAsync(CancellationToken.None));
            source.Block.TrySetResult();
            await WaitUntilAsync(() => !harness.Service.RefreshInProgress);
        }

        /// <summary>
        /// Contract: Article work request path does not take a mysql dependency.
        /// </summary>

        [Fact]
        public void Article_work_request_path_does_not_take_a_mysql_dependency()
        {
            var types = new[]
            {
                typeof(VectorNNTPD.BackFillerService.ArticleWork.ProviderArticleWorkHandler),
                typeof(NntpArticleRetriever),
                typeof(VectorNNTPD.BackFillerService.ArticleWork.ArticleWorkDeliveryPipeline),
                typeof(VectorNNTPD.BackFillerService.ArticleWork.ArticleWorkConsumerService),
            };

            foreach (var type in types)
            {
                foreach (var ctor in type.GetConstructors())
                {
                    Assert.DoesNotContain(
                        ctor.GetParameters(),
                        static parameter => parameter.ParameterType == typeof(IProviderAccountSource)
                                            || parameter.ParameterType.FullName?.Contains("MySql", StringComparison.Ordinal) == true);
                }
            }
        }

        /// <summary>
        /// Wires the production account service with fake account source, scripted transport, and collecting logger.
        /// Does not start polling or refresh accounts.
        /// </summary>
        /// <param name="source">Fake account rows read by refresh.</param>
        /// <param name="refreshInterval">Optional override for runtime account refresh interval.</param>

        private static ServiceHarness CreateHarness(
            FakeProviderAccountSource source,
            TimeSpan? refreshInterval = null)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.BackFillerAccountRefreshIntervalSeconds = 3600;
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb());
            if (refreshInterval is { } interval)
            {
                runtime = runtime with { AccountRefreshInterval = interval };
            }

            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory();
            var logger = new CollectingLogger<ProviderAccountConfigurationService>();
            var registry = new NntpProviderRegistry(
                catalog,
                transport,
                NntpSessionOptions.Default with
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                },
                TimeSpan.FromSeconds(2),
                NullLogger<NntpProviderRegistry>.Instance);
            var shared = new FixedSharedConfigurationSource();
            var acme = Options.Create(new AcmeCloudflareOptions
            {
                AcmeEmail = "from-env@example.test",
                AcmeCertificatePassword = "from-env-pfx",
                CloudFlareApiKey = "from-env-key",
            });
            var rabbit = Options.Create(new RabbitMqOptions
            {
                Username = "from-env-user",
                Password = "from-env-pass",
                VirtualHost = "/articles",
            });
            var service = new ProviderAccountConfigurationService(
                source,
                shared,
                registry,
                runtime,
                Options.Create(new BackFillerOptions()),
                acme,
                logger,
                rabbit);
            return new ServiceHarness(service, catalog, registry, transport, logger, shared, acme, rabbit);
        }

        /// <summary>
        /// Polls every 1 ms until the condition is true or five seconds elapse.
        /// Throws on timeout; does not invoke refresh or start services.
        /// </summary>
        /// <param name="condition">Predicate polled until it returns true.</param>

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(1, timeout.Token);
            }
        }

        /// <summary>Replaces the private background poll task via reflection for deterministic shutdown tests.</summary>
        /// <param name="service">Service instance whose poll task field is overwritten.</param>
        /// <param name="pollTask">Task standing in for the background poll loop.</param>

        private static void SetPollTask(ProviderAccountConfigurationService service, Task pollTask)
        {
            ArgumentNullException.ThrowIfNull(service);
            ArgumentNullException.ThrowIfNull(pollTask);
            var pollTaskField = typeof(ProviderAccountConfigurationService).GetField("_pollTask", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(pollTaskField);
            pollTaskField.SetValue(service, pollTask);
        }

        /// <summary>Hosts ProviderAccountConfigurationService with fake account source and controllable refresh timing.</summary>
        private sealed class ServiceHarness : IAsyncDisposable
        {
            /// <summary>Retains wired dependencies for account refresh and NNTP registry tests.</summary>
            /// <param name="service">Service under test.</param>
            /// <param name="catalog">Provider catalog updated by refresh.</param>
            /// <param name="registry">NNTP registry receiving provider definitions.</param>
            /// <param name="transport">Scripted NNTP transport for connect assertions.</param>
            /// <param name="logger">Collecting logger for refresh diagnostics.</param>
            /// <param name="shared">Shared configuration source stub.</param>
            /// <param name="acme">ACME options snapshot.</param>
            /// <param name="rabbit">RabbitMQ options snapshot.</param>
            internal ServiceHarness(
                ProviderAccountConfigurationService service,
                ProviderConfigurationCatalog catalog,
                NntpProviderRegistry registry,
                ScriptedNntpTransportFactory transport,
                CollectingLogger<ProviderAccountConfigurationService> logger,
                FixedSharedConfigurationSource shared,
                IOptions<AcmeCloudflareOptions> acme,
                IOptions<RabbitMqOptions> rabbit)
            {
                Service = service;
                Catalog = catalog;
                Registry = registry;
                Transport = transport;
                Logger = logger;
                Shared = shared;
                Acme = acme;
                Rabbit = rabbit;
            }

            /// <summary>Account configuration service under test.</summary>

            public ProviderAccountConfigurationService Service { get; }

            /// <summary>Catalog mutated when refresh publishes provider definitions.</summary>

            public ProviderConfigurationCatalog Catalog { get; }

            /// <summary>NNTP registry receiving providers from refresh.</summary>

            public NntpProviderRegistry Registry { get; }

            /// <summary>Scripted transport recording connect attempts during refresh.</summary>

            public ScriptedNntpTransportFactory Transport { get; }

            /// <summary>Collecting logger capturing refresh and validation diagnostics.</summary>

            public CollectingLogger<ProviderAccountConfigurationService> Logger { get; }

            /// <summary>Configurable shared-configuration stub wired into the service.</summary>

            public FixedSharedConfigurationSource Shared { get; }

            /// <summary>ACME options snapshot passed into refresh validation.</summary>

            public IOptions<AcmeCloudflareOptions> Acme { get; }

            /// <summary>RabbitMQ options snapshot passed into refresh validation.</summary>

            public IOptions<RabbitMqOptions> Rabbit { get; }

            /// <summary>Disposes the service and registry; does not dispose the fake account source.</summary>

            public async ValueTask DisposeAsync()
            {
                await Service.DisposeAsync().ConfigureAwait(false);
                await Registry.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// IBackFillerSharedConfigurationSource returning a fixed configuration root for harness tests.
        /// </summary>

        private sealed class FixedSharedConfigurationSource : IBackFillerSharedConfigurationSource
        {
            /// <summary>Configuration snapshot returned on successful reads.</summary>

            public NntpSharedConfiguration Value { get; set; } = new(
                1024,
                "news.usenet.ninja",
                null,
                "https://acme-v02.api.letsencrypt.org/directory",
                14,
                "0123456789abcdef0123456789abcdef",
                "usenet.ninja");

            /// <summary>Number of ReadAsync calls observed by tests.</summary>

            public int Reads { get; private set; }

            /// <summary>When set, ReadAsync throws instead of returning <see cref="Value"/>.</summary>

            public Exception? Failure { get; set; }

            /// <summary>Increments read count, honors cancellation, and returns or throws per <see cref="Failure"/>.</summary>
            /// <param name="cancellationToken">Honored via <see cref="CancellationToken.ThrowIfCancellationRequested"/>.</param>

            public ValueTask<NntpSharedConfiguration> ReadAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Reads++;
                if (Failure is not null)
                {
                    throw Failure;
                }

                return ValueTask.FromResult(Value);
            }
        }
    }
}
