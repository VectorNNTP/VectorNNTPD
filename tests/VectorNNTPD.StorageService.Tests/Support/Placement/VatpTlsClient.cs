using System.Net.Security;
using System.Security.Authentication;

namespace VectorNNTPD.StorageService.Tests.Support.Placement
{

    /// <summary>
    /// Shared outbound TLS client options and handshake for VATP cache connections.
    /// </summary>
    internal static class VatpTlsClient
    {
        internal static SslClientAuthenticationOptions CreateClientOptions(
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

        internal static async Task<SslStream> AuthenticateAsClientAsync(
            Stream inner,
            string host,
            TimeSpan timeout,
            RemoteCertificateValidationCallback? serverCertificateValidationCallback,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(inner);
            var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
            try
            {
                using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                tlsCts.CancelAfter(timeout);
                await ssl.AuthenticateAsClientAsync(
                        CreateClientOptions(host, serverCertificateValidationCallback),
                        tlsCts.Token)
                    .ConfigureAwait(false);
                return ssl;
            }
            catch
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }
}
