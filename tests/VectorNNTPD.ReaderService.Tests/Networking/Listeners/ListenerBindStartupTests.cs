using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.ReaderService.Hosting;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Listeners;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.Hosting.Systemd;
using VectorNNTPD.Common.Networking.Listeners;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Networking.Listeners;

/// <summary>
/// Bind-failure startup contract: all-or-nothing listener start, cleanup, and lifecycle propagation.
/// </summary>
public sealed class ListenerBindStartupTests
{
    [Fact]
    public async Task PlainStartAsync_FirstBindSucceedsSecondFails_DisposesFirstAndFails()
    {
        var bindError = AddressInUse();
        var binder = new ScriptedListenSocketBinder(null, bindError);
        var logger = new CapturingLogger<NntpPlainListenerService>();
        var options = MultiEndpointPlainOptions();
        await using var service = CreatePlain(options, binder, logger);

        var ex = await Assert.ThrowsAsync<SocketException>(() => service.StartAsync(CancellationToken.None));

        Assert.Same(bindError, ex);
        Assert.Equal(2, binder.BindAttempts);
        Assert.Empty(service.LocalEndPoints);
        Assert.False(service.HasActiveAcceptLoops);
        Assert.Null(service.Execution);
        Assert.All(binder.Sockets, static socket => Assert.True(socket.SafeHandle.IsClosed));
        Assert.Contains(
            logger.Records,
            r => r.Level == LogLevel.Error
                 && r.Exception == bindError
                 && r.Message.Contains("Failed to bind Plain NNTP listener", StringComparison.Ordinal)
                 && r.Message.Contains("listener startup cannot continue", StringComparison.Ordinal)
                 && r.Message.Contains(options.BindPort.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlainStartAsync_FirstBindFailsImmediately_FailsWithoutAcceptLoop()
    {
        var bindError = AddressInUse();
        var binder = new ScriptedListenSocketBinder(bindError);
        await using var service = CreatePlain(MultiEndpointPlainOptions(), binder);

        var ex = await Assert.ThrowsAsync<SocketException>(() => service.StartAsync(CancellationToken.None));

        Assert.Same(bindError, ex);
        Assert.Equal(1, binder.BindAttempts);
        Assert.Empty(service.LocalEndPoints);
        Assert.False(service.HasActiveAcceptLoops);
        Assert.Null(service.Execution);
        Assert.All(binder.Sockets, static socket => Assert.True(socket.SafeHandle.IsClosed));
    }

    [Fact]
    public async Task TlsStartAsync_FirstBindSucceedsSecondFails_DisposesFirstAndFails()
    {
        var bindError = AddressInUse();
        var binder = new ScriptedListenSocketBinder(null, bindError);
        var logger = new CapturingLogger<NntpTlsListenerService>();
        var options = MultiEndpointTlsOptions();
        await using var service = CreateTls(options, binder, logger);

        var ex = await Assert.ThrowsAsync<SocketException>(() => service.StartAsync(CancellationToken.None));

        Assert.Same(bindError, ex);
        Assert.Equal(2, binder.BindAttempts);
        Assert.False(service.ListenersBound);
        Assert.Empty(service.LocalEndPoints);
        Assert.False(service.HasActiveAcceptLoops);
        Assert.Null(service.Execution);
        Assert.All(binder.Sockets, static socket => Assert.True(socket.SafeHandle.IsClosed));
        Assert.Contains(
            logger.Records,
            r => r.Level == LogLevel.Error
                 && r.Exception == bindError
                 && r.Message.Contains("Failed to bind TLS NNTP listener", StringComparison.Ordinal)
                 && r.Message.Contains("listener startup cannot continue", StringComparison.Ordinal)
                 && r.Message.Contains(options.BindPortTls.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task TlsStartAsync_FirstBindFailsImmediately_FailsWithoutAcceptLoop()
    {
        var bindError = AddressInUse();
        var binder = new ScriptedListenSocketBinder(bindError);
        await using var service = CreateTls(MultiEndpointTlsOptions(), binder);

        var ex = await Assert.ThrowsAsync<SocketException>(() => service.StartAsync(CancellationToken.None));

        Assert.Same(bindError, ex);
        Assert.Equal(1, binder.BindAttempts);
        Assert.False(service.ListenersBound);
        Assert.False(service.HasActiveAcceptLoops);
        Assert.Null(service.Execution);
        Assert.All(binder.Sockets, static socket => Assert.True(socket.SafeHandle.IsClosed));
    }

    [Fact]
    public async Task PlainBindFailure_PropagatesToApplicationServiceManager()
    {
        var bindError = AddressInUse();
        var binder = new ScriptedListenSocketBinder(bindError);
        var options = MultiEndpointPlainOptions();
        await using var service = CreatePlain(options, binder);
        var managerLogger = new CapturingLogger<ApplicationServiceManager>();
        var manager = new ApplicationServiceManager(
            [service],
            options,
            managerLogger);

        var ex = await Assert.ThrowsAsync<SocketException>(() => manager.StartAsync(CancellationToken.None));

        Assert.Same(bindError, ex);
        Assert.Empty(manager.StartedServices);
        Assert.DoesNotContain(
            managerLogger.Records,
            r => r.Message.Contains("All application services started successfully", StringComparison.Ordinal));
        Assert.DoesNotContain(
            managerLogger.Records,
            r => r.Message.Contains("Application service NntpPlainListener started", StringComparison.Ordinal));
        Assert.Contains(
            managerLogger.Records,
            r => r.Level == LogLevel.Error
                 && r.Message.Contains("NntpPlainListener failed during startup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TlsBindFailure_PropagatesToApplicationServiceManager()
    {
        var bindError = AddressInUse();
        var binder = new ScriptedListenSocketBinder(bindError);
        var options = MultiEndpointTlsOptions();
        await using var service = CreateTls(options, binder);
        var managerLogger = new CapturingLogger<ApplicationServiceManager>();
        var manager = new ApplicationServiceManager(
            [service],
            options,
            managerLogger);

        var ex = await Assert.ThrowsAsync<SocketException>(() => manager.StartAsync(CancellationToken.None));

        Assert.Same(bindError, ex);
        Assert.Empty(manager.StartedServices);
        Assert.DoesNotContain(
            managerLogger.Records,
            r => r.Message.Contains("All application services started successfully", StringComparison.Ordinal));
        Assert.Contains(
            managerLogger.Records,
            r => r.Level == LogLevel.Error
                 && r.Message.Contains("NntpTlsListener failed during startup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListenerBindFailure_DoesNotReachRunning_AndOmitsSuccessLifecycleLogs()
    {
        var bindError = AddressInUse();
        var binder = new ScriptedListenSocketBinder(null, bindError);
        var options = MultiEndpointPlainOptions();
        await using var service = CreatePlain(options, binder);
        var managerLogger = new CapturingLogger<ApplicationServiceManager>();
        var lifecycleLogger = new CapturingLogger<ApplicationLifecycle>();
        var hostedLogger = new CapturingLogger<NntpdHostedService>();
        var manager = new ApplicationServiceManager(
            [service],
            options,
            managerLogger);
        await using var lifecycle = new ApplicationLifecycle(
            manager,
            options,
            lifecycleLogger);
        var hosted = new NntpdHostedService(
            lifecycle,
            new NntpdHostLifetime(
                lifecycle,
                new TestHostApplicationLifetime(),
                Options.Create(options),
                NullLogger<NntpdHostLifetime>.Instance),
            Options.Create(options),
            hostedLogger);

        var ex = await Assert.ThrowsAsync<SocketException>(() => hosted.StartAsync(CancellationToken.None));

        Assert.Same(bindError, ex);
        Assert.NotEqual(ApplicationState.Running, lifecycle.State);
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Empty(manager.StartedServices);
        Assert.False(service.HasActiveAcceptLoops);
        Assert.DoesNotContain(
            managerLogger.Records,
            r => r.Message.Contains("All application services started successfully", StringComparison.Ordinal));
        Assert.DoesNotContain(
            lifecycleLogger.Records,
            r => r.Message.Contains("Starting -> Running", StringComparison.Ordinal));
        Assert.DoesNotContain(
            lifecycleLogger.Records,
            r => r.Message.Contains("Application initialization completed", StringComparison.Ordinal));
        Assert.DoesNotContain(
            hostedLogger.Records,
            r => r.Message.Contains("Application entered Running", StringComparison.Ordinal)
                 || r.Message.Contains("Application started", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlainStartAsync_SuccessfulBind_StartsAllEndpoints()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["127.0.0.1"];
        options.BindPort = TestHostFactory.GetFreeTcpPort();
        options.BindPortTls = 0;
        await using var service = CreatePlain(options, listenBinder: null);

        await service.StartAsync(CancellationToken.None);

        Assert.NotEmpty(service.LocalEndPoints);
        Assert.Equal(options.BindPort, service.LocalEndPoints[0].Port);
        Assert.NotNull(service.Execution);
        Assert.False(service.Execution!.IsCompleted);
        Assert.True(service.HasActiveAcceptLoops);
        await service.StopAsync(CancellationToken.None);
        Assert.False(service.HasActiveAcceptLoops);
    }

    [Fact]
    public async Task PlainStartAsync_SuccessfulBind_UsesNntpdSectionBindSettings()
    {
        var port = TestHostFactory.GetFreeTcpPort();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{NntpdOptions.SectionName}:BindAddress:0"] = "127.0.0.1",
                    [$"{NntpdOptions.SectionName}:BindPort"] = port.ToString(),
                    [$"{NntpdOptions.SectionName}:BindPortTls"] = "0",
                    ["BindAddress:0"] = "203.0.113.10",
                    ["BindPort"] = "2119",
                    ["BindPortTls"] = "2563",
                })
            .Build();

        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = [];
        configuration.GetSection(NntpdOptions.SectionName).Bind(options);
        NntpdAcmeCloudflareOptionsOverlay.OverlaySharedFromRootPreservingApplicationAcme(
            options,
            configuration);

        await using var service = CreatePlain(options, listenBinder: null);
        await service.StartAsync(CancellationToken.None);

        Assert.Equal(["127.0.0.1"], options.BindAddress);
        Assert.Equal(port, options.BindPort);
        Assert.Equal(0, options.BindPortTls);
        Assert.NotEmpty(service.LocalEndPoints);
        Assert.Equal(port, service.LocalEndPoints[0].Port);
        Assert.Equal(IPAddress.Loopback, service.LocalEndPoints[0].Address);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SuccessfulStartup_ReachesRunning_AndKeepsSuccessLogs()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["127.0.0.1"];
        options.BindPort = TestHostFactory.GetFreeTcpPort();
        options.BindPortTls = 0;
        await using var service = CreatePlain(options, listenBinder: null);
        var managerLogger = new CapturingLogger<ApplicationServiceManager>();
        var lifecycleLogger = new CapturingLogger<ApplicationLifecycle>();
        var manager = new ApplicationServiceManager(
            [service],
            options,
            managerLogger);
        await using var lifecycle = new ApplicationLifecycle(
            manager,
            options,
            lifecycleLogger);

        await lifecycle.StartAsync(CancellationToken.None);

        Assert.Equal(ApplicationState.Running, lifecycle.State);
        Assert.Contains(manager.StartedServices, s => ReferenceEquals(s, service));
        Assert.Contains(
            managerLogger.Records,
            r => r.Message.Contains("All application services started successfully", StringComparison.Ordinal));
        Assert.Contains(
            lifecycleLogger.Records,
            r => r.Message.Contains("Starting -> Running", StringComparison.Ordinal));
        await lifecycle.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_DoesNotFail_WhenAcceptFailsAfterSuccessfulBind()
    {
        var binder = new ScriptedListenSocketBinder([null]);
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["127.0.0.1"];
        options.BindPort = 23456;
        await using var service = CreatePlain(options, binder);

        await service.StartAsync(CancellationToken.None);

        Assert.NotNull(service.Execution);
        Assert.False(service.Execution!.IsFaulted);
        Assert.False(service.Execution.IsCompleted);
        Assert.NotEmpty(service.LocalEndPoints);
        Assert.True(service.HasActiveAcceptLoops);

        foreach (var socket in binder.Sockets)
        {
            socket.Dispose();
        }

        Assert.False(service.Execution.IsFaulted);
        Assert.False(service.Execution.IsCompleted);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SocketAcceptListener_Start_PropagatesBindFailure_BeforeAcceptLoop()
    {
        var bindError = AddressInUse();
        var binder = new ScriptedListenSocketBinder(bindError);
        await using var listener = new SocketAcceptListener(
            new ListenBinding(IPAddress.Loopback, 23456, DualMode: false),
            static (_, _) => ValueTask.CompletedTask,
            NullLogger<SocketAcceptListener>.Instance,
            binder);

        var ex = Assert.Throws<SocketException>(() => listener.Start());

        Assert.Same(bindError, ex);
        Assert.False(listener.AcceptLoopActive);
    }

    private static SocketException AddressInUse() =>
        new((int)SocketError.AddressAlreadyInUse);

    private static NntpdOptions MultiEndpointPlainOptions()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["127.0.0.1", "::1"];
        options.BindPort = 23456;
        options.BindPortTls = 0;
        return options;
    }

    private static NntpdOptions MultiEndpointTlsOptions()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["127.0.0.1", "::1"];
        options.BindPort = TestHostFactory.GetFreeTcpPort();
        options.BindPortTls = 23457;
        return options;
    }

    [Fact]
    public async Task Plain_bind_failure_with_tls_disabled_does_not_publish()
    {
        var options = MultiEndpointPlainOptions();
        options.BindPortTls = 0;
        options.BindAddress = [TestHostFactory.TestIpv4.ToString(), TestHostFactory.TestIpv6.ToString()];
        var client = new FakeCloudflareDnsClient();
        client.Seed(new CloudflareDnsRecord
        {
            Id = "healthy-a",
            Type = "A",
            Name = options.Fqdn,
            Content = "198.18.0.10",
            Ttl = 300,
            Proxied = false,
        });
        var publication = CreatePublication(options, client);
        await publication.StartAsync(CancellationToken.None);
        await using var service = CreatePlain(options, new ScriptedListenSocketBinder(AddressInUse()), publication: publication);

        await Assert.ThrowsAsync<SocketException>(() => service.StartAsync(CancellationToken.None));
        await publication.StopAsync(CancellationToken.None);

        Assert.Equal(0, client.CreateCallCount);
        Assert.Equal(0, client.DeleteCallCount);
        Assert.Contains(client.Snapshot(), static record => record.Id == "healthy-a");
    }

    [Fact]
    public async Task Tls_bind_failure_does_not_publish()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = [TestHostFactory.TestIpv4.ToString()];
        options.BindPortTls = 563;
        var client = new FakeCloudflareDnsClient();
        client.Seed(new CloudflareDnsRecord
        {
            Id = "healthy-a",
            Type = "A",
            Name = options.Fqdn,
            Content = "198.18.0.10",
            Ttl = 300,
            Proxied = false,
        });
        var publication = CreatePublication(options, client);
        await publication.StartAsync(CancellationToken.None);
        await using var service = CreateTls(options, new ScriptedListenSocketBinder(AddressInUse()), publication: publication);

        await Assert.ThrowsAsync<SocketException>(() => service.StartAsync(CancellationToken.None));
        await publication.StopAsync(CancellationToken.None);

        Assert.Equal(0, client.CreateCallCount);
        Assert.Equal(0, client.DeleteCallCount);
        Assert.Contains(client.Snapshot(), static record => record.Id == "healthy-a");
    }

    private static NntpPlainListenerService CreatePlain(
        NntpdOptions options,
        IListenSocketBinder? listenBinder,
        ILogger<NntpPlainListenerService>? logger = null,
        CloudflareDnsReconciliationService? publication = null)
    {
        return new NntpPlainListenerService(
            Options.Create(options),
            new TrustedProxyHosts(Options.Create(options)),
            new TlsCertificateContextProvider(NullLogger<TlsCertificateContextProvider>.Instance),
            DenyAllNntpAuthenticationProvider.Instance,
            DisabledArticleIngestionQueue.Instance,
            TransitPeerAuthorization.Disabled,
            NullLoggerFactory.Instance,
            logger ?? NullLogger<NntpPlainListenerService>.Instance,
            listenBinder: listenBinder,
            publication: publication);
    }

    private static NntpTlsListenerService CreateTls(
        NntpdOptions options,
        IListenSocketBinder listenBinder,
        ILogger<NntpTlsListenerService>? logger = null,
        CloudflareDnsReconciliationService? publication = null)
    {
        return new NntpTlsListenerService(
            Options.Create(options),
            new AvailableTlsCertificateProvider(),
            new TrustedProxyHosts(Options.Create(options)),
            DenyAllNntpAuthenticationProvider.Instance,
            DisabledArticleIngestionQueue.Instance,
            TransitPeerAuthorization.Disabled,
            NullLoggerFactory.Instance,
            logger ?? NullLogger<NntpTlsListenerService>.Instance,
            listenBinder: listenBinder,
            publication: publication);
    }

    private static CloudflareDnsReconciliationService CreatePublication(NntpdOptions options, FakeCloudflareDnsClient client)
    {
        var resolver = new BindAddressResolver(
            new FakeLocalIpAddressAssignee(TestHostFactory.TestIpv4, TestHostFactory.TestIpv6),
            NullLogger<BindAddressResolver>.Instance);
        var reconciler = new CloudflareDnsReconciler(
            client,
            Options.Create<AcmeCloudflareOptions>(options),
            NullLogger<CloudflareDnsReconciler>.Instance);
        return new CloudflareDnsReconciliationService(
            Options.Create<AcmeCloudflareOptions>(options),
            resolver,
            reconciler,
            NullLogger<CloudflareDnsReconciliationService>.Instance,
            new ListenerBoundDnsPublicationLease());
    }

    private sealed class ScriptedListenSocketBinder : IListenSocketBinder
    {
        private readonly Exception?[] _outcomes;

        public ScriptedListenSocketBinder(params Exception?[] outcomes)
        {
            _outcomes = outcomes;
        }

        public List<Socket> Sockets { get; } = [];

        public int BindAttempts { get; private set; }

        public void BindAndListen(Socket socket, IPEndPoint endpoint, int backlog)
        {
            Sockets.Add(socket);
            var index = BindAttempts++;
            var outcome = index < _outcomes.Length ? _outcomes[index] : null;
            if (outcome is not null)
            {
                throw outcome;
            }

            // Bind an ephemeral loopback port so AcceptAsync parks instead of
            // throwing synchronously (which would spin Start() on the caller).
            var address = endpoint.AddressFamily == AddressFamily.InterNetwork
                ? IPAddress.Loopback
                : IPAddress.IPv6Loopback;
            socket.Bind(new IPEndPoint(address, 0));
            socket.Listen(backlog);
        }
    }

    private sealed class AvailableTlsCertificateProvider : ITlsCertificateContextProvider
    {
        public bool IsAvailable => true;

        public TlsCertificateLease Acquire() =>
            throw new InvalidOperationException("Bind-startup tests do not perform TLS handshakes.");

        public void PublishFromPfx(ReadOnlySpan<byte> pfxBytes, string password)
        {
        }
    }

    private sealed record CapturedLog(LogLevel Level, EventId EventId, string Message, Exception? Exception);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentBag<CapturedLog> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Records.Add(new CapturedLog(logLevel, eventId, formatter(state, exception), exception));
        }
    }
}
