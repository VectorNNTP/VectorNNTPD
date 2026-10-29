using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Email;
using VectorNNTPD.ReaderService.Email.Smtp;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Email;

public sealed class SmtpTransportTests
{
    [Fact]
    public async Task Plain_SendsMailRcptDataQuit()
    {
        await using var server = new FakeSmtpServer();
        var transport = Create(server.ClientOptions(SmtpSecurityMode.None));
        var result = await transport.SendAsync(Item(), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Contains(server.Commands, static c => c.StartsWith("MAIL FROM:", StringComparison.Ordinal));
        Assert.Contains(server.Commands, static c => c.StartsWith("RCPT TO:<to@example.com>", StringComparison.Ordinal));
        Assert.Contains(server.Commands, static c => c.Equals("DATA", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(server.Commands, static c => c.Equals("QUIT", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(server.LastMessage);
        Assert.Contains("Subject: hello", System.Text.Encoding.ASCII.GetString(server.LastMessage), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultipleRecipients_OneConnection()
    {
        await using var server = new FakeSmtpServer();
        var transport = Create(server.ClientOptions(SmtpSecurityMode.None));
        var item = Item();
        item = new EmailWorkItem
        {
            Message = item.Message,
            EncodedMessage = item.EncodedMessage,
            EnvelopeSender = item.EnvelopeSender,
            Recipients =
            [
                new EmailAddress("a@example.com"),
                new EmailAddress("b@example.com"),
            ],
        };
        var result = await transport.SendAsync(item, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Recipients.Count);
        Assert.Equal(1, server.Commands.Count(static c => c.StartsWith("MAIL FROM:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Recipient5xx_IsPermanent()
    {
        await using var server = new FakeSmtpServer(new FakeSmtpServerOptions { DefaultRcptCode = 550 });
        var transport = Create(server.ClientOptions(SmtpSecurityMode.None));
        var ex = await Assert.ThrowsAsync<SmtpException>(() => transport.SendAsync(Item(), CancellationToken.None));
        Assert.Equal(SmtpFailureKind.Permanent, ex.Kind);
    }

    [Fact]
    public async Task Recipient4xx_IsTransient()
    {
        await using var server = new FakeSmtpServer(new FakeSmtpServerOptions { DefaultRcptCode = 450 });
        var transport = Create(server.ClientOptions(SmtpSecurityMode.None));
        var ex = await Assert.ThrowsAsync<SmtpException>(() => transport.SendAsync(Item(), CancellationToken.None));
        Assert.Equal(SmtpFailureKind.Transient, ex.Kind);
    }

    [Fact]
    public async Task MixedRecipients_ArePartial()
    {
        var options = new FakeSmtpServerOptions { DefaultRcptCode = 250 };
        options.RcptCodes["bad@example.com"] = 550;
        await using var server = new FakeSmtpServer(options);
        var transport = Create(server.ClientOptions(SmtpSecurityMode.None));
        var item = Item();
        item = new EmailWorkItem
        {
            Message = item.Message,
            EncodedMessage = item.EncodedMessage,
            EnvelopeSender = item.EnvelopeSender,
            Recipients =
            [
                new EmailAddress("to@example.com"),
                new EmailAddress("bad@example.com"),
            ],
        };
        var ex = await Assert.ThrowsAsync<SmtpException>(() => transport.SendAsync(item, CancellationToken.None));
        Assert.Equal(SmtpFailureKind.Partial, ex.Kind);
    }

    [Fact]
    public async Task DataDotStuffing_IsApplied()
    {
        await using var server = new FakeSmtpServer();
        var transport = Create(server.ClientOptions(SmtpSecurityMode.None));
        var encoded = "From: from@example.com\r\nTo: to@example.com\r\nSubject: x\r\n\r\n.secret\r\n"u8.ToArray();
        await transport.SendAsync(Item(encoded), CancellationToken.None);
        var received = System.Text.Encoding.ASCII.GetString(server.LastMessage!);
        Assert.Contains(".secret", received, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerCloseAfterGreeting_IsNetworkFailure()
    {
        await using var server = new FakeSmtpServer(new FakeSmtpServerOptions { CloseAfterGreeting = true });
        var transport = Create(server.ClientOptions(SmtpSecurityMode.None));
        var ex = await Assert.ThrowsAsync<SmtpException>(() => transport.SendAsync(Item(), CancellationToken.None));
        Assert.Equal(SmtpFailureKind.Network, ex.Kind);
    }

    [Fact]
    public async Task OversizedMessage_UsesSizeLimit()
    {
        await using var server = new FakeSmtpServer(new FakeSmtpServerOptions { Size = 16 });
        var transport = Create(server.ClientOptions(SmtpSecurityMode.None));
        var ex = await Assert.ThrowsAsync<SmtpException>(() => transport.SendAsync(Item(), CancellationToken.None));
        Assert.Equal(SmtpFailureKind.Message, ex.Kind);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        await using var server = new FakeSmtpServer();
        var transport = Create(server.ClientOptions(SmtpSecurityMode.None));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.SendAsync(Item(), cts.Token));
    }

    [Fact]
    public async Task MultilineEhlo_IsConsumed()
    {
        await using var server = new FakeSmtpServer();
        var transport = Create(server.ClientOptions(SmtpSecurityMode.None));
        await transport.SendAsync(Item(), CancellationToken.None);
        Assert.Contains(server.Commands, static c => c.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ehlo_UsesApplicationFqdn()
    {
        await using var server = new FakeSmtpServer();
        var transport = Create(server.ClientOptions(SmtpSecurityMode.None));
        await transport.SendAsync(Item(), CancellationToken.None);
        Assert.Contains(
            server.Commands,
            static c => c.Equals("EHLO nntpd01.usenet.ninja", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            server.Commands,
            static c => c.Contains("localhost", StringComparison.OrdinalIgnoreCase));
    }

    internal static SmtpTransport Create(
        SmtpOptions smtp,
        IReadOnlyCollection<X509Certificate2>? extraTrustedRoots = null,
        ILogger<SmtpTransport>? logger = null)
    {
        var options = new EmailOptions
        {
            Enabled = true,
            DefaultFrom = "from@example.com",
            Smtp = smtp,
        };
        return new SmtpTransport(
            Options.Create(options),
            Options.Create(new NntpdOptions { ServerId = 1, DnsSuffix = "usenet.ninja" }),
            logger ?? NullLogger<SmtpTransport>.Instance,
            extraTrustedRoots);
    }

    internal static EmailWorkItem Item(ReadOnlyMemory<byte>? encoded = null)
    {
        var message = EmailServiceTests.TextMessage();
        var bytes = encoded ?? new Rfc5322MessageEncoder().Encode(message);
        return new EmailWorkItem
        {
            Message = message,
            EncodedMessage = bytes,
            EnvelopeSender = new EmailAddress("envelope@example.com"),
            Recipients = [new EmailAddress("to@example.com")],
        };
    }
}
