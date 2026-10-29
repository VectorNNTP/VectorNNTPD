using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.NntpDb;
using VectorNNTPD.TransitService.Tests.Fixtures;

namespace VectorNNTPD.TransitService.Tests.NntpDb;

public sealed class NntpSharedConfigurationServiceTests
{
    [Fact]
    public async Task Start_publishes_one_shared_configuration_row()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            SharedConfigurationRows =
            [
                new NntpSharedConfigurationCandidate(
                    4096,
                    "news.example",
                    "http://prom.example",
                    "https://acme-v02.api.letsencrypt.org/directory",
                    14,
                    "0123456789abcdef0123456789abcdef",
                    "usenet.ninja"),
            ],
        };
        await using var database = await StartDatabaseAsync(factory);
        var service = new NntpSharedConfigurationService(
            database,
            Options.Create(new NntpdOptions { ServerId = 1, BindPortTls = 0 }),
            NullLogger<NntpSharedConfigurationService>.Instance,
            TimeProvider.System,
            TimeSpan.FromHours(1));

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(4096, service.Current.MaxArticleBytes);
        Assert.Equal("news.example", service.Current.SiteName);
        Assert.Equal("http://prom.example", service.Current.PrometheusUrl);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Start_fails_when_the_row_is_invalid_and_does_not_publish()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            SharedConfigurationRows = [],
        };
        await using var database = await StartDatabaseAsync(factory);
        var service = new NntpSharedConfigurationService(
            database,
            Options.Create(new NntpdOptions { ServerId = 1, BindPortTls = 0 }),
            NullLogger<NntpSharedConfigurationService>.Instance,
            TimeProvider.System,
            TimeSpan.FromHours(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.False(service.HasSnapshot);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Start_copies_shared_credentials_and_does_not_log_them()
    {
        var nntp = Options.Create(new NntpdOptions
        {
            ServerId = 1,
            BindPortTls = 0,
            AcmeEmail = "from-env@example.test",
            AcmeCertificatePassword = "from-env-pfx",
            CloudFlareApiKey = "from-env-key",
        });
        var rabbit = Options.Create(new RabbitMqOptions
        {
            Username = "from-env-user",
            Password = "from-env-pass",
            Hosts = ["127.0.0.1"],
            VirtualHost = "/articles",
        });
        var logger = new CaptureLogger<NntpSharedConfigurationService>();
        await using var database = await StartDatabaseAsync(new FakeNntpDbConnectionFactory
        {
            SharedConfigurationRows = [ValidRow()],
        });
        var service = new NntpSharedConfigurationService(
            database,
            nntp,
            logger,
            TimeProvider.System,
            TimeSpan.FromHours(1),
            rabbit);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(NntpSharedConfigurationColumns.PlaceholderAcmeAccount, nntp.Value.AcmeEmail);
        Assert.Equal(NntpSharedConfigurationColumns.PlaceholderAcmeCertificatePassword, nntp.Value.AcmeCertificatePassword);
        Assert.Equal(NntpSharedConfigurationColumns.PlaceholderCloudFlareApiKey, nntp.Value.CloudFlareApiKey);
        Assert.Equal(NntpSharedConfigurationColumns.PlaceholderRabbitMqUsername, rabbit.Value.Username);
        Assert.Equal(NntpSharedConfigurationColumns.PlaceholderRabbitMqPassword, rabbit.Value.Password);
        Assert.Equal("/articles", rabbit.Value.VirtualHost);
        Assert.Equal("127.0.0.1", Assert.Single(rabbit.Value.Hosts!));
        Assert.DoesNotContain(NntpSharedConfigurationColumns.PlaceholderAcmeAccount, logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(NntpSharedConfigurationColumns.PlaceholderAcmeCertificatePassword, logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(NntpSharedConfigurationColumns.PlaceholderCloudFlareApiKey, logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(NntpSharedConfigurationColumns.PlaceholderRabbitMqPassword, logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(NntpSharedConfigurationColumns.PlaceholderRabbitMqUsername, logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("from-env-pfx", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("from-env-pass", logger.Text, StringComparison.Ordinal);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Invalid_shared_credentials_prevent_the_first_snapshot()
    {
        const string secret = "credential-must-not-be-logged-xxxxxxxxxxxxxxxxxxxx";
        var logger = new CaptureLogger<NntpSharedConfigurationService>();
        await using var database = await StartDatabaseAsync(new FakeNntpDbConnectionFactory
        {
            SharedConfigurationRows = [ValidRow() with { CloudFlareApiKey = secret }],
        });
        var service = new NntpSharedConfigurationService(
            database,
            Options.Create(new NntpdOptions { ServerId = 1, BindPortTls = 0 }),
            logger,
            TimeProvider.System,
            TimeSpan.FromHours(1));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.False(service.HasSnapshot);
        Assert.Contains("cloudflareapikey", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, logger.Text, StringComparison.Ordinal);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Later_invalid_refresh_retains_the_previous_credentials()
    {
        const string secret = "refresh-credential-must-not-be-logged-xxxxxxxxxxxx";
        var factory = new FakeNntpDbConnectionFactory
        {
            SharedConfigurationRows = [ValidRow()],
        };
        var clock = new ControllableTimeProvider();
        var nntp = Options.Create(new NntpdOptions { ServerId = 1, BindPortTls = 0 });
        var rabbit = Options.Create(new RabbitMqOptions());
        var logger = new CaptureLogger<NntpSharedConfigurationService>();
        await using var database = await StartDatabaseAsync(factory);
        var service = new NntpSharedConfigurationService(
            database,
            nntp,
            logger,
            clock,
            TimeSpan.FromSeconds(60),
            rabbit);
        await service.StartAsync(CancellationToken.None);
        var published = service.Current;

        factory.SharedConfigurationRows = [ValidRow() with { RabbitMqPassword = secret }];
        await WaitUntilAsync(() => clock.HasScheduledTimers);
        clock.Advance(TimeSpan.FromSeconds(60));
        await WaitUntilAsync(() => logger.Text.Contains("retaining the last known-good snapshot", StringComparison.Ordinal));

        Assert.Equal(published, service.Current);
        Assert.Equal(NntpSharedConfigurationColumns.PlaceholderRabbitMqPassword, rabbit.Value.Password);
        Assert.Equal(NntpSharedConfigurationColumns.PlaceholderCloudFlareApiKey, nntp.Value.CloudFlareApiKey);
        Assert.DoesNotContain(secret, logger.Text, StringComparison.Ordinal);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Open_before_startup_is_refused()
    {
        var database = new NntpDbService(
            new FakeNntpDbConnectionFactory(),
            Options.Create(new NntpDbOptions { ConnectionString = TestHostFactory.TestNntpDbConnectionString }),
            NullLogger<NntpDbService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.OpenAsync(CancellationToken.None).AsTask());
        await database.DisposeAsync();
    }

    private static NntpSharedConfigurationCandidate ValidRow() =>
        new(
            4096,
            "news.example",
            null,
            "https://acme-v02.api.letsencrypt.org/directory",
            14,
            "0123456789abcdef0123456789abcdef",
            "usenet.ninja");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task<NntpDbService> StartDatabaseAsync(FakeNntpDbConnectionFactory factory)
    {
        var database = new NntpDbService(
            factory,
            Options.Create(new NntpDbOptions { ConnectionString = TestHostFactory.TestNntpDbConnectionString }),
            NullLogger<NntpDbService>.Instance);
        await database.StartAsync(CancellationToken.None);
        return database;
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        private readonly List<string> _entries = [];

        public string Text
        {
            get
            {
                lock (_entries)
                {
                    return string.Join('\n', _entries);
                }
            }
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (exception is not null)
            {
                text = text + " " + exception;
            }

            lock (_entries)
            {
                _entries.Add(text);
            }
        }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
