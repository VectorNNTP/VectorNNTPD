using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Email;
using VectorNNTPD.ReaderService.Email.Smtp;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Email;

public sealed class SmtpAuthTests
{
    [Fact]
    public async Task AuthPlain_SucceedsAfterTls()
    {
        await using var server = new FakeSmtpServer(new FakeSmtpServerOptions
        {
            AdvertiseStartTls = true,
            AdvertiseAuthPlain = true,
            ExpectedUsername = "user",
            ExpectedPassword = "s3cret",
            Certificate = FakeSmtpServer.CreateSelfSignedCertificate(),
        });
        var logger = new MemoryLogger<SmtpTransport>();
        var smtp = server.ClientOptions(SmtpSecurityMode.StartTls, "user", "s3cret");
        var transport = SmtpTransportTests.Create(
            smtp,
            FakeSmtpServer.TrustAnchors(server.Certificate!),
            logger);
        await transport.SendAsync(SmtpTransportTests.Item(), CancellationToken.None);
        Assert.True(server.Authenticated);
        Assert.DoesNotContain(server.RawLines, static l => l.Contains("s3cret", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, static m => m.Contains("s3cret", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, static m => m.Contains("user", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Commands, static c => c.Contains("PLAIN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AuthLogin_SucceedsWhenPlainMissing()
    {
        await using var server = new FakeSmtpServer(new FakeSmtpServerOptions
        {
            AdvertiseStartTls = true,
            AdvertiseAuthLogin = true,
            ExpectedUsername = "user",
            ExpectedPassword = "s3cret",
            Certificate = FakeSmtpServer.CreateSelfSignedCertificate(),
        });
        var smtp = server.ClientOptions(SmtpSecurityMode.StartTls, "user", "s3cret");
        var transport = SmtpTransportTests.Create(smtp, FakeSmtpServer.TrustAnchors(server.Certificate!));
        await transport.SendAsync(SmtpTransportTests.Item(), CancellationToken.None);
        Assert.True(server.Authenticated);
        Assert.DoesNotContain(server.RawLines, static l => l.Contains("s3cret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthFailure_IsNotRetriedByTransport()
    {
        await using var server = new FakeSmtpServer(new FakeSmtpServerOptions
        {
            AdvertiseStartTls = true,
            AdvertiseAuthPlain = true,
            ExpectedUsername = "user",
            ExpectedPassword = "right",
            Certificate = FakeSmtpServer.CreateSelfSignedCertificate(),
        });
        var smtp = server.ClientOptions(SmtpSecurityMode.StartTls, "user", "wrong");
        var transport = SmtpTransportTests.Create(smtp, FakeSmtpServer.TrustAnchors(server.Certificate!));
        var ex = await Assert.ThrowsAsync<SmtpException>(
            () => transport.SendAsync(SmtpTransportTests.Item(), CancellationToken.None));
        Assert.Equal(SmtpFailureKind.Authentication, ex.Kind);
    }

    [Fact]
    public async Task Auth_NotAttemptedBeforeTlsWhenRequired()
    {
        await using var server = new FakeSmtpServer(new FakeSmtpServerOptions
        {
            AdvertiseStartTls = true,
            AdvertiseAuthPlain = true,
            ExpectedUsername = "user",
            ExpectedPassword = "s3cret",
            Certificate = FakeSmtpServer.CreateSelfSignedCertificate(),
        });
        var smtp = server.ClientOptions(SmtpSecurityMode.StartTls, "user", "s3cret");
        var transport = SmtpTransportTests.Create(smtp, FakeSmtpServer.TrustAnchors(server.Certificate!));
        await transport.SendAsync(SmtpTransportTests.Item(), CancellationToken.None);
        var commands = server.Commands.ToList();
        var startTls = commands.FindIndex(static c => c.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase));
        var auth = commands.FindIndex(static c => c.Equals("AUTH", StringComparison.OrdinalIgnoreCase));
        Assert.True(auth > startTls);
    }

    [Fact]
    public async Task Credentials_AreNotCopiedIntoHeaders()
    {
        var message = EmailServiceTests.TextMessage();
        var encoded = System.Text.Encoding.ASCII.GetString(new Rfc5322MessageEncoder().Encode(message).Span);
        Assert.DoesNotContain("AUTH", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", encoded, StringComparison.Ordinal);
    }
}

internal sealed class MemoryLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
