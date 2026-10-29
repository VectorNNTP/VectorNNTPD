using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VectorNNTPD.TransitService.Configuration;

namespace VectorNNTPD.TransitService.Tests.TestDoubles;

public sealed class FakeSmtpServerOptions
{
    public bool ImplicitTls { get; set; }

    public bool AdvertiseStartTls { get; set; }

    public bool AdvertiseAuthPlain { get; set; }

    public bool AdvertiseAuthLogin { get; set; }

    public bool RequireTlsForAuthentication { get; set; } = true;

    public string? ExpectedUsername { get; set; }

    public string? ExpectedPassword { get; set; }

    public long? Size { get; set; }

    public int MailFromCode { get; set; } = 250;

    public int DefaultRcptCode { get; set; } = 250;

    public Dictionary<string, int> RcptCodes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int DataIntroCode { get; set; } = 354;

    public int DataDoneCode { get; set; } = 250;

    public int TransientDataFailures { get; set; }

    public bool CloseAfterGreeting { get; set; }

    public bool CloseAfterEhlo { get; set; }

    public string Greeting { get; set; } = "220 test.example ESMTP";

    public X509Certificate2? Certificate { get; set; }
}

public sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly FakeSmtpServerOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _commands = [];
    private readonly List<string> _rawLines = [];
    private readonly Task? _run;
    private int _dataAttempts;
    private bool _sawPlaintextBeforeTls;

    public FakeSmtpServer(FakeSmtpServerOptions? options = null)
    {
        _options = options ?? new FakeSmtpServerOptions();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _run = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public int Port { get; }

    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_commands)
            {
                return [.. _commands];
            }
        }
    }

    public IReadOnlyList<string> RawLines
    {
        get
        {
            lock (_rawLines)
            {
                return [.. _rawLines];
            }
        }
    }

    public byte[]? LastMessage { get; private set; }

    public bool Authenticated { get; private set; }

    public bool StartedTls { get; private set; }

    public bool SawPlaintextSmtpBeforeTls => _sawPlaintextBeforeTls;

    public X509Certificate2? Certificate => _options.Certificate;

    public SmtpOptions ClientOptions(
        SmtpSecurityMode security,
        string? username = null,
        string? password = null) =>
        new()
        {
            Host = "127.0.0.1",
            Port = Port,
            Security = security,
            Username = username ?? string.Empty,
            Password = password ?? string.Empty,
            RequireTlsForAuthentication = security != SmtpSecurityMode.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            CommandTimeout = TimeSpan.FromSeconds(5),
            MaxAttempts = 1,
        };

    public static X509Certificate2[] TrustAnchors(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return [X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert))];
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        if (_run is not null)
        {
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
            }
            catch (OperationCanceledException ex)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
            }
        }

        _cts.Dispose();
        _options.Certificate?.Dispose();
    }

    public static X509Certificate2 CreateSelfSignedCertificate(
        string dnsName = "localhost",
        bool includeLoopbackIp = true)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=" + dnsName,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        if (includeLoopbackIp)
        {
            san.AddIpAddress(IPAddress.Loopback);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                critical: false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx, "vectornntp-test"),
            "vectornntp-test",
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(tcp, cancellationToken), CancellationToken.None);
            }
        }
        catch (OperationCanceledException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }
        catch (ObjectDisposedException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }
        catch (SocketException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }
    }

    private async Task HandleAsync(TcpClient tcp, CancellationToken cancellationToken)
    {
        using (tcp)
        try
        {
            Stream stream = tcp.GetStream();
            var tls = false;
            if (_options.ImplicitTls)
            {
                stream = await ServerTlsAsync(stream, cancellationToken).ConfigureAwait(false);
                tls = true;
                StartedTls = true;
            }

            await WriteLineAsync(stream, _options.Greeting, cancellationToken).ConfigureAwait(false);
            if (_options.CloseAfterGreeting)
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                if (!tls)
                {
                    _sawPlaintextBeforeTls = true;
                }

                Record(line, redact: line.StartsWith("AUTH ", StringComparison.OrdinalIgnoreCase));
                var verb = Verb(line);
                if (verb.Equals("EHLO", StringComparison.OrdinalIgnoreCase)
                    || verb.Equals("HELO", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteEhloAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (_options.CloseAfterEhlo)
                    {
                        return;
                    }

                    continue;
                }

                if (verb.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_options.AdvertiseStartTls)
                    {
                        await WriteLineAsync(stream, "502 STARTTLS not available", cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    await WriteLineAsync(stream, "220 Ready to start TLS", cancellationToken).ConfigureAwait(false);
                    stream = await ServerTlsAsync(stream, cancellationToken).ConfigureAwait(false);
                    tls = true;
                    StartedTls = true;
                    continue;
                }

                if (verb.Equals("AUTH", StringComparison.OrdinalIgnoreCase))
                {
                    if (_options.RequireTlsForAuthentication && !tls)
                    {
                        await WriteLineAsync(stream, "538 5.7.11 Encryption required for requested authentication mechanism", cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    await HandleAuthAsync(stream, line, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (verb.Equals("MAIL", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteLineAsync(stream, _options.MailFromCode + " OK", cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (verb.Equals("RCPT", StringComparison.OrdinalIgnoreCase))
                {
                    var mailbox = ExtractMailbox(line);
                    var code = _options.RcptCodes.TryGetValue(mailbox, out var mapped)
                        ? mapped
                        : _options.DefaultRcptCode;
                    await WriteLineAsync(stream, code + " " + (code < 400 ? "OK" : "rejected"), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (verb.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteLineAsync(stream, _options.DataIntroCode + " Start mail input", cancellationToken)
                        .ConfigureAwait(false);
                    if (_options.DataIntroCode >= 400)
                    {
                        continue;
                    }

                    LastMessage = await ReadDataAsync(stream, cancellationToken).ConfigureAwait(false);
                    _dataAttempts++;
                    var done = _dataAttempts <= _options.TransientDataFailures
                        ? 451
                        : _options.DataDoneCode;
                    await WriteLineAsync(stream, done + " " + (done < 400 ? "OK" : "failed"), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (verb.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteLineAsync(stream, "221 Bye", cancellationToken).ConfigureAwait(false);
                    return;
                }

                await WriteLineAsync(stream, "500 unrecognized", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }
        catch (IOException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }
        catch (AuthenticationException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }
    }
    private async Task HandleAuthAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var mechanism = parts.Length >= 2 ? parts[1] : string.Empty;
        if (mechanism.Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
        {
            string payload;
            if (parts.Length >= 3)
            {
                payload = parts[2];
            }
            else
            {
                await WriteLineAsync(stream, "334 ", cancellationToken).ConfigureAwait(false);
                payload = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false) ?? string.Empty;
                Record(payload, redact: true);
            }

            if (TryDecodePlain(payload, out var user, out var password)
                && user == _options.ExpectedUsername
                && password == _options.ExpectedPassword)
            {
                Authenticated = true;
                await WriteLineAsync(stream, "235 2.7.0 Authentication successful", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await WriteLineAsync(stream, "535 5.7.8 Authentication failed", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (mechanism.Equals("LOGIN", StringComparison.OrdinalIgnoreCase))
        {
            await WriteLineAsync(stream, "334 VXNlcm5hbWU6", cancellationToken).ConfigureAwait(false);
            var userB64 = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false) ?? string.Empty;
            Record(userB64, redact: true);
            await WriteLineAsync(stream, "334 UGFzc3dvcmQ6", cancellationToken).ConfigureAwait(false);
            var passB64 = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false) ?? string.Empty;
            Record(passB64, redact: true);
            var user = DecodeUtf8(userB64);
            var password = DecodeUtf8(passB64);
            if (user == _options.ExpectedUsername && password == _options.ExpectedPassword)
            {
                Authenticated = true;
                await WriteLineAsync(stream, "235 Authentication successful", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await WriteLineAsync(stream, "535 Authentication failed", cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteLineAsync(stream, "504 Unrecognized authentication type", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteEhloAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lines = new List<string> { "250-test.example" };
        if (_options.AdvertiseStartTls)
        {
            lines.Add("250-STARTTLS");
        }

        if (_options.Size is { } size)
        {
            lines.Add("250-SIZE " + size);
        }

        lines.Add("250-8BITMIME");
        if (_options.AdvertiseAuthPlain || _options.AdvertiseAuthLogin)
        {
            var mechanisms = new List<string>();
            if (_options.AdvertiseAuthPlain)
            {
                mechanisms.Add("PLAIN");
            }

            if (_options.AdvertiseAuthLogin)
            {
                mechanisms.Add("LOGIN");
            }

            lines.Add("250-AUTH " + string.Join(' ', mechanisms));
        }

        lines.Add("250 PIPELINING");
        foreach (var line in lines)
        {
            await WriteLineAsync(stream, line, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Stream> ServerTlsAsync(Stream inner, CancellationToken cancellationToken)
    {
        var cert = _options.Certificate ?? CreateSelfSignedCertificate();
        _options.Certificate ??= cert;
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = cert,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                cancellationToken)
            .ConfigureAwait(false);
        return ssl;
    }

    private void Record(string line, bool redact)
    {
        lock (_rawLines)
        {
            _rawLines.Add(redact ? "REDACTED" : line);
        }

        lock (_commands)
        {
            if (redact)
            {
                _commands.Add("AUTH");
                return;
            }

            _commands.Add(line);
        }
    }

    private static async Task WriteLineAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(128);
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.Count == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray());
            }

            if (one[0] == (byte)'\n')
            {
                if (buffer.Count > 0 && buffer[^1] == (byte)'\r')
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }

                return Encoding.ASCII.GetString(buffer.ToArray());
            }

            buffer.Add(one[0]);
        }
    }

    private static async Task<byte[]> ReadDataAsync(Stream stream, CancellationToken cancellationToken)
    {
        var output = new List<byte>();
        var line = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            line.Add(one[0]);
            if (line.Count >= 2 && line[^2] == (byte)'\r' && line[^1] == (byte)'\n')
            {
                if (line.Count == 3 && line[0] == (byte)'.')
                {
                    break;
                }

                var payload = line.ToArray().AsSpan()[..^2];
                if (payload.Length > 0 && payload[0] == (byte)'.')
                {
                    payload = payload[1..];
                }

                output.AddRange(payload.ToArray());
                output.Add((byte)'\r');
                output.Add((byte)'\n');
                line.Clear();
            }
        }

        return [.. output];
    }

    private static string Verb(string line)
    {
        var space = line.IndexOf(' ');
        return space < 0 ? line : line[..space];
    }

    private static string ExtractMailbox(string line)
    {
        var start = line.IndexOf('<');
        var end = line.IndexOf('>');
        return start >= 0 && end > start ? line[(start + 1)..end] : string.Empty;
    }

    private static bool TryDecodePlain(string payload, out string user, out string password)
    {
        user = string.Empty;
        password = string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(payload);
            var text = Encoding.UTF8.GetString(bytes);
            var parts = text.Split('\0');
            if (parts.Length < 3)
            {
                return false;
            }

            user = parts[1];
            password = parts[2];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string DecodeUtf8(string base64)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}
