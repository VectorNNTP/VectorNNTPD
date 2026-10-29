using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Session.Commands;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Tests.Session;

[Collection(nameof(NntpCommandLoggerCollection))]
public sealed class NntpAuthinfoTests
{
    [Fact]
    public async Task AuthinfoUser_WithoutArgument_Returns501()
    {
        await using var duplex = await AuthinfoDuplex.CreateAsync(CreateAcceptingProvider());
        var session = duplex.CreateSession();
        var sessionTask = session.RunAsync();
        await duplex.ReadGreetingAsync();

        await duplex.WriteClientLineAsync("AUTHINFO USER");
        Assert.StartsWith("501 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.False(session.Authentication.IsAuthenticated);
        Assert.Null(session.PendingAuthUsername);

        await duplex.WriteClientLineAsync("QUIT");
        await duplex.ReadClientLineAsync();
        await sessionTask;
    }

    [Fact]
    public async Task CleartextAllowed_AuthinfoUserPass_SucceedsWithoutTls()
    {
        await using var duplex = await AuthinfoDuplex.CreateAsync(CreateAcceptingProvider());
        var session = duplex.CreateSession(allowCleartextAuth: true);
        var sessionTask = session.RunAsync();
        await duplex.ReadGreetingAsync();

        await duplex.WriteClientLineAsync("AUTHINFO USER fred");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.Equal("fred", session.PendingAuthUsername);
        Assert.False(session.Authentication.IsAuthenticated);

        await duplex.WriteClientLineAsync("AUTHINFO PASS flintstone");
        Assert.StartsWith("281 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.True(session.Authentication.IsAuthenticated);
        Assert.Equal("fred", session.Authentication.Username);

        await duplex.WriteClientLineAsync("QUIT");
        await duplex.ReadClientLineAsync();
        await sessionTask;
    }

    [Fact]
    public async Task AuthinfoUser_Repeated_ReplacesPendingUsername()
    {
        await using var duplex = await AuthinfoDuplex.CreateAsync(CreateAcceptingProvider());
        var session = duplex.CreateSession();
        var sessionTask = session.RunAsync();
        await duplex.ReadGreetingAsync();

        await duplex.WriteClientLineAsync("AUTHINFO USER fred");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO USER barney");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.Equal("barney", session.PendingAuthUsername);
        Assert.False(session.Authentication.IsAuthenticated);

        await duplex.WriteClientLineAsync("QUIT");
        await duplex.ReadClientLineAsync();
        await sessionTask;
    }

    [Fact]
    public async Task AuthinfoPass_WithoutUser_Returns482()
    {
        await using var duplex = await AuthinfoDuplex.CreateAsync(CreateAcceptingProvider());
        var session = duplex.CreateSession();
        var sessionTask = session.RunAsync();
        await duplex.ReadGreetingAsync();

        await duplex.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.StartsWith("482 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.False(session.Authentication.IsAuthenticated);

        await duplex.WriteClientLineAsync("QUIT");
        await duplex.ReadClientLineAsync();
        await sessionTask;
    }

    [Fact]
    public async Task AuthinfoPass_WrongPassword_Returns481_LeavesUnauthenticated()
    {
        await using var duplex = await AuthinfoDuplex.CreateAsync(CreateAcceptingProvider());
        var session = duplex.CreateSession();
        var sessionTask = session.RunAsync();
        await duplex.ReadGreetingAsync();

        await duplex.WriteClientLineAsync("AUTHINFO USER fred");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS wrong");
        Assert.StartsWith("481 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.False(session.Authentication.IsAuthenticated);
        Assert.False(session.Authorization.IsAuthenticated);
        Assert.False(session.Authorization.AuthorizedReader);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.False(session.Authorization.PostingPermitted);
        Assert.False(session.Authorization.StreamingPermitted);
        Assert.Equal("fred", session.PendingAuthUsername);

        await duplex.WriteClientLineAsync("QUIT");
        await duplex.ReadClientLineAsync();
        await sessionTask;
    }

    [Fact]
    public async Task AuthinfoPass_Success_AuthenticatedWithoutImpliedPrivileges()
    {
        var provider = new ScriptedAuthenticationProvider(
            (user, pass) => user == "fred" && pass == "flintstone"
                ? NntpAuthenticationResult.Success(
                    "fred",
                    new NntpAuthorization(
                        isAuthenticated: true,
                        authorizedReader: false,
                        authorizedTransit: false,
                        postingPermitted: false,
                        streamingPermitted: false))
                : NntpAuthenticationResult.Failed);

        await using var duplex = await AuthinfoDuplex.CreateAsync(provider);
        var session = duplex.CreateSession();
        var sessionTask = session.RunAsync();
        await duplex.ReadGreetingAsync();

        await duplex.WriteClientLineAsync("AUTHINFO USER fred");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS flintstone");
        Assert.StartsWith("281 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        Assert.True(session.Authentication.IsAuthenticated);
        Assert.Equal("fred", session.Authentication.Username);
        Assert.Null(session.PendingAuthUsername);
        Assert.True(session.Authorization.IsAuthenticated);
        Assert.False(session.Authorization.AuthorizedReader);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.False(session.Authorization.PostingPermitted);
        Assert.False(session.Authorization.StreamingPermitted);
        Assert.False(session.Authorization.ControlCancelPermitted);

        await duplex.WriteClientLineAsync("QUIT");
        await duplex.ReadClientLineAsync();
        await sessionTask;
    }

    [Fact]
    public async Task AuthinfoPass_Newsmaster_GrantsControlCancelOnly()
    {
        var options = new NntpdOptions
        {
            NewsmasterUser = "newsmaster",
            NewsmasterPassword = "unit-test-newsmaster-password",
        };
        var provider = NewsmasterNntpAuthenticationProvider.Create(options);

        await using var duplex = await AuthinfoDuplex.CreateAsync(provider);
        var session = duplex.CreateSession();
        var sessionTask = session.RunAsync();
        await duplex.ReadGreetingAsync();

        await duplex.WriteClientLineAsync("AUTHINFO USER newsmaster");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS unit-test-newsmaster-password");
        Assert.StartsWith("281 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        Assert.True(session.Authorization.IsAuthenticated);
        Assert.True(session.Authorization.PostingPermitted);
        Assert.True(session.Authorization.ControlCancelPermitted);
        Assert.True(session.Authorization.AuthorizedReader);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.False(session.Authorization.StreamingPermitted);

        await duplex.WriteClientLineAsync("QUIT");
        await duplex.ReadClientLineAsync();
        await sessionTask;
    }

    [Fact]
    public async Task AuthinfoPass_Success_AppliesProviderAuthorizationIndependently()
    {
        var provider = new ScriptedAuthenticationProvider(
            (user, pass) => user == "reader1" && pass == "pw"
                ? NntpAuthenticationResult.Success(
                    "reader1",
                    new NntpAuthorization(
                        isAuthenticated: true,
                        authorizedReader: true,
                        authorizedTransit: false,
                        postingPermitted: true,
                        streamingPermitted: false))
                : NntpAuthenticationResult.Failed);

        await using var duplex = await AuthinfoDuplex.CreateAsync(provider);
        var session = duplex.CreateSession();
        var sessionTask = session.RunAsync();
        await duplex.ReadGreetingAsync();

        await duplex.WriteClientLineAsync("AUTHINFO USER reader1");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS pw");
        Assert.StartsWith("281 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        Assert.True(session.Authorization.AuthorizedReader);
        Assert.True(session.Authorization.PostingPermitted);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.False(session.Authorization.StreamingPermitted);
        Assert.False(session.Authorization.ControlCancelPermitted);

        await duplex.WriteClientLineAsync("ARTICLE");
        Assert.StartsWith("412 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("IHAVE <x@y>");
        Assert.StartsWith("502 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("QUIT");
        await duplex.ReadClientLineAsync();
        await sessionTask;
    }

    [Fact]
    public async Task Authinfo_AfterSuccess_Returns502_AndCapabilitiesOmitAuthinfo()
    {
        await using var duplex = await AuthinfoDuplex.CreateAsync(CreateAcceptingProvider());
        var session = duplex.CreateSession();
        var sessionTask = session.RunAsync();
        await duplex.ReadGreetingAsync();

        await duplex.WriteClientLineAsync("AUTHINFO USER fred");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS flintstone");
        Assert.StartsWith("281 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("AUTHINFO USER other");
        Assert.StartsWith("502 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("CAPABILITIES");
        Assert.StartsWith("101 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        var caps = await duplex.ReadMultilineBodyAsync();
        Assert.DoesNotContain(caps, static c => c.StartsWith("AUTHINFO", StringComparison.Ordinal));
        Assert.DoesNotContain(caps, static c => c.Equals("MODE-READER", StringComparison.Ordinal));

        await duplex.WriteClientLineAsync("QUIT");
        await duplex.ReadClientLineAsync();
        await sessionTask;
    }

    [Fact]
    public async Task CleartextDisabled_WithoutTls_AuthinfoUser_Returns483_DoesNotCallProvider()
    {
        var provider = new CountingAuthenticationProvider();
        await using var duplex = await AuthinfoDuplex.CreateAsync(provider);
        var session = duplex.CreateSession(allowCleartextAuth: false);
        var sessionTask = session.RunAsync();
        await duplex.ReadGreetingAsync();

        await duplex.WriteClientLineAsync("AUTHINFO USER fred");
        Assert.StartsWith("483 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.Null(session.PendingAuthUsername);
        Assert.Equal(0, provider.CallCount);

        await duplex.WriteClientLineAsync("QUIT");
        await duplex.ReadClientLineAsync();
        await sessionTask;
    }

    [Fact]
    public async Task CleartextDisabled_WithoutTls_AuthinfoPass_Returns483_DoesNotCallProvider()
    {
        var provider = new CountingAuthenticationProvider();
        await using var duplex = await AuthinfoDuplex.CreateAsync(provider);
        var session = duplex.CreateSession(allowCleartextAuth: false);
        // Simulate a pending USER that somehow existed before policy change — PASS must still gate first.
        session.SetPendingAuthUsername("fred");
        var sessionTask = session.RunAsync();
        await duplex.ReadGreetingAsync();

        await duplex.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.StartsWith("483 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.Equal(0, provider.CallCount);
        Assert.False(session.Authentication.IsAuthenticated);

        await duplex.WriteClientLineAsync("QUIT");
        await duplex.ReadClientLineAsync();
        await sessionTask;
    }

    [Fact]
    public async Task CleartextDisabled_WithTls_AuthinfoUserPass_Succeeds()
    {
        await using var duplex = await AuthinfoDuplex.CreateAsync(CreateAcceptingProvider(), isTls: true);
        var session = duplex.CreateSession(allowCleartextAuth: false);
        var sessionTask = session.RunAsync();
        await duplex.ReadGreetingAsync();

        await duplex.WriteClientLineAsync("AUTHINFO USER fred");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS flintstone");
        Assert.StartsWith("281 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.True(session.Authentication.IsAuthenticated);

        await duplex.WriteClientLineAsync("QUIT");
        await duplex.ReadClientLineAsync();
        await sessionTask;
    }

    [Fact]
    public async Task Capabilities_CleartextAllowed_AdvertisesAuthinfoUser()
    {
        await using var duplex = await AuthinfoDuplex.CreateAsync(CreateAcceptingProvider());
        var session = duplex.CreateSession(allowCleartextAuth: true);
        var sessionTask = session.RunAsync();
        await duplex.ReadGreetingAsync();

        await duplex.WriteClientLineAsync("CAPABILITIES");
        Assert.StartsWith("101 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        var caps = await duplex.ReadMultilineBodyAsync();
        Assert.Contains("AUTHINFO USER", caps);

        await duplex.WriteClientLineAsync("QUIT");
        await duplex.ReadClientLineAsync();
        await sessionTask;
    }

    [Fact]
    public async Task Capabilities_CleartextDisabled_WithoutTls_DoesNotAdvertiseAuthinfoUser()
    {
        await using var duplex = await AuthinfoDuplex.CreateAsync(CreateAcceptingProvider());
        var session = duplex.CreateSession(allowCleartextAuth: false);
        var sessionTask = session.RunAsync();
        await duplex.ReadGreetingAsync();

        await duplex.WriteClientLineAsync("CAPABILITIES");
        Assert.StartsWith("101 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        var caps = await duplex.ReadMultilineBodyAsync();
        Assert.Contains("AUTHINFO", caps);
        Assert.DoesNotContain(caps, static c => c.Equals("AUTHINFO USER", StringComparison.Ordinal));

        await duplex.WriteClientLineAsync("QUIT");
        await duplex.ReadClientLineAsync();
        await sessionTask;
    }

    [Fact]
    public async Task Capabilities_CleartextDisabled_WithTls_AdvertisesAuthinfoUser()
    {
        await using var duplex = await AuthinfoDuplex.CreateAsync(CreateAcceptingProvider(), isTls: true);
        var session = duplex.CreateSession(allowCleartextAuth: false);
        var sessionTask = session.RunAsync();
        await duplex.ReadGreetingAsync();

        await duplex.WriteClientLineAsync("CAPABILITIES");
        Assert.StartsWith("101 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        var caps = await duplex.ReadMultilineBodyAsync();
        Assert.Contains("AUTHINFO USER", caps);

        await duplex.WriteClientLineAsync("QUIT");
        await duplex.ReadClientLineAsync();
        await sessionTask;
    }

    [Fact]
    public async Task AuthinfoPass_DoesNotLogPassword_OnHandlerFailure()
    {
        var recording = new RecordingLoggerFactory();
        var provider = new ThrowingAuthenticationProvider();
        await using var duplex = await AuthinfoDuplex.CreateAsync(provider);
        var session = duplex.CreateSession(provider, allowCleartextAuth: true, loggerFactory: recording);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("AUTHINFO USER fred");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("AUTHINFO PASS super-secret-password-xyz");
        Assert.StartsWith("403 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;

        var joined = string.Join('\n', recording.Messages);
        Assert.DoesNotContain("super-secret-password-xyz", joined, StringComparison.Ordinal);
        Assert.Contains(recording.Messages, m => m.Contains("AUTHINFO PASS", StringComparison.Ordinal));
        Assert.Contains(
            recording.Categories,
            c => c == "VectorNNTPD.ReaderService.Session.Commands.AuthInfo");
    }

    private static ScriptedAuthenticationProvider CreateAcceptingProvider() =>
        new(
            (user, pass) => user == "fred" && pass == "flintstone"
                ? NntpAuthenticationResult.Success(
                    "fred",
                    new NntpAuthorization(
                        isAuthenticated: true,
                        authorizedReader: false,
                        authorizedTransit: false,
                        postingPermitted: false,
                        streamingPermitted: false))
                : NntpAuthenticationResult.Failed);

    private sealed class ScriptedAuthenticationProvider : INntpAuthenticationProvider
    {
        private readonly Func<string, string, NntpAuthenticationResult> _authenticate;

        public ScriptedAuthenticationProvider(Func<string, string, NntpAuthenticationResult> authenticate)
        {
            _authenticate = authenticate;
        }

        public ValueTask<NntpAuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_authenticate(username, password));
    }

    private sealed class CountingAuthenticationProvider : INntpAuthenticationProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<NntpAuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(NntpAuthenticationResult.Failed);
        }
    }

    private sealed class ThrowingAuthenticationProvider : INntpAuthenticationProvider
    {
        public ValueTask<NntpAuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("provider boom");
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public ConcurrentBag<string> Messages { get; } = [];
        public ConcurrentBag<string> Categories { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            Categories.Add(categoryName);
            return new RecordingLogger(Messages);
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentBag<string> _messages;

        public RecordingLogger(ConcurrentBag<string> messages) => _messages = messages;

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
            _messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                _messages.Add(exception.ToString());
            }
        }
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
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                Messages.Add(exception.ToString());
            }
        }
    }

    private sealed class AuthinfoDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());
        private readonly INntpAuthenticationProvider _provider;
        private readonly bool _isTls;

        private AuthinfoDuplex(INntpAuthenticationProvider provider, bool isTls)
        {
            _provider = provider;
            _isTls = isTls;
        }

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public static Task<AuthinfoDuplex> CreateAsync(
            INntpAuthenticationProvider provider,
            bool isTls = false) =>
            Task.FromResult(new AuthinfoDuplex(provider, isTls));

        public NntpSession CreateSession(
            INntpAuthenticationProvider? provider = null,
            bool allowCleartextAuth = true,
            ILoggerFactory? loggerFactory = null)
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)),
                isTls: _isTls);
            return new NntpSession(
                connection,
                loggerFactory?.CreateLogger<NntpSession>() ?? NullLogger<NntpSession>.Instance,
                authenticationProvider: provider ?? _provider,
                allowCleartextAuth: allowCleartextAuth,
                loggerFactory: loggerFactory);
        }

        public async Task ReadGreetingAsync()
        {
            var line = await ReadClientLineAsync();
            Assert.StartsWith("201 ", line, StringComparison.Ordinal);
        }

        public async Task WriteClientLineAsync(string line)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            await _clientToServer.Writer.WriteAsync(bytes);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
            Assert.NotNull(line);
            return line!;
        }

        public async Task<List<string>> ReadMultilineBodyAsync()
        {
            var lines = new List<string>();
            while (true)
            {
                var line = await ReadClientLineAsync();
                if (line == ".")
                {
                    break;
                }

                lines.Add(line);
            }

            return lines;
        }

        public async ValueTask DisposeAsync()
        {
            await _clientToServer.Writer.CompleteAsync();
            await _clientToServer.Reader.CompleteAsync();
            await _serverToClient.Writer.CompleteAsync();
            await _serverToClient.Reader.CompleteAsync();
        }
    }

    private sealed class PipeNntpConnection : INntpConnection
    {
        private readonly CancellationTokenSource _cts = new();

        public PipeNntpConnection(
            PipeReader input,
            PipeWriter output,
            ConnectionClientIdentity identity,
            bool isTls = false)
        {
            Input = input;
            Output = output;
            ClientIdentity = identity;
            IsTls = isTls;
        }

        public PipeReader Input { get; }
        public PipeWriter Output { get; }
        public System.Net.EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
        public System.Net.EndPoint? LocalEndPoint => null;
        public ConnectionClientIdentity ClientIdentity { get; }
        public bool IsTls { get; }

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
        {
            tlsVersion = string.Empty;
            cipher = string.Empty;
            return false;
        }

        public bool IsCompressed => Volatile.Read(ref _compressed) == 1;
        public CancellationToken ConnectionClosed => _cts.Token;
        public bool IsCompleted => _cts.IsCancellationRequested;
        public long OutboundIdleVersion => 0;

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitForOutboundDeliveryAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CompleteAsync(Exception? exception = null)
        {
            _cts.Cancel();
            return Task.CompletedTask;
        }

        public Task UpgradeToTlsAsync(
            ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _compressed, 1);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }

        private int _compressed;
    }
}
