using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.NntpDb;
using VectorNNTPD.TransitService.Redis;
using VectorNNTPD.TransitService.RabbitMq;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.Common.Core;
using VectorNNTPD.TransitService.Hosting;
using VectorNNTPD.TransitService.Logging;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.NntpDb;

namespace VectorNNTPD.TransitService.Tests.Configuration;

public sealed class NntpdOptionsValidatorTests
{
    private static NntpdOptionsValidator CreateValidator(ILocalIpAddressAssignee? assignee = null) =>
        new(assignee ?? new FakeLocalIpAddressAssignee(assignAll: true));

    [Fact]
    public void Validate_Rejects_OutstandingStoreLimitOutsideOneOrTwo()
    {
        var options = TestHostFactory.CreateValidOptions();
        Assert.Equal(2, options.ArticleIngestion.MaxOutstandingStoresPerConnection);
        Assert.True(CreateValidator().Validate(null, options).Succeeded);
        options.ArticleIngestion.MaxOutstandingStoresPerConnection = 1;
        Assert.True(CreateValidator().Validate(null, options).Succeeded);
        options.ArticleIngestion.MaxOutstandingStoresPerConnection = 64;
        var result = CreateValidator().Validate(null, options);
        Assert.False(result.Succeeded);
        Assert.Contains("MaxOutstandingStoresPerConnection", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Succeeds_ForDefaultHistoryTime()
    {
        var options = TestHostFactory.CreateValidOptions();
        Assert.Equal(TimeSpan.FromHours(2), options.HistoryTime);
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Succeeds_ForConfiguredHistoryTime()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.HistoryTime = TimeSpan.FromHours(6);
        Assert.True(CreateValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void CheckPipelineDepth_IsNotAnOptionsProperty()
    {
        Assert.Null(typeof(NntpdOptions).GetProperty("CheckPipelineDepth"));
        Assert.Equal(16, VectorNNTPD.TransitService.Session.CheckPipeline.Depth);
    }

    [Fact]
    public void BindConfiguration_IgnoresCheckPipelineDepthKey()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nntpd:ServerId"] = "1",
                ["Nntpd:BindAddress:0"] = "127.0.0.1",
                ["Nntpd:CheckPipelineDepth"] = "64",
            })
            .Build();
        var options = new NntpdOptions();
        configuration.GetSection("Nntpd").Bind(options);
        Assert.Null(typeof(NntpdOptions).GetProperty("CheckPipelineDepth"));
        Assert.Equal(16, VectorNNTPD.TransitService.Session.CheckPipeline.Depth);
    }

    [Fact]
    public void Validate_Succeeds_ForDefaultIdleTime()
    {
        var options = TestHostFactory.CreateValidOptions();
        Assert.Equal(NntpdOptions.DefaultIdleTime, options.IdleTime);
        Assert.Equal(300, new NntpdOptions().IdleTime);
        Assert.True(CreateValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_Succeeds_ForConfiguredIdleTime()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.IdleTime = 120;
        Assert.True(CreateValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_Fails_ForIdleTimeBelowOneSecond(int idleTime)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.IdleTime = idleTime;
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains(nameof(NntpdOptions.IdleTime), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Fails_ForIdleTimeAboveOneDay()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.IdleTime = NntpdOptions.MaxIdleTime + 1;
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains(nameof(NntpdOptions.IdleTime), StringComparison.Ordinal));
    }

    [Fact]
    public void BindConfiguration_HonoursIdleTimeSeconds()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nntpd:IdleTime"] = "120",
            })
            .Build();
        var options = new NntpdOptions();
        configuration.GetSection("Nntpd").Bind(options);
        Assert.Equal(120, options.IdleTime);
    }

    [Fact]
    public void Validate_Succeeds_ForDefaultMaxArticleSize()
    {
        var options = TestHostFactory.CreateValidOptions();
        Assert.Equal(NntpdOptions.DefaultMaxArticleSize, options.MaxArticleSize);
        Assert.Equal(5_242_880, new NntpdOptions().MaxArticleSize);
        Assert.True(CreateValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_Fails_ForNonPositiveMaxArticleSize(int size)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.MaxArticleSize = size;
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains(nameof(NntpdOptions.MaxArticleSize), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Fails_ForMaxArticleSizeAboveCeiling()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.MaxArticleSize = NntpdOptions.MaxMaxArticleSize + 1;
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains(nameof(NntpdOptions.MaxArticleSize), StringComparison.Ordinal));
    }

    [Fact]
    public void BindConfiguration_HonoursMaxArticleSize()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nntpd:MaxArticleSize"] = "1048576",
            })
            .Build();
        var options = new NntpdOptions();
        configuration.GetSection("Nntpd").Bind(options);
        Assert.Equal(1_048_576, options.MaxArticleSize);
    }

    [Fact]
    public void Validate_Succeeds_ForDefaultMailComplaintsTo()
    {
        var options = TestHostFactory.CreateValidOptions();
        Assert.Equal(NntpdOptions.DefaultMailComplaintsTo, options.MailComplaintsTo);
        Assert.Equal("abuse@usenet.ninja", new NntpdOptions().MailComplaintsTo);
        Assert.True(CreateValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_Succeeds_WhenTop1000IsMissingOrEmpty()
    {
        var options = TestHostFactory.CreateValidOptions();
        Assert.Empty(options.Top1000);
        Assert.True(CreateValidator().Validate(null, options).Succeeded);
        options.Top1000 = [];
        Assert.True(CreateValidator().Validate(null, options).Succeeded);
        options.Top1000 = ["", "  ", "\t"];
        Assert.True(CreateValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_Succeeds_ForTop1000Mailboxes_IgnoringWhitespace()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.Top1000 = ["top1000@anthologeek.net", "  ", "ops@usenet.ninja"];
        Assert.True(CreateValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_Fails_ForInvalidTop1000Mailbox()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.Top1000 = ["not-a-mailbox"];
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains(nameof(NntpdOptions.Top1000), StringComparison.Ordinal));
    }

    [Fact]
    public void BindConfiguration_HonoursTop1000()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nntpd:Top1000:0"] = "top1000@anthologeek.net",
                ["Nntpd:Top1000:1"] = "ops@usenet.ninja",
            })
            .Build();
        var options = new NntpdOptions();
        configuration.GetSection("Nntpd").Bind(options);
        Assert.Equal(["top1000@anthologeek.net", "ops@usenet.ninja"], options.Top1000);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-mailbox")]
    [InlineData("abuse@localhost")]
    public void Validate_Fails_ForInvalidMailComplaintsTo(string value)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.MailComplaintsTo = value;
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains(nameof(NntpdOptions.MailComplaintsTo), StringComparison.Ordinal));
    }

    [Fact]
    public void BindConfiguration_HonoursMailComplaintsTo()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nntpd:MailComplaintsTo"] = "ops@usenet.ninja",
            })
            .Build();
        var options = new NntpdOptions();
        configuration.GetSection("Nntpd").Bind(options);
        Assert.Equal("ops@usenet.ninja", options.MailComplaintsTo);
    }

    [Fact]
    public void Validate_Succeeds_ForDefaultXTraceKeyWhenConfigured()
    {
        var options = TestHostFactory.CreateValidOptions();
        Assert.Equal(TestHostFactory.TestXTraceKey, options.XTraceKey);
        Assert.True(XTraceKeyParser.TryDecode(options.XTraceKey, out var key));
        Assert.Equal(32, key!.Length);
        Assert.True(CreateValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-key")]
    [InlineData("0123456789abcdef")]
    public void Validate_Fails_ForInvalidXTraceKey(string value)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.XTraceKey = value;
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains(NntpdOptions.XTraceKeyConfigurationKey, StringComparison.Ordinal));
        var joined = NntpdOptionsValidator.JoinFailures(result);
        if (!string.IsNullOrWhiteSpace(value))
        {
            Assert.DoesNotContain(value.Trim(), joined, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Validate_Fails_ForInvalidXTracePreviousKey()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.XTracePreviousKey = "short";
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains(NntpdOptions.XTracePreviousKeyConfigurationKey, StringComparison.Ordinal));
        Assert.DoesNotContain("short", NntpdOptionsValidator.JoinFailures(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Succeeds_WhenNewsmasterCredentialsArePaired()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.NewsmasterUser = "newsmaster";
        options.NewsmasterPassword = "unit-test-newsmaster-password";
        Assert.True(CreateValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_Fails_WhenOnlyNewsmasterUserIsSet()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.NewsmasterUser = "newsmaster";
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains(NntpdOptions.NewsmasterPasswordConfigurationKey, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Fails_WhenOnlyNewsmasterPasswordIsSet()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.NewsmasterPassword = "unit-test-newsmaster-password";
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        var joined = NntpdOptionsValidator.JoinFailures(result);
        Assert.Contains(NntpdOptions.NewsmasterUserConfigurationKey, joined, StringComparison.Ordinal);
        Assert.DoesNotContain("unit-test-newsmaster-password", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void BindConfiguration_HonoursXTraceKey()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nntpd:XTraceKey"] = TestHostFactory.TestXTraceKey,
            })
            .Build();
        var options = new NntpdOptions();
        configuration.GetSection("Nntpd").Bind(options);
        Assert.Equal(TestHostFactory.TestXTraceKey, options.XTraceKey);
    }

    [Fact]
    public void Validate_Fails_ForHistoryTimeBelowOneSecond()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.HistoryTime = TimeSpan.Zero;
        Assert.True(CreateValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void Validate_Succeeds_ForValidDefaults()
    {
        var result = CreateValidator().Validate(null, TestHostFactory.CreateValidOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void FeedDiagnostics_DefaultsAreOff()
    {
        var options = TestHostFactory.CreateValidOptions();
        Assert.False(options.FeedDiagnostics.Enabled);
        Assert.Equal(FeedDiagnosticsOptions.DefaultIntervalSeconds, options.FeedDiagnostics.IntervalSeconds);
        Assert.True(options.FeedDiagnostics.IncludeSessions);
        Assert.True(CreateValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void SpeedTest_DefaultsAreDiagnosticSized()
    {
        var options = TestHostFactory.CreateValidOptions();
        Assert.Equal(SpeedTestOptions.DefaultMaxDurationSeconds, options.SpeedTest.MaxDurationSeconds);
        Assert.Equal(SpeedTestOptions.DefaultMaxBytes, options.SpeedTest.MaxBytes);
        Assert.Equal(SpeedTestOptions.DefaultMaxConcurrent, options.SpeedTest.MaxConcurrent);
        Assert.Equal(SpeedTestOptions.DefaultMaxConcurrentPerPeer, options.SpeedTest.MaxConcurrentPerPeer);
        Assert.True(CreateValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0, 4096, 2, 1)]
    [InlineData(61, 4096, 2, 1)]
    [InlineData(10, 512, 2, 1)]
    [InlineData(10, 4096, 0, 1)]
    [InlineData(10, 4096, 9, 1)]
    [InlineData(10, 4096, 2, 0)]
    [InlineData(10, 4096, 2, 5)]
    public void SpeedTest_InvalidLimits_FailValidation(
        int duration,
        long maxBytes,
        int concurrent,
        int perPeer)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.SpeedTest = new SpeedTestOptions
        {
            MaxDurationSeconds = duration,
            MaxBytes = maxBytes,
            MaxConcurrent = concurrent,
            MaxConcurrentPerPeer = perPeer,
        };
        Assert.True(CreateValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void TransitQueueMemoryLimit_DefaultsToOneGibibyte()
    {
        Assert.Equal(1_073_741_824L, NntpdOptions.DefaultTransitQueueMemoryLimit);
        Assert.Equal(NntpdOptions.DefaultTransitQueueMemoryLimit, new NntpdOptions().TransitQueueMemoryLimit);
        Assert.Equal(NntpdOptions.DefaultTransitQueueMemoryLimit, TestHostFactory.CreateValidOptions().TransitQueueMemoryLimit);
        Assert.True(CreateValidator().Validate(null, TestHostFactory.CreateValidOptions()).Succeeded);
    }

    [Fact]
    public void BindConfiguration_HonoursTransitQueueMemoryLimit()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nntpd:TransitQueueMemoryLimit"] = "2097152",
            })
            .Build();
        var options = new NntpdOptions();
        configuration.GetSection("Nntpd").Bind(options);
        Assert.Equal(2_097_152L, options.TransitQueueMemoryLimit);
        var valid = TestHostFactory.CreateValidOptions();
        valid.TransitQueueMemoryLimit = options.TransitQueueMemoryLimit;
        Assert.True(CreateValidator().Validate(null, valid).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Validate_Fails_ForNonPositiveTransitQueueMemoryLimit(long limit)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.TransitQueueMemoryLimit = limit;
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("TransitQueueMemoryLimit", NntpdOptionsValidator.JoinFailures(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Succeeds_ForMinimumTransitQueueMemoryLimit()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.TransitQueueMemoryLimit = 1;
        Assert.True(CreateValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_Succeeds_ForDefaultIngestionWorkerPoolSettings()
    {
        var options = TestHostFactory.CreateValidOptions();
        var ingestion = options.ArticleIngestion;
        Assert.Equal(ArticleIngestionOptions.DefaultMinWorkers, ingestion.MinWorkers);
        Assert.Equal(ArticleIngestionOptions.DefaultMaxWorkers, ingestion.MaxWorkers);
        Assert.Equal(ArticleIngestionOptions.DefaultMaxPublishConcurrency, ingestion.MaxPublishConcurrency);
        Assert.Equal(
            ArticleIngestionOptions.DefaultOverviewDbWorkQueueMemoryLimit,
            ingestion.OverviewDbWorkQueueMemoryLimit);
        Assert.Equal(
            ArticleIngestionOptions.DefaultOverviewDbMinPublisherWorkers,
            ingestion.OverviewDbMinPublisherWorkers);
        Assert.Equal(
            ArticleIngestionOptions.DefaultOverviewDbMaxPublisherWorkers,
            ingestion.OverviewDbMaxPublisherWorkers);
        Assert.True(CreateValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_Fails_WhenOverviewDbMaxPublisherWorkersLessThanMin()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ArticleIngestion.OverviewDbMinPublisherWorkers = 4;
        options.ArticleIngestion.OverviewDbMaxPublisherWorkers = 2;
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            "OverviewDbMaxPublisherWorkers",
            NntpdOptionsValidator.JoinFailures(result),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Fails_WhenMaxWorkersLessThanMinWorkers()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ArticleIngestion.MinWorkers = 4;
        options.ArticleIngestion.MaxWorkers = 2;
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("MaxWorkers", NntpdOptionsValidator.JoinFailures(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Fails_WhenScaleUpThresholdNotGreaterThanScaleDown()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ArticleIngestion.ScaleUpPressureThreshold = 0.2;
        options.ArticleIngestion.ScaleDownPressureThreshold = 0.2;
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("ScaleUpPressureThreshold", NntpdOptionsValidator.JoinFailures(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(513)]
    public void Validate_Fails_ForInvalidMinWorkers(int minWorkers)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ArticleIngestion.MinWorkers = minWorkers;
        options.ArticleIngestion.MaxWorkers = Math.Max(minWorkers, 1);
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("MinWorkers", NntpdOptionsValidator.JoinFailures(result), StringComparison.Ordinal);
    }

    [Fact]
    public void BindConfiguration_HonoursIngestionWorkerPoolSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nntpd:ArticleIngestion:MinWorkers"] = "3",
                ["Nntpd:ArticleIngestion:MaxWorkers"] = "12",
                ["Nntpd:ArticleIngestion:MaxPublishConcurrency"] = "8",
                ["Nntpd:ArticleIngestion:OverviewDbWorkQueueMemoryLimit"] = "536870912",
                ["Nntpd:ArticleIngestion:OverviewDbMinPublisherWorkers"] = "3",
                ["Nntpd:ArticleIngestion:OverviewDbMaxPublisherWorkers"] = "10",
                ["Nntpd:ArticleIngestion:ScaleIntervalSeconds"] = "5",
                ["Nntpd:ArticleIngestion:ScaleUpPressureThreshold"] = "0.55",
                ["Nntpd:ArticleIngestion:ScaleDownPressureThreshold"] = "0.15",
                ["Nntpd:ArticleIngestion:ScaleUpConsecutiveIntervals"] = "4",
                ["Nntpd:ArticleIngestion:ScaleDownConsecutiveIntervals"] = "6",
            })
            .Build();
        var options = new NntpdOptions();
        configuration.GetSection("Nntpd").Bind(options);
        Assert.Equal(3, options.ArticleIngestion.MinWorkers);
        Assert.Equal(12, options.ArticleIngestion.MaxWorkers);
        Assert.Equal(8, options.ArticleIngestion.MaxPublishConcurrency);
        Assert.Equal(536870912, options.ArticleIngestion.OverviewDbWorkQueueMemoryLimit);
        Assert.Equal(3, options.ArticleIngestion.OverviewDbMinPublisherWorkers);
        Assert.Equal(10, options.ArticleIngestion.OverviewDbMaxPublisherWorkers);
        Assert.Equal(5, options.ArticleIngestion.ScaleIntervalSeconds);
        Assert.Equal(0.55, options.ArticleIngestion.ScaleUpPressureThreshold);
        Assert.Equal(0.15, options.ArticleIngestion.ScaleDownPressureThreshold);
        Assert.Equal(4, options.ArticleIngestion.ScaleUpConsecutiveIntervals);
        Assert.Equal(6, options.ArticleIngestion.ScaleDownConsecutiveIntervals);

        var valid = TestHostFactory.CreateValidOptions();
        valid.ArticleIngestion = options.ArticleIngestion;
        Assert.True(CreateValidator().Validate(null, valid).Succeeded);
    }

    [Fact]
    public void LogDir_DefaultsToLogsSlash()
    {
        Assert.Equal("logs/", NntpdOptions.DefaultLogDir);
        Assert.Equal(NntpdOptions.DefaultLogDir, new NntpdOptions().LogDir);
        Assert.Equal(NntpdOptions.DefaultLogDir, TestHostFactory.CreateValidOptions().LogDir);
        Assert.True(CreateValidator().Validate(null, TestHostFactory.CreateValidOptions()).Succeeded);
    }

    [Fact]
    public void Validate_Fails_ForEmptyLogDir()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.LogDir = " ";
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(NntpdOptions.LogDir), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Fails_ForEmptyApplicationName()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ApplicationName = " ";
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_Fails_ForTooShortShutdownTimeout()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.GracefulShutdownTimeout = TimeSpan.FromMilliseconds(100);
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_Fails_ForTooShortStartupTimeout()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.StartupTimeout = TimeSpan.FromMilliseconds(10);
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_Fails_ForInvalidWatchdogIntervalFraction()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.Systemd = new SystemdOptions { WatchdogIntervalFraction = 1.5 };
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void Validate_Succeeds_ForValidStreamOutstandingArticleDepth(int depth)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.Transit.StreamOutstandingArticleDepth = depth;
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(0)]
    [InlineData(64)]
    public void Validate_Fails_ForInvalidStreamOutstandingArticleDepth(int depth)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.Transit.StreamOutstandingArticleDepth = depth;
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            f => f.Contains("StreamOutstandingArticleDepth", StringComparison.Ordinal));
    }

    [Fact]
    public void Transit_WantTrashAndLogTrash_DefaultTrue()
    {
        var transit = new TransitOptions();
        Assert.True(transit.WantTrash);
        Assert.True(transit.LogTrash);
        var options = TestHostFactory.CreateValidOptions();
        Assert.True(options.Transit.WantTrash);
        Assert.True(options.Transit.LogTrash);
    }
}

public sealed class NntpdConfigurationTests
{
    [Fact]
    public void Bind_StandardizedPascalCaseNames_FromConfiguration()
    {
        // Isolated JSON (not the working-tree appsettings) so bind defaults and PascalCase names are asserted.
        var json = """
                   {
                     "Nntpd": {
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "ServerId": 1
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;

        Assert.Equal(["*"], options.BindAddress);
        Assert.Equal(119, options.BindPort);
        Assert.Equal(0, options.BindPortTls);
        Assert.False(options.IsTlsListenerEnabled);
        Assert.Equal("5811a29d39a0732afb5f160c9b137c3d", options.CloudFlareZoneId);
        Assert.Equal(string.Empty, options.CloudFlareApiKey);
        Assert.Equal("usenet.ninja", options.DnsSuffix);
        Assert.Equal(1, options.ServerId);
        Assert.Equal("transit01.usenet.ninja", options.Fqdn);
    }

    [Fact]
    public void Bind_ConsumesNntpdSectionBindSettings()
    {
        var json = """
                   {
                     "Nntpd": {
                       "BindAddress": [ "127.0.0.1" ],
                       "BindPort": 1199,
                       "BindPortTls": 0,
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "ServerId": 1
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;

        Assert.Equal(["127.0.0.1"], options.BindAddress);
        Assert.Equal(1199, options.BindPort);
        Assert.Equal(0, options.BindPortTls);
        Assert.False(options.IsTlsListenerEnabled);
    }

    [Fact]
    public void Bind_NntpdTransit_RejectsDatabaseOwnedWantTrashAndLogTrash()
    {
        var json = """
                   {
                     "Nntpd": {
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "ServerId": 1,
                       "Transit": {
                         "LogTrash": false,
                         "WantTrash": true
                       }
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value);
        Assert.Contains("nntptransitglobalrevision", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_NntpdTransit_KeepsStreamOutstandingArticleDepth()
    {
        var json = """
                   {
                     "Nntpd": {
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "ServerId": 1,
                       "Transit": {
                         "StreamOutstandingArticleDepth": 12
                       }
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
        Assert.Equal(12, options.Transit.StreamOutstandingArticleDepth);
        Assert.True(options.Transit.WantTrash);
        Assert.True(options.Transit.LogTrash);
    }

    [Fact]
    public void Bind_DoesNotConsumeRootLevelBindSettings()
    {
        var json = """
                   {
                     "BindAddress": [ "203.0.113.10" ],
                     "BindPort": 2119,
                     "BindPortTls": 2563,
                     "Nntpd": {
                       "BindAddress": [ "127.0.0.1" ],
                       "BindPort": 1199,
                       "BindPortTls": 0,
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "ServerId": 1
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json, forceTlsDisabled: false);
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;

        Assert.Equal(["127.0.0.1"], options.BindAddress);
        Assert.Equal(1199, options.BindPort);
        Assert.Equal(0, options.BindPortTls);
        Assert.False(options.IsTlsListenerEnabled);
    }

    [Fact]
    public void Bind_RootLevelBindSettingsAlone_DoNotReplaceNntpdDefaults()
    {
        var json = """
                   {
                     "BindAddress": [ "203.0.113.10" ],
                     "BindPort": 2119,
                     "BindPortTls": 2563,
                     "Nntpd": {
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "ServerId": 1
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json, forceTlsDisabled: false);
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;

        Assert.Equal(["*"], options.BindAddress);
        Assert.Equal(119, options.BindPort);
        Assert.Equal(0, options.BindPortTls);
    }

    [Fact]
    public void Bind_OverlayPreservesNntpdBindPortTlsAgainstRoot()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{NntpdOptions.SectionName}:BindAddress:0"] = "127.0.0.1",
                    [$"{NntpdOptions.SectionName}:BindPort"] = "1199",
                    [$"{NntpdOptions.SectionName}:BindPortTls"] = "5633",
                    ["BindAddress:0"] = "203.0.113.10",
                    ["BindPort"] = "2119",
                    ["BindPortTls"] = "2563",
                })
            .Build();

        var options = new NntpdOptions();
        configuration.GetSection(NntpdOptions.SectionName).Bind(options);
        NntpdAcmeCloudflareOptionsOverlay.OverlaySharedFromRootPreservingApplicationAcme(
            options,
            configuration);

        Assert.Equal(["127.0.0.1"], options.BindAddress);
        Assert.Equal(1199, options.BindPort);
        Assert.Equal(5633, options.BindPortTls);
        Assert.True(options.IsTlsListenerEnabled);
    }

    [Fact]
    public void Bind_HasNoDedicatedBindEnvironmentVariableConstants()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        Assert.Null(typeof(AcmeCloudflareOptions).GetField("BindAddressEnvironmentVariable", flags));
        Assert.Null(typeof(AcmeCloudflareOptions).GetField("BindPortEnvironmentVariable", flags));
        Assert.Null(typeof(AcmeCloudflareOptions).GetField("BindPortTlsEnvironmentVariable", flags));
        Assert.Null(typeof(NntpdOptions).GetField("BindAddressEnvironmentVariable", flags));
        Assert.Null(typeof(NntpdOptions).GetField("BindPortEnvironmentVariable", flags));
        Assert.Null(typeof(NntpdOptions).GetField("BindPortTlsEnvironmentVariable", flags));
    }

    [Fact]
    public void Bind_HonoursConfiguredLogDir()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nntpd:LogDir"] = "D:/nntp-logs/",
            })
            .Build();
        var options = new NntpdOptions();
        configuration.GetSection("Nntpd").Bind(options);
        Assert.Equal("D:/nntp-logs/", options.LogDir);
        var valid = TestHostFactory.CreateValidOptions();
        valid.LogDir = options.LogDir;
        var validator = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true));
        Assert.True(validator.Validate(null, valid).Succeeded);
    }

    [Fact]
    public void ProductionAppsettings_DeclaresIdleTimeDefault()
    {
        var path = FindProductionAppsettings();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(NntpdOptions.DefaultIdleTime, doc.RootElement.GetProperty("Nntpd").GetProperty("IdleTime").GetInt32());
    }

    [Fact]
    public void ProductionAppsettings_DeclaresMaxArticleSizeDefault()
    {
        var path = FindProductionAppsettings();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(
            NntpdOptions.DefaultMaxArticleSize,
            doc.RootElement.GetProperty("Nntpd").GetProperty("MaxArticleSize").GetInt32());
    }

    [Fact]
    public void ProductionAppsettings_DeclaresMailComplaintsToDefault()
    {
        var path = FindProductionAppsettings();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(
            NntpdOptions.DefaultMailComplaintsTo,
            doc.RootElement.GetProperty("Nntpd").GetProperty("MailComplaintsTo").GetString());
    }

    [Fact]
    public void ProductionAppsettings_DeclaresTop1000Recipients()
    {
        var path = FindProductionAppsettings();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var top = doc.RootElement.GetProperty("Nntpd").GetProperty("Top1000");
        Assert.Equal(2, top.GetArrayLength());
        Assert.Equal("top1000@anthologeek.net", top[0].GetString());
        Assert.Equal("cknipe@opticnetworks.net", top[1].GetString());
    }

    [Fact]
    public void ProductionAppsettings_DoesNotDeclareXTraceKey()
    {
        var path = FindProductionAppsettings();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(doc.RootElement.GetProperty("Nntpd").TryGetProperty("XTraceKey", out _));
        Assert.False(doc.RootElement.GetProperty("Nntpd").TryGetProperty("XTracePreviousKey", out _));
        Assert.False(doc.RootElement.GetProperty("Nntpd").TryGetProperty("NewsmasterUser", out _));
        Assert.False(doc.RootElement.GetProperty("Nntpd").TryGetProperty("NewsmasterPassword", out _));
    }

    [Fact]
    public void ProductionAppsettings_DeclaresBindSettingsUnderNntpdOnly()
    {
        var path = FindProductionAppsettings();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var nntpd = root.GetProperty("Nntpd");

        Assert.False(root.TryGetProperty("BindAddress", out _));
        Assert.False(root.TryGetProperty("BindPort", out _));
        Assert.False(root.TryGetProperty("BindPortTls", out _));
        Assert.Equal("198.18.0.66", nntpd.GetProperty("BindAddress")[0].GetString());
        Assert.Equal("2c0f:f030:1442:501:198:18:0:66", nntpd.GetProperty("BindAddress")[1].GetString());
        Assert.Equal(1199, nntpd.GetProperty("BindPort").GetInt32());
        Assert.Equal(5633, nntpd.GetProperty("BindPortTls").GetInt32());
    }

    [Fact]
    public void ProductionAppsettings_DoesNotDeclareSharedConfigColumns()
    {
        var path = FindProductionAppsettings();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var nntpd = root.GetProperty("Nntpd");

        Assert.False(root.TryGetProperty("CloudFlareZoneId", out _));
        Assert.False(root.TryGetProperty("DnsSuffix", out _));
        Assert.False(root.TryGetProperty("AcmeDirectoryUrl", out _));
        Assert.False(root.TryGetProperty("AcmeRenewalThresholdDays", out _));
        Assert.False(nntpd.TryGetProperty("CloudFlareZoneId", out _));
        Assert.False(nntpd.TryGetProperty("DnsSuffix", out _));
        Assert.False(nntpd.TryGetProperty("AcmeDirectoryUrl", out _));
        Assert.False(nntpd.TryGetProperty("AcmeRenewalThresholdDays", out _));
    }

    [Fact]
    public void Bind_ConsumesNntpdZoneIdAndDnsSuffix()
    {
        var json = """
                   {
                     "CloudFlareZoneId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                     "DnsSuffix": "root-must-not-bind.example",
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "BindPortTls": 0,
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "DnsSuffix": "example.test",
                       "ServerId": 7
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;

        Assert.Equal("5811a29d39a0732afb5f160c9b137c3d", options.CloudFlareZoneId);
        Assert.Equal("example.test", options.DnsSuffix);
        Assert.Equal("transit07.example.test", options.Fqdn);
    }

    [Fact]
    public void ProductionAppsettings_DeclaresLogDirDefault()
    {
        var path = FindProductionAppsettings();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("logs/", doc.RootElement.GetProperty("Nntpd").GetProperty("LogDir").GetString());
        Assert.Equal(
            "Information",
            doc.RootElement.GetProperty("Serilog").GetProperty("WriteTo")[0]
                .GetProperty("Args").GetProperty("restrictedToMinimumLevel").GetString());
        Assert.Equal(
            "Debug",
            doc.RootElement.GetProperty("Serilog").GetProperty("MinimumLevel")
                .GetProperty("Override").GetProperty("VectorNNTPD.TransitService").GetString());
        var async = doc.RootElement.GetProperty("Serilog").GetProperty("WriteTo")[1];
        Assert.Equal("Async", async.GetProperty("Name").GetString());
        Assert.Equal(50000, async.GetProperty("Args").GetProperty("bufferSize").GetInt32());
        Assert.True(async.GetProperty("Args").GetProperty("blockWhenFull").GetBoolean());
        Assert.Equal(
            "Debug",
            async.GetProperty("Args").GetProperty("configure")[0].GetProperty("Args")
                .GetProperty("restrictedToMinimumLevel").GetString());
    }

    private static string FindProductionAppsettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Join(dir.FullName, "src", "VectorNNTPD.TransitService", "VectorNNTPD.TransitService.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate src/VectorNNTPD.TransitService/VectorNNTPD.TransitService.json.");
    }

    [Fact]
    public void OldUnderscoreSeparatedNames_AreNotBound()
    {
        // Underscore-separated legacy keys are not aliases. (DnsSuffix vs dnssuffix is the same
        // case-insensitive configuration key and is therefore not covered here.)
        var json = """
                   {
                     "Nntpd": {
                       "bind_address": [ "198.18.0.66" ],
                       "bind_port": 1199,
                       "bind_port_tls": 5633,
                       "cloudflare_api_key": "should-not-bind",
                       "cloudflare_zone_id": "should-not-bind-zone",
                       "server_id": 42,
                       "ServerId": 7,
                       "DnsSuffix": "example.test",
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "XTraceKey": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                     }
                   }
                   """;

        using var jsonStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(jsonStream)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        services.AddOptions<NntpdOptions>()
            .Bind(configuration.GetSection(NntpdOptions.SectionName))
            .PostConfigure(static o =>
            {
                if (o.BindAddress is null || o.BindAddress.Length == 0)
                {
                    o.BindAddress = ["*"];
                }
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<NntpdOptions>, NntpdOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NntpdOptions>>().Value;

        Assert.Equal(["*"], options.BindAddress);
        Assert.Equal(119, options.BindPort);
        Assert.Equal(0, options.BindPortTls);
        Assert.Equal(7, options.ServerId);
        Assert.Equal("example.test", options.DnsSuffix);
        Assert.Equal("5811a29d39a0732afb5f160c9b137c3d", options.CloudFlareZoneId);
        Assert.Equal(TestHostFactory.TestCloudFlareApiKey, options.CloudFlareApiKey);
        Assert.NotEqual("should-not-bind", options.CloudFlareApiKey);
        Assert.NotEqual("should-not-bind-zone", options.CloudFlareZoneId);
    }

    [Fact]
    public void EnvironmentVariables_UseExactCloudFlareNames()
    {
        const string envKey = "env-override-cloudflare-api-key";

        var previousKey = Environment.GetEnvironmentVariable(NntpdOptions.CloudFlareApiKeyEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(NntpdOptions.CloudFlareApiKeyEnvironmentVariable, envKey);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [$"{NntpdOptions.SectionName}:ServerId"] = "1",
                        [$"{NntpdOptions.SectionName}:{NntpdOptions.XTraceKeyConfigurationKey}"] =
                            TestHostFactory.TestXTraceKey,
                        [$"{NntpdOptions.SectionName}:BindAddress:0"] = "*",
                        [$"{NntpdOptions.SectionName}:CloudFlareApiKey"] = "from-json-should-be-overridden",
                        [$"{NntpdOptions.SectionName}:CloudFlareZoneId"] = "5811a29d39a0732afb5f160c9b137c3d",
                    })
                .AddVectorEnvironmentVariables()
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            services.AddOptions<NntpdOptions>()
                .BindConfiguration(NntpdOptions.SectionName)
                .Configure<IConfiguration>(static (options, config) =>
                {
                    NntpdAcmeCloudflareOptionsOverlay.OverlaySharedFromRootPreservingApplicationAcme(options, config);
                })
                .PostConfigure(static options =>
                {
                    options.AcmeEmail = string.Empty;
                    options.AcmeCertificatePassword = string.Empty;
                    options.CloudFlareApiKey = string.Empty;
                })
                .ValidateOnStart();
            services.AddSingleton<IValidateOptions<NntpdOptions>, NntpdOptionsValidator>();

            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<NntpdOptions>>().Value;

            Assert.NotEqual(envKey, options.CloudFlareApiKey);
            Assert.NotEqual("from-json-should-be-overridden", options.CloudFlareApiKey);
            Assert.Equal(string.Empty, options.CloudFlareApiKey);
            Assert.Equal("5811a29d39a0732afb5f160c9b137c3d", options.CloudFlareZoneId);
            Assert.Equal(NntpdOptions.CloudFlareApiKeyEnvironmentVariable, "VECTOR__CLOUDFLAREAPIKEY");
        }
        finally
        {
            Environment.SetEnvironmentVariable(NntpdOptions.CloudFlareApiKeyEnvironmentVariable, previousKey);
        }
    }

    [Fact]
    public void ValidationErrors_DoNotExposeApiKey()
    {
        const string secret = "super-secret-cloudflare-api-key-value";
        var options = TestHostFactory.CreateValidOptions();
        options.CloudFlareApiKey = secret;
        options.BindPort = 0;

        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);

        Assert.True(result.Failed);
        var joined = NntpdOptionsValidator.JoinFailures(result);
        Assert.DoesNotContain(secret, joined, StringComparison.Ordinal);
        Assert.False(NntpdOptionsValidator.ContainsSecret(joined, secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void CloudFlareApiKey_BlankOrMissing_DoesNotFailOptionsValidation(string? apiKey)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.CloudFlareApiKey = apiKey!;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded, NntpdOptionsValidator.JoinFailures(result));
    }

    [Fact]
    public void CloudFlareApiKey_ValidNonblank_Succeeds()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.CloudFlareApiKey = TestHostFactory.TestCloudFlareApiKey;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded, NntpdOptionsValidator.JoinFailures(result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void CloudFlareZoneId_BlankOrMissing_FailsSharedConfiguration(string? zoneId)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(
        [
            new NntpSharedConfigurationCandidate(
                1024,
                "news.example",
                null,
                "https://acme.example.test/directory",
                14,
                zoneId,
                "usenet.ninja"),
        ]));
        Assert.Contains("cloudflarezoneid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudFlareZoneId_ValidNonblank_Succeeds()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.CloudFlareZoneId = "5811a29d39a0732afb5f160c9b137c3d";
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded, NntpdOptionsValidator.JoinFailures(result));
    }

    [Fact]
    public void MissingCloudFlareApiKey_DoesNotFailOptionsValidation()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.CloudFlareApiKey = " ";
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded, NntpdOptionsValidator.JoinFailures(result));
    }

    [Fact]
    public void MissingCloudFlareZoneId_FailsSharedConfiguration()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(
        [
            new NntpSharedConfigurationCandidate(
                1024,
                "news.example",
                null,
                "https://acme.example.test/directory",
                14,
                string.Empty,
                "usenet.ninja"),
        ]));
        Assert.Contains("cloudflarezoneid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_ApplyWhenListenerSettingsOmitted()
    {
        var json = """
                   {
                     "Nntpd": {
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "ServerId": 1,
                       "XTraceKey": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                     }
                   }
                   """;

        using var jsonStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(jsonStream)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        services.AddOptions<NntpdOptions>()
            .Bind(configuration.GetSection(NntpdOptions.SectionName))
            .PostConfigure(static o =>
            {
                if (o.BindAddress is null || o.BindAddress.Length == 0)
                {
                    o.BindAddress = ["*"];
                }
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<NntpdOptions>, NntpdOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NntpdOptions>>().Value;

        Assert.Equal(["*"], options.BindAddress);
        Assert.Equal(119, options.BindPort);
        Assert.Equal(0, options.BindPortTls);
        Assert.False(options.IsTlsListenerEnabled);
        Assert.Equal("usenet.ninja", options.DnsSuffix);
    }

    [Fact]
    public void MissingServerId_FailsValidationAndIsDistinctFromZero()
    {
        var missing = TestHostFactory.CreateValidOptions();
        missing.ServerId = null;
        var missingResult = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, missing);
        Assert.True(missingResult.Failed);
        Assert.Contains("required", NntpdOptionsValidator.JoinFailures(missingResult), StringComparison.OrdinalIgnoreCase);

        var zero = TestHostFactory.CreateValidOptions();
        zero.ServerId = 0;
        var zeroResult = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, zero);
        Assert.True(zeroResult.Failed);
        Assert.Contains("1–255", NntpdOptionsValidator.JoinFailures(zeroResult), StringComparison.Ordinal);

        Assert.Null(missing.ServerId);
        Assert.Equal(0, zero.ServerId);
    }

    [Fact]
    public void MissingServerId_FromConfiguration_FailsStartup()
    {
        var json = """
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "DnsSuffix": "usenet.ninja"
                     }
                   }
                   """;

        using var jsonStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(jsonStream)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        services.AddOptions<NntpdOptions>()
            .Bind(configuration.GetSection(NntpdOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<NntpdOptions>, NntpdOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<NntpdOptions>>().Value);
        Assert.Contains(nameof(NntpdOptions.ServerId), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("198.18.0.66")]
    [InlineData("2001:db8::1")]
    public void BindAddress_AssignedAddresses_AreAccepted(string address)
    {
        var ip = IPAddress.Parse(address);
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = [address];
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(ip))
            .Validate(null, options);
        Assert.True(result.Succeeded, NntpdOptionsValidator.JoinFailures(result));
    }

    [Fact]
    public void BindAddress_MultipleConfiguredAddresses_AreAllValidated()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["198.18.0.66", "2001:db8::1"];
        var assignee = new FakeLocalIpAddressAssignee(
            IPAddress.Parse("198.18.0.66"),
            IPAddress.Parse("2001:db8::1"));
        var result = new NntpdOptionsValidator(assignee).Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void BindAddress_AssignedPrivateAndPublic_AreAccepted()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["10.0.0.5", "203.0.113.10"];
        var assignee = new FakeLocalIpAddressAssignee(
            IPAddress.Parse("10.0.0.5"),
            IPAddress.Parse("203.0.113.10"));
        var result = new NntpdOptionsValidator(assignee).Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void BindAddress_UnassignedAddress_IsRejected()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["198.18.0.66"];
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(false))
            .Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("not assigned", NntpdOptionsValidator.JoinFailures(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BindAddress_InvalidSyntax_IsRejected()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["not-an-ip"];
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void BindAddress_Wildcards_AreAcceptedWithoutNicAssignment(string wildcard)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = [wildcard];
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(false))
            .Validate(null, options);
        Assert.True(result.Succeeded, NntpdOptionsValidator.JoinFailures(result));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(119)]
    [InlineData(65535)]
    public void BindPort_ValidRange_Succeeds(int port)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindPort = port;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void BindPort_InvalidValues_Fail(int port)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindPort = port;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void BindPortTls_UnsetAndZero_DisableTls()
    {
        var unset = TestHostFactory.CreateValidOptions();
        unset.BindPortTls = default;
        Assert.Equal(0, unset.BindPortTls);
        Assert.False(unset.IsTlsListenerEnabled);
        Assert.True(
            new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
                .Validate(null, unset)
                .Succeeded);

        var explicitZero = TestHostFactory.CreateValidOptions();
        explicitZero.BindPortTls = 0;
        Assert.False(explicitZero.IsTlsListenerEnabled);
        Assert.True(
            new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
                .Validate(null, explicitZero)
                .Succeeded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(563)]
    [InlineData(65535)]
    public void BindPortTls_NonZero_EnablesTlsConfiguration(int port)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindPortTls = port;
        options.AcmeEmail = "ops@example.org";
        options.AcmeCertificatePassword = "unit-test-pfx-password";
        Assert.True(options.IsTlsListenerEnabled);
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void BindPortTls_OutOfRange_Fails(int port)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindPortTls = port;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(1, "nntpd01.usenet.ninja")]
    [InlineData(8, "nntpd08.usenet.ninja")]
    [InlineData(9, "nntpd09.usenet.ninja")]
    [InlineData(10, "nntpd10.usenet.ninja")]
    [InlineData(99, "nntpd99.usenet.ninja")]
    public void Fqdn_UsesExactFormat_WithoutDotBeforeId(int serverId, string expected)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ServerId = serverId;
        options.DnsSuffix = "usenet.ninja";

        Assert.Equal(expected, options.Fqdn);
        Assert.Equal(NntpdOptions.ApplicationPrefix, "nntpd");
        Assert.Equal(
            ApplicationFqdn.Build(NntpdOptions.ApplicationPrefix, serverId, "usenet.ninja"),
            options.Fqdn);
        Assert.Equal(
            ApplicationFqdn.Build(NntpdOptions.ApplicationPrefix, serverId, "usenet.ninja"),
            NntpdOptions.FormatFqdn(serverId, "usenet.ninja"));
        Assert.NotEqual(
            ApplicationFqdn.Build("backfiller", serverId, "usenet.ninja"),
            options.Fqdn);
        Assert.DoesNotContain("nntpd.", options.Fqdn, StringComparison.Ordinal);
        Assert.StartsWith($"nntpd{serverId:00}.", options.Fqdn, StringComparison.Ordinal);

        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(255)]
    public void ServerId_ValidBoundaries_Succeed(int serverId)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ServerId = serverId;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(256)]
    public void ServerId_OutOfRange_Fails(int serverId)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ServerId = serverId;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("1–255", NntpdOptionsValidator.JoinFailures(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, (int)ServerIdValidationStatus.Missing, false)]
    [InlineData(-1, (int)ServerIdValidationStatus.OutOfRange, false)]
    [InlineData(0, (int)ServerIdValidationStatus.OutOfRange, false)]
    [InlineData(1, (int)ServerIdValidationStatus.Valid, true)]
    [InlineData(8, (int)ServerIdValidationStatus.Valid, true)]
    [InlineData(99, (int)ServerIdValidationStatus.Valid, true)]
    [InlineData(100, (int)ServerIdValidationStatus.Valid, true)]
    [InlineData(255, (int)ServerIdValidationStatus.Valid, true)]
    [InlineData(256, (int)ServerIdValidationStatus.OutOfRange, false)]
    public void ServerId_DelegatesNumericRulesToCommon(
        int? serverId,
        int expectedStatus,
        bool expectedValid)
    {
        Assert.Equal((ServerIdValidationStatus)expectedStatus, ServerIdRules.Classify(serverId));

        var options = TestHostFactory.CreateValidOptions();
        options.ServerId = serverId;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);

        Assert.Equal(expectedValid, result.Succeeded);
        if (!expectedValid)
        {
            Assert.Contains("1–255", NntpdOptionsValidator.JoinFailures(result), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ServerId_RangeAttribute_UsesSharedCommonBounds()
    {
        var range = typeof(NntpdOptions)
            .GetProperty(nameof(NntpdOptions.ServerId))
            ?.GetCustomAttribute<RangeAttribute>();
        Assert.NotNull(range);
        Assert.Equal(ServerIdRules.MinimumInclusive, Convert.ToInt32(range.Minimum, CultureInfo.InvariantCulture));
        Assert.Equal(ServerIdRules.MaximumInclusive, Convert.ToInt32(range.Maximum, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-bad.example")]
    [InlineData("bad..example")]
    [InlineData("bad_label.example")]
    public void DnsSuffix_Invalid_FailsSharedConfiguration(string suffix)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(
        [
            new NntpSharedConfigurationCandidate(
                1024,
                "news.example",
                null,
                "https://acme.example.test/directory",
                14,
                "0123456789abcdef0123456789abcdef",
                suffix),
        ]));
        Assert.Contains("dnssuffix", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fqdn_CannotBeIndependentlyConfiguredOrOverridden()
    {
        var json = """
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "BindPort": 119,
                       "BindPortTls": 0,
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "DnsSuffix": "usenet.ninja",
                       "ServerId": 8,
                       "Fqdn": "evil.example.com"
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;

        Assert.Equal(8, options.ServerId);
        Assert.Equal("transit08.usenet.ninja", options.Fqdn);
        Assert.DoesNotContain("evil", options.Fqdn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostStart_MissingCloudFlareApiKey_PublishesTheSharedSnapshot()
    {
        var port = TestHostFactory.GetFreeTcpPort();
        var json = $$"""
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "BindPort": {{port}},
                       "CloudFlareApiKey": "",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "ServerId": 1
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        await host.StartAsync();
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
        Assert.Equal(NntpSharedConfigurationColumns.PlaceholderCloudFlareApiKey, options.CloudFlareApiKey);
        Assert.NotEqual(string.Empty, options.CloudFlareApiKey);
        await host.StopAsync();
    }

    [Fact]
    public void HostStart_MissingCloudFlareZoneId_DoesNotFailOptionsValidation()
    {
        var json = """
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": " ",
                       "ServerId": 1
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
        Assert.Equal(" ", options.CloudFlareZoneId);
    }

    [Fact]
    public async Task HostStart_MissingServerId_FailsBeforeRunning()
    {
        var json = """
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d"
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(nameof(NntpdOptions.ServerId), ex.Message, StringComparison.Ordinal);
        Assert.Contains(NntpdOptions.ServerIdEnvironmentVariable, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestHostFactory.TestCloudFlareApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostStart_ValidMandatorySettings_ReachesRunning()
    {
        var port = TestHostFactory.GetFreeTcpPort();
        var json = $$"""
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "BindPort": {{port}},
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "ServerId": 1
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        await host.StartAsync();
        var lifecycle = host.Services.GetRequiredService<ApplicationLifecycle>();
        Assert.Equal(ApplicationState.Running, lifecycle.State);
        await host.StopAsync();
    }

    [Fact]
    public void CloudFlareApiKey_NullFromConfiguration_DoesNotFailOptionsValidation()
    {
        var json = """
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "CloudFlareApiKey": null,
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "ServerId": 1
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
        Assert.Equal(string.Empty, options.CloudFlareApiKey);
    }

    [Fact]
    public void CloudFlareZoneId_NullFromConfiguration_DoesNotFailOptionsValidation()
    {
        var json = """
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": null,
                       "ServerId": 1
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
        Assert.True(string.IsNullOrWhiteSpace(options.CloudFlareZoneId));
    }

    [Fact]
    public void ServerId_NullFromConfiguration_FailsStartup()
    {
        var json = """
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "ServerId": null
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value);
        Assert.Contains(nameof(NntpdOptions.ServerId), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentVariables_DocumentedNames_AreExact()
    {
        Assert.Equal("VECTOR__CLOUDFLAREAPIKEY", NntpdOptions.CloudFlareApiKeyEnvironmentVariable);
        Assert.Equal("VECTOR__CLOUDFLAREZONEID", NntpdOptions.CloudFlareZoneIdEnvironmentVariable);
        Assert.Equal("NNTPD__SERVERID", NntpdOptions.ServerIdEnvironmentVariable);
        Assert.Equal("NNTPD__XTRACEKEY", NntpdOptions.XTraceKeyEnvironmentVariable);
        Assert.Equal("NNTPD__XTRACEPREVIOUSKEY", NntpdOptions.XTracePreviousKeyEnvironmentVariable);
        Assert.Equal("NNTPD__NEWSMASTERUSER", NntpdOptions.NewsmasterUserEnvironmentVariable);
        Assert.Equal("NNTPD__NEWSMASTERPASSWORD", NntpdOptions.NewsmasterPasswordEnvironmentVariable);
        Assert.False(VectorEnvironment.IsCanonicalName(NntpdOptions.ServerIdEnvironmentVariable));
        Assert.False(VectorEnvironment.IsCanonicalName(NntpdOptions.XTraceKeyEnvironmentVariable));
        Assert.False(VectorEnvironment.IsCanonicalName(NntpdOptions.NewsmasterPasswordEnvironmentVariable));
    }

    [Fact]
    public void ApplicationSpecificSettings_BindFromNntpdSection_NotVectorRootKeys()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Nntpd:ServerId"] = "12",
                    ["Nntpd:XTraceKey"] = TestHostFactory.TestXTraceKey,
                    ["ServerId"] = "99",
                    ["XTraceKey"] = new string('a', 64),
                    ["NewsmasterPassword"] = "vector-must-not-bind",
                })
            .Build();

        var options = new NntpdOptions();
        configuration.GetSection(NntpdOptions.SectionName).Bind(options);
        AcmeCloudflareOptions.OverlaySharedFromRoot(options, configuration);

        Assert.Equal(12, options.ServerId);
        Assert.Equal(TestHostFactory.TestXTraceKey, options.XTraceKey);
        Assert.True(string.IsNullOrEmpty(options.NewsmasterPassword));
    }

    private static IHost CreateEmptyNntpdHost(string json, bool forceTlsDisabled = true)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "VectorNNTPD.TransitService.Tests",
            EnvironmentName = Environments.Development,
        });

        using var jsonStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        builder.Configuration.AddJsonStream(jsonStream);
        TestHostFactory.PromoteSharedKeysToRoot(builder.Configuration);
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [$"{NntpdOptions.SectionName}:LogDir"] = TestHostFactory.NewTestLogDir(),
                [$"{NntpdOptions.SectionName}:{NntpdOptions.XTraceKeyConfigurationKey}"] =
                    TestHostFactory.TestXTraceKey,
            });
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<ICloudflareDnsClient>(new FakeCloudflareDnsClient());
        builder.Services.AddSingleton<IRedisConnectionFactory, FakeRedisConnectionFactory>();
        builder.Services.AddSingleton<IRabbitMqConnectionFactory, FakeRabbitMqConnectionFactory>();
        builder.Services.PostConfigure<NntpdOptions>(options =>
        {
            if (forceTlsDisabled)
            {
                options.BindPortTls = 0;
            }

            options.AcmeEmail = string.Empty;
        });
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Redis:Host:0"] = "127.0.0.1",
                ["RabbitMQ:Hosts:0"] = "127.0.0.1",
                ["RabbitMQ:Username"] = "guest",
                ["RabbitMQ:Password"] = "guest",
                ["RabbitMQ:Management:BaseUrl"] = "http://127.0.0.1:15672",
                ["RabbitMQ:Management:RequestTimeoutSeconds"] = "5",
                [$"ConnectionStrings:{NntpDbOptions.ConnectionStringName}"] =
                    TestHostFactory.TestNntpDbConnectionString,
            });
        builder.Services.AddSingleton<INntpDbConnectionFactory, FakeNntpDbConnectionFactory>();
        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.Services.AddNntpTransitHosting(includePlaceholderService: false);
        return builder.Build();
    }

    private static IHost CreateConfiguredHost(
        IReadOnlyDictionary<string, string?>? configuration = null,
        ILocalIpAddressAssignee? assignee = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [NntpdOptions.CloudFlareApiKeyConfigurationKey] =
                    TestHostFactory.TestCloudFlareApiKey,
                [$"{NntpdOptions.SectionName}:BindAddress:0"] = "*",
                [$"{NntpdOptions.SectionName}:BindAddress:1"] = null,
                [$"{NntpdOptions.SectionName}:LogDir"] = TestHostFactory.NewTestLogDir(),
                ["Redis:Host:0"] = "127.0.0.1",
                ["RabbitMQ:Hosts:0"] = "127.0.0.1",
                ["RabbitMQ:Username"] = "guest",
                ["RabbitMQ:Password"] = "guest",
                ["RabbitMQ:Management:BaseUrl"] = "http://127.0.0.1:15672",
                ["RabbitMQ:Management:RequestTimeoutSeconds"] = "5",
                [$"ConnectionStrings:{NntpDbOptions.ConnectionStringName}"] =
                    TestHostFactory.TestNntpDbConnectionString,
            });

        if (configuration is not null)
        {
            builder.Configuration.AddInMemoryCollection(configuration);
        }

        builder.Services.AddSingleton<ILocalIpAddressAssignee>(
            assignee ?? new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<ICloudflareDnsClient>(new FakeCloudflareDnsClient());
        builder.Services.AddSingleton<IRedisConnectionFactory, FakeRedisConnectionFactory>();
        builder.Services.AddSingleton<IRabbitMqConnectionFactory, FakeRabbitMqConnectionFactory>();
        builder.Services.AddSingleton<INntpDbConnectionFactory, FakeNntpDbConnectionFactory>();
        builder.Services.PostConfigure<NntpdOptions>(static options =>
        {
            options.BindAddress = ["*"];
            options.BindPortTls = 0;
            options.AcmeEmail = string.Empty;
        });
        TestHostFactory.IsolateTransit(builder.Services);

        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.Services.AddNntpTransitHosting(includePlaceholderService: false);
        return builder.Build();
    }
}
