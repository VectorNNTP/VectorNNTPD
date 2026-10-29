using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Listeners;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.Networking.Transport;

namespace VectorNNTPD.ReaderService.Tests.Networking.Listeners;

/// <summary>Connection-acceptance log format contract (TCP peer vs PROXY effective client; TLS params).</summary>
public sealed class ConnectionAcceptanceLoggingTests
{
    [Fact]
    public void LogPlainAccepted_Direct_OmitsClientSuffix()
    {
        var logger = new RecordingLogger();
        var identity = ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Parse("198.18.0.70"), 42122));
        ConnectionAcceptanceLogging.LogPlainAccepted(logger, identity);

        Assert.Single(logger.Messages);
        var message = logger.Messages.Single();
        Assert.Equal("Plain connection accepted from 198.18.0.70:42122", message);
        Assert.DoesNotContain("client ", message, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy ", message, StringComparison.Ordinal);
    }

    [Fact]
    public void LogPlainAccepted_Proxy_DistinguishesTcpPeerAndEffectiveClient()
    {
        var logger = new RecordingLogger();
        var identity = ConnectionClientIdentity.FromTrustedProxy(
            new IPEndPoint(IPAddress.Parse("198.18.0.10"), 40000),
            new IPEndPoint(IPAddress.Parse("198.18.0.70"), 42122),
            proxyProtocolVersion: 2);
        ConnectionAcceptanceLogging.LogPlainAccepted(logger, identity);

        Assert.Single(logger.Messages);
        Assert.Equal(
            "Proxy connection accepted from 198.18.0.10:40000; proxy 198.18.0.70:42122",
            logger.Messages.Single());
    }

    [Fact]
    public void LogTlsAccepted_Direct_IncludesNegotiatedParameters()
    {
        var logger = new RecordingLogger();
        var identity = ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Parse("198.18.0.70"), 40106));
        ConnectionAcceptanceLogging.LogTlsAccepted(logger, identity, "TLSv1.3", "TLS_AES_256_GCM_SHA384");

        Assert.Single(logger.Messages);
        Assert.Equal(
            "TLS connection accepted from 198.18.0.70:40106 (TlsVersion=TLSv1.3, Cipher=TLS_AES_256_GCM_SHA384)",
            logger.Messages.Single());
    }

    [Fact]
    public void LogTlsAccepted_Proxy_IncludesNegotiatedParameters()
    {
        var logger = new RecordingLogger();
        var identity = ConnectionClientIdentity.FromTrustedProxy(
            new IPEndPoint(IPAddress.Parse("198.18.0.10"), 40000),
            new IPEndPoint(IPAddress.Parse("198.18.0.70"), 40106),
            proxyProtocolVersion: 2);
        ConnectionAcceptanceLogging.LogTlsAccepted(logger, identity, "TLSv1.3", "TLS_AES_256_GCM_SHA384");

        Assert.Single(logger.Messages);
        Assert.Equal(
            "TLS/Proxy connection accepted from 198.18.0.10:40000; proxy 198.18.0.70:40106 (TlsVersion=TLSv1.3, Cipher=TLS_AES_256_GCM_SHA384)",
            logger.Messages.Single());
    }

    [Fact]
    public void FormatSslProtocol_UsesOperationalLabels()
    {
        Assert.Equal("TLSv1.3", TlsNegotiationLogging.FormatSslProtocol(SslProtocols.Tls13));
        Assert.Equal("TLSv1.2", TlsNegotiationLogging.FormatSslProtocol(SslProtocols.Tls12));
    }

    [Fact]
    public async Task PlainListener_DirectAccept_LogsRequiredFormat()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["127.0.0.1"];
        options.BindPort = TestHostFactory.GetFreeTcpPort();
        options.ProxyHosts = [];

        var recording = new RecordingLogger<NntpPlainListenerService>();
        await using var service = new NntpPlainListenerService(
            Options.Create(options),
            new TrustedProxyHosts(Options.Create(options)),
            new TlsCertificateContextProvider(NullLogger<TlsCertificateContextProvider>.Instance),
            DenyAllNntpAuthenticationProvider.Instance,
            DisabledArticleIngestionQueue.Instance,
            TransitPeerAuthorization.Disabled,
            NullLoggerFactory.Instance,
            recording);

        await service.StartAsync(CancellationToken.None);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(service.LocalEndPoints[0]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!recording.Messages.Any(m => m.StartsWith("Plain connection accepted from ", StringComparison.Ordinal))
               && !cts.IsCancellationRequested)
        {
            await Task.Delay(10, cts.Token);
        }

        var accept = Assert.Single(
            recording.Messages,
            m => m.StartsWith("Plain connection accepted from ", StringComparison.Ordinal));
        Assert.DoesNotContain("; client ", accept, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy ", accept, StringComparison.Ordinal);
        await service.StopAsync(CancellationToken.None);
    }

    private sealed class RecordingLogger : ILogger
    {
        public ConcurrentBag<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentBag<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
