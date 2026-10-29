using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Logging;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Email;
using VectorNNTPD.ReaderService.Hosting;
using VectorNNTPD.ReaderService.Logging;
using VectorNNTPD.ReaderService.Ninpaths;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace VectorNNTPD.ReaderService.Tests.Ninpaths;

public sealed class NinpathsProcessingServiceTests
{
    [Fact]
    public void Top1000_WhitespaceOnly_IsDisabled()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.Top1000 = ["  ", "\t"];
        Assert.False(NinpathsTop1000.IsEnabled(options));
        Assert.Empty(NinpathsTop1000.GetRecipients(options));
    }

    [Fact]
    public void Top1000_DedupesAndKeepsValidMailboxes()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.Top1000 = ["a@example.com", " ", "A@example.com", "b@example.com"];
        Assert.Equal(["a@example.com", "b@example.com"], NinpathsTop1000.GetRecipients(options));
    }

    [Fact]
    public async Task Handler_ReturnsWithoutParsing_UntilTheWorkerRuns()
    {
        using var dir = new TempDir();
        var path = dir.Write("inpaths-20260927.log", "Path: a!b!c\n");
        var email = new RecordingEmailService();
        var service = CreateService(email, ["survey@example.com"]);
        var handler = CreateHandler(service, ["survey@example.com"]);

        handler.OnCompletedFile(path);

        Assert.Equal(1, service.PendingCount);
        Assert.Empty(email.Sent);
        Assert.True(File.Exists(path));

        await service.StartAsync(CancellationToken.None);
        await email.SentOnce.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        var message = Assert.Single(email.Sent);
        Assert.Equal("inpaths nntpd01.usenet.ninja", message.Subject);
        var body = Encoding.Latin1.GetString(message.Body.Span);
        Assert.StartsWith("!!NINP 3.1.1 ", body, StringComparison.Ordinal);
        Assert.Contains("!!NLREC", body, StringComparison.Ordinal);
        Assert.Contains("!!NLEND", body, StringComparison.Ordinal);
        Assert.DoesNotContain("survey@example.com", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultipleRecipients_ShareOneGeneratedReport()
    {
        using var dir = new TempDir();
        var path = dir.Write("inpaths-20260927.log", "Path: a!b!c\n");
        var email = new RecordingEmailService();
        var service = CreateService(email, ["one@example.com", "two@example.com"]);
        var handler = CreateHandler(service, ["one@example.com", "two@example.com"]);
        await service.StartAsync(CancellationToken.None);
        handler.OnCompletedFile(path);
        await email.SentOnce.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, email.SendCount);
        var message = Assert.Single(email.Sent);
        Assert.Equal(["one@example.com", "two@example.com"], message.To.Select(static a => a.Address).ToArray());
        Assert.Equal("us-ascii", message.Charset);
        Assert.Contains(message.Headers, static h => h.Name == "Auto-Submitted" && h.Value == "auto-generated");
    }

    [Fact]
    public async Task EmailFailure_IsNonFatal_AndDoesNotFaultExecution()
    {
        using var dir = new TempDir();
        var path = dir.Write("inpaths-20260927.log", "Path: a!b!c\n");
        var email = new RecordingEmailService
        {
            Throw = new InvalidOperationException("smtp exploded"),
        };
        var service = CreateService(email, ["survey@example.com"]);
        var handler = CreateHandler(service, ["survey@example.com"]);
        await service.StartAsync(CancellationToken.None);
        handler.OnCompletedFile(path);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (service.PendingCount > 0 && !wait.IsCancellationRequested)
        {
            await Task.Delay(10, wait.Token);
        }

        Assert.NotNull(service.Execution);
        Assert.False(service.Execution.IsFaulted);
        Assert.False(service.Execution.IsCompleted);
        await service.StopAsync(CancellationToken.None);
        Assert.False(service.Execution.IsFaulted);
    }

    [Fact]
    public async Task EmailDisabledStatus_IsNonFatal()
    {
        using var dir = new TempDir();
        var path = dir.Write("inpaths-20260927.log", "Path: a!b!c\n");
        var email = new RecordingEmailService { Status = EmailEnqueueStatus.Disabled };
        var service = CreateService(email, ["survey@example.com"]);
        var handler = CreateHandler(service, ["survey@example.com"]);
        await service.StartAsync(CancellationToken.None);
        handler.OnCompletedFile(path);
        await email.SentOnce.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(EmailEnqueueStatus.Disabled, email.Status);
        Assert.False(service.Execution!.IsFaulted);
    }

    [Fact]
    public async Task DisabledTop1000_DoesNotOpenOrEnqueue()
    {
        using var dir = new TempDir();
        var path = dir.Write("inpaths-20260927.log", "Path: a!b!c\n");
        var email = new RecordingEmailService();
        var service = CreateService(email, []);
        var handler = CreateHandler(service, []);
        handler.OnCompletedFile(path);
        Assert.Equal(0, service.PendingCount);
        Assert.Empty(email.Sent);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DuplicateCompletedFile_IsNotProcessedTwice()
    {
        using var dir = new TempDir();
        var path = dir.Write("inpaths-20260927.log", "Path: a!b!c\n");
        var email = new RecordingEmailService();
        var service = CreateService(email, ["survey@example.com"]);
        var handler = CreateHandler(service, ["survey@example.com"]);
        handler.OnCompletedFile(path);
        handler.OnCompletedFile(path);
        Assert.Equal(1, service.PendingCount);
        await service.StartAsync(CancellationToken.None);
        await email.SentOnce.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(1, email.SendCount);
    }

    [Fact]
    public async Task CompletedSourceRemainsReadableAfterDelete_ViaShareDeleteHandle()
    {
        using var dir = new TempDir();
        var path = dir.Write("inpaths-20260927.log", "Path: a!b!c\n");
        var email = new RecordingEmailService();
        var service = CreateService(email, ["survey@example.com"]);
        var handler = CreateHandler(service, ["survey@example.com"]);
        handler.OnCompletedFile(path);
        File.Delete(path);
        Assert.False(File.Exists(path));
        await service.StartAsync(CancellationToken.None);
        await email.SentOnce.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task GzipStillRuns_WhileNinpathsHoldsTheFile()
    {
        using var dir = new TempDir();
        var path = dir.Write("inpaths-20260927.log", "Path: a!b!c\n");
        var email = new RecordingEmailService { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = CreateService(email, ["survey@example.com"]);
        var handler = CreateHandler(service, ["survey@example.com"]);
        await service.StartAsync(CancellationToken.None);

        handler.OnCompletedFile(path);
        Assert.True(new GzipLogFileCompressor().TryCompressAndReplace(path));

        Assert.False(File.Exists(path));
        var archive = Path.Join(dir.Path, NntpdFileLogging.GzipArchiveFileName(path));
        Assert.True(File.Exists(archive));

        email.Hold.SetResult();
        await email.SentOnce.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task EmptySurvey_DoesNotSendEmail()
    {
        using var dir = new TempDir();
        var path = dir.Write("inpaths-20260927.log", "");
        var email = new RecordingEmailService();
        var service = CreateService(email, ["survey@example.com"]);
        var handler = CreateHandler(service, ["survey@example.com"]);
        await service.StartAsync(CancellationToken.None);
        handler.OnCompletedFile(path);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (service.PendingCount > 0 && !wait.IsCancellationRequested)
        {
            await Task.Delay(10, wait.Token);
        }

        await service.StopAsync(CancellationToken.None);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public void Host_RegistersNinpathsHandlerAndService()
    {
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.Services.AddNntpReaderHosting(includePlaceholderService: false);

        using var host = builder.Build();
        Assert.IsType<NinpathsCompletedFileHandler>(host.Services.GetRequiredService<ICompletedPathSurveyFileHandler>());
        var services = host.Services.GetServices<VectorNNTPD.Common.Core.IApplicationService>().Select(static s => s.GetType()).ToArray();
        var email = Array.IndexOf(services, typeof(EmailDeliveryService));
        var ninpaths = Array.IndexOf(services, typeof(NinpathsProcessingService));
        Assert.True(email >= 0);
        Assert.Equal(email + 1, ninpaths);
    }

    [Fact]
    public void ReportMail_DoesNotEmbedRecipientAddressesInTheBody()
    {
        var report = "!!NINP 3.1.1 1 2 0 1 2\n\n!!NLREC\n\n!!NLEND 0\n"u8.ToArray();
        var message = NinpathsReportMail.TryCreate(
            report,
            "nntpd01.usenet.ninja",
            "noreply@usenet.ninja",
            ["one@example.com", "two@example.com"]);
        Assert.NotNull(message);
        var body = Encoding.Latin1.GetString(message.Body.Span);
        Assert.DoesNotContain("one@example.com", body, StringComparison.Ordinal);
        Assert.DoesNotContain("two@example.com", body, StringComparison.Ordinal);
        Assert.Equal("inpaths nntpd01.usenet.ninja", message.Subject);
    }

    private static NinpathsProcessingService CreateService(RecordingEmailService email, string[] top1000)
    {
        var nntpd = TestHostFactory.CreateValidOptions();
        nntpd.Top1000 = top1000;
        var emailOptions = new EmailOptions { DefaultFrom = "noreply@usenet.ninja" };
        return new NinpathsProcessingService(
            email,
            Options.Create(nntpd),
            Options.Create(emailOptions),
            NullLogger<NinpathsProcessingService>.Instance,
            new UtcTimeProvider(new DateTimeOffset(2026, 9, 28, 1, 0, 0, TimeSpan.Zero)));
    }

    private static NinpathsCompletedFileHandler CreateHandler(NinpathsProcessingService service, string[] top1000)
    {
        var nntpd = TestHostFactory.CreateValidOptions();
        nntpd.Top1000 = top1000;
        return new NinpathsCompletedFileHandler(
            service,
            Options.Create(nntpd),
            NullLogger<NinpathsCompletedFileHandler>.Instance);
    }

    private sealed class UtcTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utc;

        public UtcTimeProvider(DateTimeOffset utc) => _utc = utc;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => _utc;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "ninpaths-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, string content)
        {
            var file = System.IO.Path.Join(Path, name);
            File.WriteAllBytes(file, Encoding.Latin1.GetBytes(content));
            return file;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException ex)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
            }
        }
    }
}
