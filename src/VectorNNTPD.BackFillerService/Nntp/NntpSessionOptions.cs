using VectorNNTPD.BackFillerService.Networking;

namespace VectorNNTPD.BackFillerService.Nntp
{
    /// <summary>
    /// Immutable acquisition guardrails. Defaults match the old BackFiller acquisition contract.
    /// </summary>
    /// <param name="ReceiveBufferBytes">
    /// Managed <c>NntpStreamReader</c> buffer in bytes. This is not the TCP receive window.
    /// </param>
    /// <param name="MaxStatusLineBytes">Maximum accepted status-line length excluding CRLF.</param>
    /// <param name="MaxArticleBytes">Maximum destuffed ARTICLE payload. Defaults to the old 5 MiB contract.</param>
    /// <param name="ConnectTimeout">TCP connect and TLS handshake budget.</param>
    /// <param name="CommandTimeout">Command write and single-line status read budget.</param>
    /// <param name="ReceiveTimeout">Multiline ARTICLE payload read budget.</param>
    /// <param name="TcpSendBufferBytes">
    /// Requested TCP send-buffer limit for the upstream socket. Independent of <paramref name="TcpReceiveBufferBytes"/>
    /// and of <paramref name="ReceiveBufferBytes"/>.
    /// </param>
    /// <param name="TcpReceiveBufferBytes">
    /// Requested TCP receive-buffer limit for the upstream socket. Article bodies arrive on this window.
    /// </param>
    /// <param name="TcpKeepAlive">
    /// TCP keepalive timings for each upstream socket. Independent of provider MySQL <c>keepalive</c> (<c>DATE</c>).
    /// </param>
    /// <param name="ServerCertificateValidationCallback">
    /// Optional TLS certificate callback. <see langword="null"/> uses platform validation
    /// (production default). Tests may supply a callback; this is not a configuration setting.
    /// </param>
    internal sealed record NntpSessionOptions(
        int ReceiveBufferBytes,
        int MaxStatusLineBytes,
        int MaxArticleBytes,
        TimeSpan ConnectTimeout,
        TimeSpan CommandTimeout,
        TimeSpan ReceiveTimeout,
        int TcpSendBufferBytes,
        int TcpReceiveBufferBytes,
        TcpKeepAliveSettings TcpKeepAlive = default,
        System.Net.Security.RemoteCertificateValidationCallback? ServerCertificateValidationCallback = null)
    {
        /// <summary>
        /// Reader buffer 64 KiB, TCP send 1 MiB, TCP receive 2 MiB, 16 KiB status lines, 5 MiB articles,
        /// 30 s connect/command, 2 min receive, and <see cref="TcpKeepAliveSettings.Default"/> keepalive.
        /// </summary>
        /// <remarks>
        /// The TCP sizes match <see cref="Configuration.BackFillerNntpOptions"/> defaults.
        /// One upstream session therefore requests 3 MiB of kernel socket buffering, not a 3 MiB managed allocation.
        /// The reader allocation stays 64 KiB.
        /// </remarks>
        internal static NntpSessionOptions Default { get; } = new(
            ReceiveBufferBytes: 64 * 1024,
            MaxStatusLineBytes: ArticleResourceLimits.MaxStatusLineBytes,
            MaxArticleBytes: ArticleResourceLimits.MaxArticleBytes,
            ConnectTimeout: TimeSpan.FromSeconds(30),
            CommandTimeout: TimeSpan.FromSeconds(30),
            ReceiveTimeout: TimeSpan.FromMinutes(2),
            TcpSendBufferBytes: Configuration.BackFillerNntpOptions.DefaultSendBufferBytes,
            TcpReceiveBufferBytes: Configuration.BackFillerNntpOptions.DefaultReceiveBufferBytes,
            TcpKeepAlive: TcpKeepAliveSettings.Default);

        /// <summary>
        /// Effective keepalive timings. A default-constructed option (all zeros) resolves to
        /// <see cref="TcpKeepAliveSettings.Default"/> so older test constructors stay valid.
        /// </summary>
        internal TcpKeepAliveSettings EffectiveTcpKeepAlive =>
            TcpKeepAlive.TimeSeconds == 0
            && TcpKeepAlive.IntervalSeconds == 0
            && TcpKeepAlive.RetryCount == 0
                ? TcpKeepAliveSettings.Default
                : TcpKeepAlive;
    }
}
