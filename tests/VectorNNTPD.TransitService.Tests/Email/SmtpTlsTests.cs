using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Email.Smtp;
using VectorNNTPD.TransitService.Tests.TestDoubles;

namespace VectorNNTPD.TransitService.Tests.Email;

public sealed class SmtpTlsTests
{
    [Fact]
    public async Task ImplicitTls_ValidCertificate_SucceedsWithoutPlaintext()
    {
        var certificate = FakeSmtpServer.CreateSelfSignedCertificate();
        Assert.True(certificate.HasPrivateKey);
        await using var server = new FakeSmtpServer(new FakeSmtpServerOptions
        {
            ImplicitTls = true,
            Certificate = certificate,
        });
        var transport = SmtpTransportTests.Create(
            server.ClientOptions(SmtpSecurityMode.ImplicitTls),
            FakeSmtpServer.TrustAnchors(certificate));
        var result = await transport.SendAsync(SmtpTransportTests.Item(), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.True(server.StartedTls);
        Assert.False(server.SawPlaintextSmtpBeforeTls);
    }

    [Fact]
    public async Task ImplicitTls_UntrustedCertificate_Fails()
    {
        var certificate = FakeSmtpServer.CreateSelfSignedCertificate();
        await using var server = new FakeSmtpServer(new FakeSmtpServerOptions
        {
            ImplicitTls = true,
            Certificate = certificate,
        });
        var transport = SmtpTransportTests.Create(server.ClientOptions(SmtpSecurityMode.ImplicitTls));
        var ex = await Assert.ThrowsAsync<SmtpException>(
            () => transport.SendAsync(SmtpTransportTests.Item(), CancellationToken.None));
        Assert.Equal(SmtpFailureKind.Tls, ex.Kind);
    }

    [Fact]
    public async Task ImplicitTls_HostnameMismatch_Fails()
    {
        var certificate = FakeSmtpServer.CreateSelfSignedCertificate("wrong.example", includeLoopbackIp: false);
        await using var server = new FakeSmtpServer(new FakeSmtpServerOptions
        {
            ImplicitTls = true,
            Certificate = certificate,
        });
        var transport = SmtpTransportTests.Create(
            server.ClientOptions(SmtpSecurityMode.ImplicitTls),
            FakeSmtpServer.TrustAnchors(certificate));
        var ex = await Assert.ThrowsAsync<SmtpException>(
            () => transport.SendAsync(SmtpTransportTests.Item(), CancellationToken.None));
        Assert.Equal(SmtpFailureKind.Tls, ex.Kind);
    }

    [Fact]
    public async Task StartTls_SecondEhloAndNoPlaintextAuth()
    {
        var certificate = FakeSmtpServer.CreateSelfSignedCertificate();
        await using var server = new FakeSmtpServer(new FakeSmtpServerOptions
        {
            AdvertiseStartTls = true,
            AdvertiseAuthPlain = true,
            ExpectedUsername = "user",
            ExpectedPassword = "secret",
            Certificate = certificate,
        });
        var smtp = server.ClientOptions(SmtpSecurityMode.StartTls, "user", "secret");
        var transport = SmtpTransportTests.Create(smtp, FakeSmtpServer.TrustAnchors(certificate));
        await transport.SendAsync(SmtpTransportTests.Item(), CancellationToken.None);
        Assert.True(server.StartedTls);
        Assert.True(server.Authenticated);
        var commands = server.Commands.ToList();
        var ehlo = commands.Count(static c => c.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase));
        Assert.True(ehlo >= 2);
        var startTlsIndex = commands.FindIndex(static c => c.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase));
        var authIndex = commands.FindIndex(static c => c.Equals("AUTH", StringComparison.OrdinalIgnoreCase));
        Assert.True(startTlsIndex >= 0);
        Assert.True(authIndex > startTlsIndex);
    }

    [Fact]
    public async Task StartTls_Required_FailsWhenNotAdvertised()
    {
        await using var server = new FakeSmtpServer(new FakeSmtpServerOptions { AdvertiseStartTls = false });
        var transport = SmtpTransportTests.Create(server.ClientOptions(SmtpSecurityMode.StartTls));
        var ex = await Assert.ThrowsAsync<SmtpException>(
            () => transport.SendAsync(SmtpTransportTests.Item(), CancellationToken.None));
        Assert.Equal(SmtpFailureKind.Tls, ex.Kind);
        Assert.DoesNotContain(server.Commands, static c => c.StartsWith("MAIL FROM:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartTls_DoesNotDowngradeToPlaintext()
    {
        await using var server = new FakeSmtpServer();
        var smtp = server.ClientOptions(SmtpSecurityMode.StartTls);
        var transport = SmtpTransportTests.Create(smtp);
        await Assert.ThrowsAsync<SmtpException>(() => transport.SendAsync(SmtpTransportTests.Item(), CancellationToken.None));
        Assert.False(server.StartedTls);
        Assert.DoesNotContain(server.Commands, static c => c.StartsWith("MAIL FROM:", StringComparison.Ordinal));
    }

    [Fact]
    public void SmtpOptions_HasNoCertificateBypassOrEhloHostname()
    {
        Assert.Null(typeof(SmtpOptions).GetProperty("DangerousAcceptAnyServerCertificate"));
        Assert.Null(typeof(SmtpOptions).GetProperty("EhloHostname"));
        Assert.Null(typeof(EmailOptions).GetProperty("Queue"));
        Assert.NotNull(typeof(EmailOptions).GetProperty(nameof(EmailOptions.Spool)));
    }
}
