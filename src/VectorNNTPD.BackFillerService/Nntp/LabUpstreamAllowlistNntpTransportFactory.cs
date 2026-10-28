using VectorNNTPD.BackFillerService.Configuration;

namespace VectorNNTPD.BackFillerService.Nntp
{
    /// <summary>
    /// Lab-only decorator that rejects upstream dials that are not the configured loopback peer
    /// before the inner factory opens a socket.
    /// </summary>
    /// <remarks>
    /// Constructed only when <see cref="LabUpstreamIsolationOptions.Enabled"/> is true.
    /// Does not rewrite <see cref="BackFillerProviderDefinition"/> fields.
    /// Reconnects and new pooled sessions call <see cref="ConnectAsync"/> again and reapply the gate.
    /// There is no fallback to an unguarded factory from this type.
    /// </remarks>
    internal sealed class LabUpstreamAllowlistNntpTransportFactory : INntpTransportFactory
    {
        private readonly INntpTransportFactory _inner;
        private readonly string _allowedHost;
        private readonly int _allowedPort;
        private readonly bool _rejectTls;

        /// <summary>
        /// Wraps <paramref name="inner"/> with fail-closed allowlist checks from <paramref name="options"/>.
        /// </summary>
        /// <param name="inner">Concrete transport that opens sockets (typically <see cref="TcpNntpTransportFactory"/>).</param>
        /// <param name="options">Enabled lab isolation options already validated at startup.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="InvalidOperationException"><paramref name="options"/> is disabled or incomplete.</exception>
        internal LabUpstreamAllowlistNntpTransportFactory(
            INntpTransportFactory inner,
            LabUpstreamIsolationOptions options)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(options);
            if (!options.Enabled)
            {
                throw new InvalidOperationException(
                    "LabUpstreamAllowlistNntpTransportFactory must not be constructed when LabUpstreamIsolation is disabled.");
            }

            if (string.IsNullOrWhiteSpace(options.AllowedHost)
                || !string.Equals(
                    options.AllowedHost.Trim(),
                    LabUpstreamIsolationOptions.RequiredLiteralHost,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "LabUpstreamIsolation AllowedHost must be "
                    + LabUpstreamIsolationOptions.RequiredLiteralHost
                    + " when constructing the allowlist transport.");
            }

            if (options.AllowedPort is null or < 1 or > 65535)
            {
                throw new InvalidOperationException(
                    "LabUpstreamIsolation AllowedPort must be in 1..65535 when constructing the allowlist transport.");
            }

            _inner = inner;
            _allowedHost = LabUpstreamIsolationOptions.RequiredLiteralHost;
            _allowedPort = options.AllowedPort.Value;
            _rejectTls = options.RejectTls;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Validates <paramref name="provider"/> host/port/(optional) TLS before calling the inner factory.
        /// Invalid destinations never reach socket creation.
        /// </remarks>
        public Task<Stream> ConnectAsync(
            BackFillerProviderDefinition provider,
            NntpSessionOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentNullException.ThrowIfNull(options);
            EnsureAllowedOrThrow(provider);
            return _inner.ConnectAsync(provider, options, cancellationToken);
        }

        /// <summary>Throws when <paramref name="provider"/> is outside the lab allowlist.</summary>
        internal void EnsureAllowedOrThrow(BackFillerProviderDefinition provider)
        {
            ArgumentNullException.ThrowIfNull(provider);

            if (string.IsNullOrWhiteSpace(provider.Host))
            {
                throw new InvalidOperationException(
                    $"Lab upstream isolation refused backbone={provider.Backbone}: host is missing. "
                    + $"Allowed={_allowedHost}:{_allowedPort}.");
            }

            var host = provider.Host.Trim();
            if (!string.Equals(host, _allowedHost, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Lab upstream isolation refused backbone={provider.Backbone} host='{host}' port={provider.Port}. "
                    + $"Allowed host is literal {_allowedHost} only (no DNS, localhost, IPv6, or non-loopback). "
                    + $"Allowed={_allowedHost}:{_allowedPort}.");
            }

            if (provider.Port != _allowedPort)
            {
                throw new InvalidOperationException(
                    $"Lab upstream isolation refused backbone={provider.Backbone} port={provider.Port}; "
                    + $"allowed port is {_allowedPort}.");
            }

            if (_rejectTls && provider.UseTls)
            {
                throw new InvalidOperationException(
                    $"Lab upstream isolation refused backbone={provider.Backbone}: UseTls=true is incompatible "
                    + "with the cleartext local ARTICLE peer (configure usessl=n). Destination was not rewritten.");
            }
        }
    }
}
