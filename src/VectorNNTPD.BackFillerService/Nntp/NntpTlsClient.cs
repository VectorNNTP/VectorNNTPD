using System.Security.Authentication;
using System.Net.Security;

namespace VectorNNTPD.BackFillerService.Nntp
{
    /// <summary>
    /// Shared outbound TLS client options and handshake used by implicit TLS connect
    /// and STARTTLS upgrade. Platform certificate validation is the default.
    /// </summary>
    internal static class NntpTlsClient
    {
        /// <summary>Builds the production client TLS options for <paramref name="host"/>.</summary>
        /// <param name="host">SNI and certificate target host.</param>
        /// <param name="serverCertificateValidationCallback">
        /// Callback assigned when non-null. A null callback leaves platform validation in place.
        /// </param>
        /// <returns>
        /// Options with <see cref="SslClientAuthenticationOptions.TargetHost"/> set to <paramref name="host"/>
        /// and protocols TLS 1.2 and TLS 1.3.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="host"/> is empty or white space.</exception>
        private static SslClientAuthenticationOptions CreateClientOptions(
            string host,
            RemoteCertificateValidationCallback? serverCertificateValidationCallback)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(host);
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            };
            if (serverCertificateValidationCallback is not null)
            {
                options.RemoteCertificateValidationCallback = serverCertificateValidationCallback;
            }

            return options;
        }

        /// <summary>
        /// Wraps <paramref name="inner"/> in <see cref="SslStream"/> and authenticates as client.
        /// On failure the wrapper is disposed and owns (closes) <paramref name="inner"/>.
        /// </summary>
        /// <param name="inner">
        /// Stream to wrap. Ownership transfers to the returned <see cref="SslStream"/>, including when authentication fails.
        /// </param>
        /// <param name="host">Target host passed to <see cref="CreateClientOptions"/>.</param>
        /// <param name="timeout">
        /// Handshake budget applied with <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>
        /// on a token linked to <paramref name="cancellationToken"/>.
        /// </param>
        /// <param name="serverCertificateValidationCallback">
        /// Optional certificate callback. Null uses platform validation.
        /// </param>
        /// <param name="cancellationToken">Cancels the handshake. Combined with <paramref name="timeout"/>.</param>
        /// <param name="streamFactory">
        /// When non-null, builds the wrapper around <paramref name="inner"/>. The wrapper must own <paramref name="inner"/>.
        /// Null uses <see cref="SslStream"/> with the inner stream closed on dispose. Production callers pass null.
        /// </param>
        /// <returns>The authenticated stream. It owns <paramref name="inner"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="host"/> is empty or white space.</exception>
        /// <remarks>
        /// Any exception from client authentication disposes the <see cref="SslStream"/> and therefore
        /// <paramref name="inner"/>, then propagates.
        /// </remarks>
        internal static async Task<SslStream> AuthenticateAsClientAsync(
            Stream inner,
            string host,
            TimeSpan timeout,
            RemoteCertificateValidationCallback? serverCertificateValidationCallback,
            CancellationToken cancellationToken,
            Func<Stream, SslStream>? streamFactory = null)
        {
            ArgumentNullException.ThrowIfNull(inner);
            // Ownership transfer uses DisposeAsync (not sync Dispose) so STARTTLS teardown matches
            // SslStream's async dispose path. cs/missed-using-statement targets IDisposable.Dispose
            // in finally; DisposeAsync here is intentional and preserves handshake-cancel cleanup.
            SslStream? pendingDispose =
                streamFactory?.Invoke(inner) ?? new SslStream(inner, leaveInnerStreamOpen: false);
            try
            {
                using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                tlsCts.CancelAfter(timeout);
                await pendingDispose.AuthenticateAsClientAsync(
                        CreateClientOptions(host, serverCertificateValidationCallback),
                        tlsCts.Token)
                    .ConfigureAwait(false);
                var authenticated = pendingDispose;
                pendingDispose = null;
                return authenticated;
            }
            finally
            {
                if (pendingDispose is not null)
                {
                    await pendingDispose.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
