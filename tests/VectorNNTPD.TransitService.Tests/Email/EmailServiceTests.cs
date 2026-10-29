using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Email;

namespace VectorNNTPD.TransitService.Tests.Email;

public sealed class EmailServiceTests
{
    [Fact]
    public async Task SendAsync_Disabled_DoesNotCreateSpoolFile()
    {
        using var harness = new EmailSpoolHarness(enabled: false);
        var result = await harness.CreateService().SendAsync(TextMessage());
        Assert.Equal(EmailEnqueueStatus.Disabled, result.Status);
        Assert.False(Directory.Exists(harness.Directory));
        Assert.Empty(harness.EmlFiles());
    }

    [Fact]
    public async Task SendAsync_Enabled_AcceptsWithoutSmtp()
    {
        using var harness = new EmailSpoolHarness();
        var result = await harness.CreateService().SendAsync(TextMessage());
        Assert.Equal(EmailEnqueueStatus.Accepted, result.Status);
        Assert.Contains("spool", result.Detail, StringComparison.Ordinal);
        Assert.Single(harness.EmlFiles());
        var bytes = await File.ReadAllBytesAsync(harness.EmlFiles()[0]);
        Assert.True(EmailSpoolRecord.TryParse(bytes, out var item));
        Assert.Equal("envelope@example.com", item!.EnvelopeSender.Address);
        Assert.NotEqual(0, item.EncodedMessage.Length);
    }

    [Fact]
    public async Task SendAsync_Injection_IsRejectedWithoutSpoolFile()
    {
        using var harness = new EmailSpoolHarness();
        var result = await harness.CreateService().SendAsync(TextMessage(subject: "x\r\nBcc: evil@example.com"));
        Assert.Equal(EmailEnqueueStatus.Rejected, result.Status);
        Assert.Empty(harness.EmlFiles());
    }

    [Fact]
    public async Task SendAsync_StoppedSpool_IsUnavailable()
    {
        using var harness = new EmailSpoolHarness();
        harness.Spool.StopAccepting();
        var result = await harness.CreateService().SendAsync(TextMessage());
        Assert.Equal(EmailEnqueueStatus.Unavailable, result.Status);
        Assert.Empty(harness.EmlFiles());
    }

    [Fact]
    public async Task SendAsync_SpoolWriteFailure_ReturnsFailed()
    {
        var blocker = Path.Join(Path.GetTempPath(), "vectornntp-email-failed-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(blocker, "x"u8.ToArray());
        try
        {
            var options = new EmailOptions
            {
                Enabled = true,
                DefaultFrom = "from@example.com",
                EnvelopeSender = "envelope@example.com",
                Smtp = new SmtpOptions { Host = "127.0.0.1", Port = 25 },
                Spool = new EmailSpoolOptions { Directory = blocker },
            };
            var spool = new FilesystemEmailSpool(Options.Create(options), NullLogger<FilesystemEmailSpool>.Instance);
            var service = new EmailService(
                spool,
                new Rfc5322MessageEncoder(),
                Options.Create(options),
                NullLogger<EmailService>.Instance);
            var result = await service.SendAsync(TextMessage());
            Assert.Equal(EmailEnqueueStatus.Failed, result.Status);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void ChannelEmailQueue_IsGone()
    {
        Assert.Null(typeof(EmailService).Assembly.GetType("VectorNNTPD.TransitService.Email.ChannelEmailQueue"));
        Assert.Null(typeof(EmailService).Assembly.GetType("VectorNNTPD.TransitService.Email.IEmailQueue"));
    }

    internal static EmailMessage TextMessage(string subject = "hello") =>
        new()
        {
            From = new EmailAddress("from@example.com"),
            To = [new EmailAddress("to@example.com")],
            Subject = subject,
            Body = "body\r\n"u8.ToArray(),
        };
}
