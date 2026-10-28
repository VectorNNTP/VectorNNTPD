using VectorNNTPD.BackFillerService.Nntp;

namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    /// <summary>
    /// Lab/test transport decorator that refuses any connect whose host/port is not the allowlisted
    /// local peer. Demonstrates dial-time fail-closed behaviour for automated tests.
    /// </summary>
    /// <remarks>
    /// Not registered in production DI. Wiring an equivalent decorator into the running BackFiller
    /// service is the minimum production-path change required for process-level dial enforcement
    /// and requires separate approval (see ISOLATION_AND_ACCOUNTING_FIXES.md).
    /// </remarks>
    internal sealed class LoopbackAllowlistNntpTransportFactory : INntpTransportFactory
    {
        private readonly INntpTransportFactory _inner;
        private readonly string _allowedHost;
        private readonly int _allowedPort;

        internal LoopbackAllowlistNntpTransportFactory(
            INntpTransportFactory inner,
            string allowedHost,
            int allowedPort)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _allowedHost = allowedHost ?? throw new ArgumentNullException(nameof(allowedHost));
            if (allowedPort is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(allowedPort));
            }

            _allowedPort = allowedPort;
        }

        /// <inheritdoc />
        public Task<Stream> ConnectAsync(
            BackFillerProviderDefinition provider,
            NntpSessionOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (!string.Equals(provider.Host, _allowedHost, StringComparison.Ordinal)
                || provider.Port != _allowedPort)
            {
                throw new InvalidOperationException(
                    $"Lab allowlist refused upstream connect backbone={provider.Backbone} "
                    + $"host={provider.Host} port={provider.Port}; "
                    + $"allowed={_allowedHost}:{_allowedPort}. No external fallback.");
            }

            if (provider.UseTls)
            {
                throw new InvalidOperationException(
                    $"Lab allowlist refused TLS upstream connect for backbone={provider.Backbone} "
                    + "(local peer is cleartext).");
            }

            return _inner.ConnectAsync(provider, options, cancellationToken);
        }
    }
}
