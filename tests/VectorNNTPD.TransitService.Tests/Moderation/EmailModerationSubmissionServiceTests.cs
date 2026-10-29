using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Email;
using VectorNNTPD.TransitService.Moderation;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Tests.Email;

namespace VectorNNTPD.TransitService.Tests.Moderation;

public sealed class EmailModerationSubmissionServiceTests
{
    [Fact]
    public async Task DisabledEmail_IsUnavailableWithoutSpoolFile()
    {
        using var harness = new EmailSpoolHarness(enabled: false);
        var service = Create(harness, enabled: false);
        var result = await service.SubmitAsync(Submission());
        Assert.Equal(ModerationSubmissionStatus.Unavailable, result.Status);
        Assert.False(Directory.Exists(harness.Directory));
        Assert.Empty(harness.EmlFiles());
    }

    [Fact]
    public async Task EnabledEmail_SpoolIsAccepted()
    {
        using var harness = new EmailSpoolHarness();
        var service = Create(harness, enabled: true);
        var result = await service.SubmitAsync(Submission());
        Assert.Equal(ModerationSubmissionStatus.Accepted, result.Status);
        Assert.Contains("spool", result.Detail, StringComparison.Ordinal);
        Assert.Single(harness.EmlFiles());
        var bytes = await File.ReadAllBytesAsync(harness.EmlFiles()[0]);
        Assert.True(EmailSpoolRecord.TryParse(bytes, out var item));
        Assert.Equal("comp-example@moderators.isc.org", item!.Recipients[0].Address);
        Assert.Equal("noreply@example.com", item.EnvelopeSender.Address);
        var encoded = System.Text.Encoding.ASCII.GetString(item.EncodedMessage.Span);
        Assert.Contains("application/news-transmission; usage=moderate", encoded, StringComparison.Ordinal);
        Assert.Contains("X-Moderated-Newsgroup: comp.example", encoded, StringComparison.Ordinal);
        Assert.Contains("Newsgroups: comp.example", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("Injection-Info:", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("Injection-Date:", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Trace:", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("Path:", encoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpoolWriteFailure_IsFailed()
    {
        var blocker = Path.Join(Path.GetTempPath(), "vectornntp-moderation-blocker-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(blocker, "x"u8.ToArray());
        try
        {
            var options = EnabledOptions();
            options.Spool.Directory = blocker;
            var spool = new FilesystemEmailSpool(Options.Create(options), NullLogger<FilesystemEmailSpool>.Instance);
            var service = new EmailModerationSubmissionService(
                new EmailService(spool, new Rfc5322MessageEncoder(), Options.Create(options), NullLogger<EmailService>.Instance),
                new ModerationEmailComposer(Options.Create(options)),
                Options.Create(options));
            Assert.Equal(ModerationSubmissionStatus.Failed, (await service.SubmitAsync(Submission())).Status);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void PercentS_Address_ExpandsInComposerDestination()
    {
        var composer = new ModerationEmailComposer(Options.Create(EnabledOptions()));
        var message = composer.Compose(Submission(moderator: "comp-example@moderators.isc.org"));
        Assert.Equal("comp-example@moderators.isc.org", message.To[0].Address);
        Assert.Equal("comp.example <ok@example.com>", message.Subject);
    }

    [Fact]
    public void Destuff_RemovesStuffingDots()
    {
        var destuffed = ModerationEmailComposer.DestuffProtoArticle("..hidden\r\nbody\r\n"u8);
        Assert.Equal(".hidden\r\nbody\r\n", System.Text.Encoding.ASCII.GetString(destuffed));
    }

    internal static EmailModerationSubmissionService Create(EmailSpoolHarness harness, bool enabled)
    {
        var options = enabled ? harness.Options : new EmailOptions();
        if (enabled)
        {
            options.DefaultFrom = "noreply@example.com";
            options.EnvelopeSender = "noreply@example.com";
        }

        var boxed = Options.Create(options);
        return new EmailModerationSubmissionService(
            new EmailService(harness.Spool, new Rfc5322MessageEncoder(), boxed, NullLogger<EmailService>.Instance),
            new ModerationEmailComposer(boxed),
            boxed);
    }

    internal static EmailOptions EnabledOptions() =>
        new()
        {
            Enabled = true,
            DefaultFrom = "noreply@example.com",
            EnvelopeSender = "noreply@example.com",
            Smtp = new SmtpOptions { Host = "127.0.0.1", Port = 25 },
            Spool = new EmailSpoolOptions
            {
                Directory = Path.Join(Path.GetTempPath(), "vectornntp-email-unused", Guid.NewGuid().ToString("N")),
            },
        };

    internal static ModerationSubmission Submission(string moderator = "comp-example@moderators.isc.org") =>
        new()
        {
            ProtoArticle = "From: poster@example.com\r\nNewsgroups: comp.example\r\nSubject: hi\r\nMessage-ID: <ok@example.com>\r\nDate: Fri, 25 Sep 2026 00:00:00 +0000\r\n\r\nbody\r\n"u8.ToArray(),
            MessageId = "<ok@example.com>",
            Newsgroups = ["comp.example"],
            TargetModeratedGroup = "comp.example",
            ModeratorAddress = moderator,
            AuthenticatedUsername = "alice",
            Sender = ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)),
        };
}
