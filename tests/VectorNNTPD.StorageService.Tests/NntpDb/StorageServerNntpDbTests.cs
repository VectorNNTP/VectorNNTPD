using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VectorNNTPD.StorageService.Hosting;
using VectorNNTPD.StorageService.Logging;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.NntpDb;
using VectorNNTPD.StorageService.Tests.Fixtures;
using VectorNNTPD.StorageService.Tests.TestDoubles;

namespace VectorNNTPD.StorageService.Tests.NntpDb
{

    public sealed class StorageServerNntpDbTests
    {
        [Fact]
        public void Missing_connection_string_fails_shared_validation()
        {
            var result = new NntpDbOptionsValidator().Validate(null, new NntpDbOptions());
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static failure => failure.Contains("ConnectionStrings:NntpDB", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Startup_publishes_shared_configuration_and_ignores_json_duplicates()
        {
            var storage = StorageServerTestOptions.CreateValid();
            storage.DnsSuffix = "ignored.example";
            storage.CloudFlareZoneId = "json-zone";
            storage.AcmeDirectoryUrl = "https://json.example/directory";
            storage.AcmeRenewalThresholdDays = 9;
            var acme = new AcmeCloudflareOptions
            {
                BindAddress = storage.BindAddress ?? ["*"],
                BindPort = storage.BindPort,
                BindPortTls = storage.BindPortTls ?? 1191,
                AcmeStateDir = storage.AcmeStateDir,
                IncludeNewsHostnameInCertificate = false,
            };
            var rabbit = new RabbitMqOptions { Username = "json-user", Password = "json-secret" };
            var source = new StorageServerRuntimeOptionsSource();
            var messages = new List<string>();
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new CollectingLoggerProvider(messages)));
            var factory = new FakeNntpDbConnectionFactory(FakeNntpDbConnectionFactory.SharedRow(dnsSuffix: "db-config.example"));
            var database = new NntpDbService(
                factory,
                Options.Create(new NntpDbOptions { ConnectionString = FakeNntpDbConnectionFactory.ConnectionString }),
                NullLogger<NntpDbService>.Instance);
            var shared = new StorageServerSharedConfigurationService(
                database,
                Options.Create(storage),
                Options.Create(acme),
                Options.Create(rabbit),
                source,
                loggerFactory.CreateLogger<StorageServerSharedConfigurationService>(),
                TimeProvider.System,
                TimeSpan.FromHours(1));

            await database.StartAsync(CancellationToken.None);
            await shared.StartAsync(CancellationToken.None);
            try
            {
                Assert.Equal("db-config.example", storage.DnsSuffix);
                Assert.Equal("cache01.db-config.example", storage.Fqdn);
                Assert.Equal("0123456789abcdef0123456789abcdef", storage.CloudFlareZoneId);
                Assert.Equal("https://acme-staging-v02.api.letsencrypt.org/directory", storage.AcmeDirectoryUrl);
                Assert.Equal(21, storage.AcmeRenewalThresholdDays);
                Assert.Equal(storage.Fqdn, acme.Fqdn);
                Assert.Equal(NntpSharedConfigurationColumns.PlaceholderCloudFlareApiKey, acme.CloudFlareApiKey);
                Assert.Equal(NntpSharedConfigurationColumns.PlaceholderAcmeAccount, acme.AcmeEmail);
                Assert.Equal(NntpSharedConfigurationColumns.PlaceholderRabbitMqUsername, rabbit.Username);
                Assert.Equal(NntpSharedConfigurationColumns.PlaceholderRabbitMqPassword, rabbit.Password);
                Assert.True(source.IsPublished);
                Assert.Equal(storage.Fqdn, source.Current.Fqdn);
                Assert.DoesNotContain("news.usenet.ninja", source.Current.CertificateDomainNames);
                Assert.Null(shared.Current.PrometheusUrl);
                Assert.NotEqual(0, shared.Current.MaxArticleBytes);
                Assert.NotEqual("http://unused.example/metrics", shared.Current.PrometheusUrl);
                var published = string.Join('\n', messages);
                Assert.Contains("fqdn=cache01.db-config.example", published, StringComparison.Ordinal);
                Assert.DoesNotContain("maxartsize", published, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("sitename", published, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("prometheus", published, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("unused.example", published, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await shared.StopAsync(CancellationToken.None);
                await database.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Invalid_shared_row_fails_startup_without_publishing_runtime_options()
        {
            var storage = StorageServerTestOptions.CreateValid();
            var source = new StorageServerRuntimeOptionsSource();
            var row = FakeNntpDbConnectionFactory.SharedRow();
            row = row with { CloudFlareZoneId = " " };
            var database = new NntpDbService(
                new FakeNntpDbConnectionFactory(row),
                Options.Create(new NntpDbOptions { ConnectionString = FakeNntpDbConnectionFactory.ConnectionString }),
                NullLogger<NntpDbService>.Instance);
            var shared = new StorageServerSharedConfigurationService(
                database,
                Options.Create(storage),
                Options.Create(new AcmeCloudflareOptions { BindPortTls = 1191, AcmeStateDir = "certs" }),
                Options.Create(new RabbitMqOptions()),
                source,
                NullLogger<StorageServerSharedConfigurationService>.Instance,
                TimeProvider.System,
                TimeSpan.FromHours(1));

            await database.StartAsync(CancellationToken.None);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => shared.StartAsync(CancellationToken.None));
            Assert.Contains("cloudflarezoneid", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(source.IsPublished);
            await database.StopAsync(CancellationToken.None);
        }

        [Fact]
        public void Host_registration_uses_the_connection_string_convention()
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder([]);
            builder.Configuration.AddInMemoryCollection(StorageServerTestOptions.CreateValidConfigurationPairs());
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            builder.ConfigureStorageServerLogging();
            builder.ConfigureStorageServerPlatformHosting();
            builder.AddStorageServerHosting();

            using var host = builder.Build();
            var options = host.Services.GetRequiredService<IOptions<NntpDbOptions>>().Value;
            Assert.Equal(FakeNntpDbConnectionFactory.ConnectionString, options.ConnectionString);
            Assert.NotNull(host.Services.GetRequiredService<NntpDbService>());
            Assert.NotNull(host.Services.GetRequiredService<StorageServerSharedConfigurationService>());
        }

        [Fact]
        public void Shared_configuration_query_omits_unused_article_columns()
        {
            var sql = StorageServerSharedConfigurationReader.SelectSql;
            Assert.DoesNotContain("maxartsize", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sitename", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("prometheusurl", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("acmedirectoryurl", sql, StringComparison.Ordinal);
            Assert.Contains("acmerenewalthresholddays", sql, StringComparison.Ordinal);
            Assert.Contains("cloudflarezoneid", sql, StringComparison.Ordinal);
            Assert.Contains("dnssuffix", sql, StringComparison.Ordinal);
            Assert.Contains("acmeaccount", sql, StringComparison.Ordinal);
            Assert.Contains("acmecertpass", sql, StringComparison.Ordinal);
            Assert.Contains("cloudflareapikey", sql, StringComparison.Ordinal);
            Assert.Contains("rabbitmqusername", sql, StringComparison.Ordinal);
            Assert.Contains("rabbitmqpassword", sql, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Refresh_applies_required_columns_and_keeps_the_startup_fqdn()
        {
            var storage = StorageServerTestOptions.CreateValid();
            var acme = new AcmeCloudflareOptions { BindPortTls = 1191, AcmeStateDir = "certs" };
            var source = new StorageServerRuntimeOptionsSource();
            var factory = new FakeNntpDbConnectionFactory(FakeNntpDbConnectionFactory.SharedRow(dnsSuffix: "first.example"));
            var time = new FakeTimeProvider();
            var database = new NntpDbService(
                factory,
                Options.Create(new NntpDbOptions { ConnectionString = FakeNntpDbConnectionFactory.ConnectionString }),
                NullLogger<NntpDbService>.Instance);
            var shared = new StorageServerSharedConfigurationService(
                database,
                Options.Create(storage),
                Options.Create(acme),
                Options.Create(new RabbitMqOptions()),
                source,
                NullLogger<StorageServerSharedConfigurationService>.Instance,
                time,
                TimeSpan.FromSeconds(30));

            await database.StartAsync(CancellationToken.None);
            await shared.StartAsync(CancellationToken.None);
            try
            {
                Assert.Equal("cache01.first.example", source.Current.Fqdn);
                var opens = factory.OpenCount;
                factory.Row = FakeNntpDbConnectionFactory.SharedRow(
                    dnsSuffix: "second.example",
                    directoryUrl: "https://refreshed.example/directory");
                await WaitForRefreshAsync(time, () => factory.OpenCount > opens);
                Assert.Equal("second.example", storage.DnsSuffix);
                Assert.Equal("https://refreshed.example/directory", acme.AcmeDirectoryUrl);
                Assert.Equal("cache01.second.example", acme.Fqdn);
                Assert.Equal("cache01.first.example", source.Current.Fqdn);
                Assert.Null(shared.Current.PrometheusUrl);
            }
            finally
            {
                await shared.StopAsync(CancellationToken.None);
                await database.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Refresh_failure_keeps_the_last_known_good_configuration()
        {
            var storage = StorageServerTestOptions.CreateValid();
            var acme = new AcmeCloudflareOptions { BindPortTls = 1191, AcmeStateDir = "certs" };
            var source = new StorageServerRuntimeOptionsSource();
            var factory = new FakeNntpDbConnectionFactory(FakeNntpDbConnectionFactory.SharedRow(dnsSuffix: "kept.example"));
            var time = new FakeTimeProvider();
            var database = new NntpDbService(
                factory,
                Options.Create(new NntpDbOptions { ConnectionString = FakeNntpDbConnectionFactory.ConnectionString }),
                NullLogger<NntpDbService>.Instance);
            var shared = new StorageServerSharedConfigurationService(
                database,
                Options.Create(storage),
                Options.Create(acme),
                Options.Create(new RabbitMqOptions()),
                source,
                NullLogger<StorageServerSharedConfigurationService>.Instance,
                time,
                TimeSpan.FromSeconds(30));

            await database.StartAsync(CancellationToken.None);
            await shared.StartAsync(CancellationToken.None);
            try
            {
                var opens = factory.OpenCount;
                factory.Row = factory.Row with { CloudFlareZoneId = " " };
                await WaitForRefreshAsync(time, () => factory.OpenCount > opens);
                Assert.Equal("kept.example", storage.DnsSuffix);
                Assert.Equal("kept.example", acme.DnsSuffix);
                Assert.Equal("cache01.kept.example", source.Current.Fqdn);
            }
            finally
            {
                await shared.StopAsync(CancellationToken.None);
                await database.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Stop_ignores_caller_cancellation_of_the_execution_wait()
        {
            var (database, shared) = await CreateStartedSharedConfigurationAsync(TimeSpan.FromHours(1));
            try
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                await shared.StopAsync(cts.Token);
            }
            finally
            {
                await database.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Stop_propagates_unrelated_execution_cancellation()
        {
            var (database, shared) = await CreateStartedSharedConfigurationAsync(TimeSpan.FromHours(1));
            try
            {
                using var foreign = new CancellationTokenSource();
                foreign.Cancel();
                SetExecution(shared, Task.FromCanceled(foreign.Token));

                var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => shared.StopAsync(CancellationToken.None));
                Assert.Equal(foreign.Token, canceled.CancellationToken);
            }
            finally
            {
                await database.StopAsync(CancellationToken.None);
            }
        }

        private static async Task<(NntpDbService Database, StorageServerSharedConfigurationService Shared)> CreateStartedSharedConfigurationAsync(
            TimeSpan interval)
        {
            var storage = StorageServerTestOptions.CreateValid();
            var source = new StorageServerRuntimeOptionsSource();
            var database = new NntpDbService(
                new FakeNntpDbConnectionFactory(FakeNntpDbConnectionFactory.SharedRow()),
                Options.Create(new NntpDbOptions { ConnectionString = FakeNntpDbConnectionFactory.ConnectionString }),
                NullLogger<NntpDbService>.Instance);
            var shared = new StorageServerSharedConfigurationService(
                database,
                Options.Create(storage),
                Options.Create(new AcmeCloudflareOptions { BindPortTls = 1191, AcmeStateDir = "certs" }),
                Options.Create(new RabbitMqOptions()),
                source,
                NullLogger<StorageServerSharedConfigurationService>.Instance,
                TimeProvider.System,
                interval);

            await database.StartAsync(CancellationToken.None);
            await shared.StartAsync(CancellationToken.None);
            return (database, shared);
        }

        private static void SetExecution(StorageServerSharedConfigurationService shared, Task execution)
        {
            var field = typeof(StorageServerSharedConfigurationService).GetField(
                "_execution",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(shared, execution);
        }

        private static async Task WaitForRefreshAsync(FakeTimeProvider time, Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("Shared configuration did not refresh.");
                }

                time.Advance(TimeSpan.FromSeconds(30));
                await Task.Yield();
            }
        }

        private sealed class CollectingLoggerProvider(List<string> messages) : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new CollectingLogger(messages);

            public void Dispose()
            {
            }

            private sealed class CollectingLogger(List<string> messages) : ILogger
            {
                public IDisposable BeginScope<TState>(TState state)
                    where TState : notnull => NullScope.Instance;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(
                    LogLevel logLevel,
                    EventId eventId,
                    TState state,
                    Exception? exception,
                    Func<TState, Exception?, string> formatter) =>
                    messages.Add(formatter(state, exception));

                private sealed class NullScope : IDisposable
                {
                    public static readonly NullScope Instance = new();

                    public void Dispose()
                    {
                    }
                }
            }
        }
    }
}
