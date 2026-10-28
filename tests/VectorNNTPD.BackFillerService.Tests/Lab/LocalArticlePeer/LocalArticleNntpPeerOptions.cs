using System.Net;

namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    /// <summary>Lab-only bind and corpus options for <see cref="LocalArticleNntpPeer"/>.</summary>
    internal sealed class LocalArticleNntpPeerOptions
    {
        /// <summary>Environment flag required to bind outside loopback.</summary>
        internal const string AllowNonLoopbackEnvironmentVariable =
            "VECTORNNTPD_LAB_LOCAL_ARTICLE_PEER_ALLOW_NON_LOOPBACK";

        /// <summary>Absolute corpus root (logical article files).</summary>
        public required string CorpusRoot { get; init; }

        /// <summary>Bind address. Defaults to loopback.</summary>
        public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

        /// <summary>TCP port. Zero selects an ephemeral port.</summary>
        public int Port { get; init; }

        /// <summary>Maximum concurrent accepted sessions.</summary>
        public int MaxConcurrentSessions { get; init; } = 64;

        /// <summary>Maximum logical article bytes accepted from disk.</summary>
        public int MaxArticleBytes { get; init; } = 5 * 1024 * 1024;

        /// <summary>
        /// When set, AUTHINFO USER/PASS must match. When null, authentication is not required
        /// (client must also omit credentials).
        /// </summary>
        public string? Username { get; init; }

        /// <summary>Password paired with <see cref="Username"/>.</summary>
        public string? Password { get; init; }

        /// <summary>Optional artificial delay before ARTICLE body (separate latency experiments).</summary>
        public TimeSpan ArticleDelay { get; init; } = TimeSpan.Zero;

        /// <summary>
        /// When true, frame every indexed article once at startup and serve immutable precomputed
        /// ARTICLE wire bytes. Lab diagnostic mode only; default remains file-backed.
        /// </summary>
        public bool PreloadArticlesIntoMemory { get; init; }

        /// <summary>True when either credential is configured (client must authenticate).</summary>
        internal bool RequiresAuthentication =>
            !string.IsNullOrWhiteSpace(Username) || !string.IsNullOrWhiteSpace(Password);

        /// <summary>
        /// Validates bind safety: non-loopback requires
        /// <see cref="AllowNonLoopbackEnvironmentVariable"/>=<c>1</c>.
        /// </summary>
        internal void ValidateBindOrThrow()
        {
            if (IPAddress.IsLoopback(BindAddress))
            {
                return;
            }

            var allow = Environment.GetEnvironmentVariable(AllowNonLoopbackEnvironmentVariable);
            if (!string.Equals(allow, "1", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to bind LocalArticleNntpPeer to non-loopback {BindAddress}. "
                    + $"Set {AllowNonLoopbackEnvironmentVariable}=1 for explicit lab override only.");
            }
        }
    }
}
