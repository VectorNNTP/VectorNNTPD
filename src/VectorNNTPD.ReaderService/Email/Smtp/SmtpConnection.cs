using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VectorNNTPD.ReaderService.Configuration;

namespace VectorNNTPD.ReaderService.Email.Smtp
{
    /// <summary>
    /// One SMTP TCP/TLS session. Supports sequential messages, QUIT, and reconnect.
    /// Not a connection pool.
    /// </summary>
    internal sealed class SmtpConnection : IAsyncDisposable
    {
        private static readonly byte[] Crlf = "\r\n"u8.ToArray();

        private readonly SmtpOptions _options;
        private readonly string _ehloHostname;
        private readonly IReadOnlyCollection<X509Certificate2>? _extraTrustedRoots;
        private TcpClient? _tcp;
        private Stream? _stream;
        private SmtpResponseReader? _reader;
        private bool _tls;
        private bool _quit;

        public SmtpConnection(
            SmtpOptions options,
            string ehloHostname,
            IReadOnlyCollection<X509Certificate2>? extraTrustedRoots = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(ehloHostname);
            _options = options;
            _ehloHostname = ehloHostname;
            _extraTrustedRoots = extraTrustedRoots;
        }

        public SmtpCapabilities Capabilities { get; private set; } = new();

        public bool IsAuthenticated { get; private set; }

        public bool IsTls => _tls;

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            await DisposeSocketAsync().ConfigureAwait(false);
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(_options.ConnectTimeout);

            _tcp = new TcpClient();
            try
            {
                await _tcp.ConnectAsync(_options.Host.Trim(), _options.Port, connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SmtpException(SmtpFailureKind.Network, "SMTP connect timed out.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                throw new SmtpException(SmtpFailureKind.Network, "SMTP connect failed.", ex);
            }

            _tcp.NoDelay = true;
            _stream = _tcp.GetStream();
            _reader = new SmtpResponseReader(_stream);

            if (_options.Security == SmtpSecurityMode.ImplicitTls)
            {
                await UpgradeTlsAsync(cancellationToken).ConfigureAwait(false);
            }

            var greeting = await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
            if (!greeting.IsPositiveCompletion)
            {
                throw Classify(greeting, "SMTP greeting rejected.");
            }
        }

        public async Task EhloAsync(CancellationToken cancellationToken)
        {
            var response = await CommandAsync("EHLO", SanitizeEhlo(_ehloHostname), cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsPositiveCompletion)
            {
                var helo = await CommandAsync("HELO", SanitizeEhlo(_ehloHostname), cancellationToken)
                    .ConfigureAwait(false);
                if (!helo.IsPositiveCompletion)
                {
                    throw Classify(helo, "EHLO/HELO failed.");
                }

                Capabilities = new SmtpCapabilities();
                return;
            }

            Capabilities = SmtpCapabilities.Parse(response);
        }

        public async Task StartTlsAsync(CancellationToken cancellationToken)
        {
            if (!Capabilities.StartTls)
            {
                throw new SmtpException(
                    SmtpFailureKind.Tls,
                    "STARTTLS is required but the server did not advertise STARTTLS.");
            }

            var response = await CommandAsync("STARTTLS", argument: null, cancellationToken).ConfigureAwait(false);
            if (response.Code != 220)
            {
                throw Classify(response, "STARTTLS was rejected.");
            }

            await UpgradeTlsAsync(cancellationToken).ConfigureAwait(false);
            await EhloAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task AuthenticateAsync(CancellationToken cancellationToken)
        {
            var username = _options.Username.Trim();
            if (string.IsNullOrEmpty(username))
            {
                return;
            }

            if (_options.RequireTlsForAuthentication && !_tls)
            {
                throw new SmtpException(
                    SmtpFailureKind.Authentication,
                    "SMTP AUTH requires TLS and the session is not TLS-protected.");
            }

            if (Capabilities.SupportsAuth("PLAIN"))
            {
                await AuthPlainAsync(username, _options.Password, cancellationToken).ConfigureAwait(false);
                IsAuthenticated = true;
                return;
            }

            if (Capabilities.SupportsAuth("LOGIN"))
            {
                await AuthLoginAsync(username, _options.Password, cancellationToken).ConfigureAwait(false);
                IsAuthenticated = true;
                return;
            }

            throw new SmtpException(
                SmtpFailureKind.Authentication,
                "SMTP AUTH is configured but the server advertised neither PLAIN nor LOGIN.");
        }

        public Task<SmtpResponse> MailFromAsync(string mailbox, CancellationToken cancellationToken) =>
            CommandAsync("MAIL", "FROM:<" + mailbox + ">", cancellationToken);

        public Task<SmtpResponse> RcptToAsync(string mailbox, CancellationToken cancellationToken) =>
            CommandAsync("RCPT", "TO:<" + mailbox + ">", cancellationToken);

        public async Task<SmtpResponse> DataAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
        {
            var intro = await CommandAsync("DATA", argument: null, cancellationToken).ConfigureAwait(false);
            if (!intro.IsPositiveIntermediate)
            {
                throw Classify(intro, "DATA was rejected.");
            }

            await WriteDataAsync(message, cancellationToken).ConfigureAwait(false);
            return await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task QuitAsync(CancellationToken cancellationToken)
        {
            if (_quit || _stream is null)
            {
                return;
            }

            _quit = true;
            try
            {
                _ = await CommandAsync("QUIT", argument: null, cancellationToken).ConfigureAwait(false);
            }
            catch (SmtpException)
            {
                // Best-effort close.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeSocketAsync().ConfigureAwait(false);
        }

        private async Task UpgradeTlsAsync(CancellationToken cancellationToken)
        {
            if (_stream is null)
            {
                throw new SmtpException(SmtpFailureKind.Protocol, "SMTP TLS upgrade has no transport stream.");
            }

            var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
            try
            {
                using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                tlsCts.CancelAfter(_options.ConnectTimeout);
                await ssl.AuthenticateAsClientAsync(CreateClientTlsOptions(), tlsCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw new SmtpException(SmtpFailureKind.Tls, "SMTP TLS handshake timed out.");
            }
            catch (OperationCanceledException)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is AuthenticationException or IOException)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw new SmtpException(SmtpFailureKind.Tls, "SMTP TLS handshake failed.", ex);
            }

            _stream = ssl;
            _reader?.ReturnBuffer();
            _reader = new SmtpResponseReader(ssl);
            _tls = true;
        }

        private async Task AuthPlainAsync(string username, string password, CancellationToken cancellationToken)
        {
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0" + username + "\0" + password));
            var response = await CommandAsync("AUTH", "PLAIN " + payload, cancellationToken, redact: true)
                .ConfigureAwait(false);
            if (!response.IsPositiveCompletion)
            {
                throw new SmtpException(SmtpFailureKind.Authentication, "SMTP AUTH PLAIN failed.")
                {
                    StatusCode = response.Code,
                };
            }
        }

        private async Task AuthLoginAsync(string username, string password, CancellationToken cancellationToken)
        {
            var intro = await CommandAsync("AUTH", "LOGIN", cancellationToken, redact: true).ConfigureAwait(false);
            if (!intro.IsPositiveIntermediate)
            {
                throw new SmtpException(SmtpFailureKind.Authentication, "SMTP AUTH LOGIN was rejected.")
                {
                    StatusCode = intro.Code,
                };
            }

            var userResponse = await CommandAsync(
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(username)),
                    argument: null,
                    cancellationToken,
                    redact: true)
                .ConfigureAwait(false);
            if (!userResponse.IsPositiveIntermediate)
            {
                throw new SmtpException(SmtpFailureKind.Authentication, "SMTP AUTH LOGIN username was rejected.")
                {
                    StatusCode = userResponse.Code,
                };
            }

            var passResponse = await CommandAsync(
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(password)),
                    argument: null,
                    cancellationToken,
                    redact: true)
                .ConfigureAwait(false);
            if (!passResponse.IsPositiveCompletion)
            {
                throw new SmtpException(SmtpFailureKind.Authentication, "SMTP AUTH LOGIN failed.")
                {
                    StatusCode = passResponse.Code,
                };
            }
        }

        private async Task<SmtpResponse> CommandAsync(
            string verb,
            string? argument,
            CancellationToken cancellationToken,
            bool redact = false)
        {
            _ = redact;
            var line = argument is null ? verb : verb + " " + argument;
            if (line.IndexOf('\r') >= 0 || line.IndexOf('\n') >= 0)
            {
                throw new SmtpException(SmtpFailureKind.Message, "SMTP command contained CR or LF.");
            }

            await WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
            return await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            if (_stream is null)
            {
                throw new SmtpException(SmtpFailureKind.Network, "SMTP connection is closed.");
            }

            var encoded = Encoding.ASCII.GetBytes(line);
            using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            writeCts.CancelAfter(_options.CommandTimeout);
            try
            {
                await _stream.WriteAsync(encoded, writeCts.Token).ConfigureAwait(false);
                await _stream.WriteAsync(Crlf, writeCts.Token).ConfigureAwait(false);
                await _stream.FlushAsync(writeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SmtpException(SmtpFailureKind.Transient, "SMTP write timed out.");
            }
            catch (IOException ex)
            {
                throw new SmtpException(SmtpFailureKind.Network, "SMTP write failed.", ex);
            }
        }

        private async Task WriteDataAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
        {
            if (_stream is null)
            {
                throw new SmtpException(SmtpFailureKind.Network, "SMTP connection is closed.");
            }

            using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            writeCts.CancelAfter(_options.CommandTimeout);
            try
            {
                await SmtpDataEncoder.WriteStuffedAsync(_stream, message, writeCts.Token).ConfigureAwait(false);
                await _stream.FlushAsync(writeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SmtpException(SmtpFailureKind.Transient, "SMTP DATA write timed out.");
            }
            catch (IOException ex)
            {
                throw new SmtpException(SmtpFailureKind.Network, "SMTP DATA write failed.", ex);
            }
        }

        private async Task<SmtpResponse> ReadResponseAsync(CancellationToken cancellationToken)
        {
            if (_reader is null)
            {
                throw new SmtpException(SmtpFailureKind.Network, "SMTP connection is closed.");
            }

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(_options.CommandTimeout);
            try
            {
                return await _reader.ReadAsync(readCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SmtpException(SmtpFailureKind.Transient, "SMTP read timed out.");
            }
            catch (SmtpException)
            {
                throw;
            }
            catch (IOException ex)
            {
                throw new SmtpException(SmtpFailureKind.Network, "SMTP read failed.", ex);
            }
        }

        private async Task DisposeSocketAsync()
        {
            _reader?.ReturnBuffer();
            _reader = null;
            if (_stream is not null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
            }

            _tcp?.Dispose();
            _tcp = null;
            _tls = false;
            IsAuthenticated = false;
            _quit = false;
        }

        internal static SmtpException Classify(SmtpResponse response, string message)
        {
            var kind = response.IsTransientNegative
                ? SmtpFailureKind.Transient
                : response.IsPermanentNegative
                    ? SmtpFailureKind.Permanent
                    : SmtpFailureKind.Protocol;
            return new SmtpException(kind, message) { StatusCode = response.Code };
        }

        private SslClientAuthenticationOptions CreateClientTlsOptions()
        {
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = _options.Host.Trim(),
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            };

            if (_extraTrustedRoots is { Count: > 0 })
            {
                var policy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    RevocationMode = X509RevocationMode.NoCheck,
                };
                foreach (var root in _extraTrustedRoots)
                {
                    policy.CustomTrustStore.Add(root);
                }

                options.CertificateChainPolicy = policy;
            }

            return options;
        }

        private static string SanitizeEhlo(string hostname)
        {
            var trimmed = hostname.Trim();
            if (trimmed.Length == 0 || trimmed.IndexOfAny(['\r', '\n', ' ']) >= 0)
            {
                throw new SmtpException(SmtpFailureKind.Protocol, "SMTP EHLO hostname is missing or invalid.");
            }

            return trimmed;
        }
    }
}
