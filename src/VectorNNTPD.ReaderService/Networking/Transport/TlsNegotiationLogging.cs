using System.Net.Security;
using System.Security.Authentication;

namespace VectorNNTPD.ReaderService.Networking.Transport
{
    /// <summary>Formats negotiated <see cref="SslStream"/> parameters for operational logs.</summary>
    internal static class TlsNegotiationLogging
    {
        /// <summary>Builds the STARTTLS / TLS acceptance detail fragment.</summary>
        public static string FormatDetail(string tlsVersion, string cipher) =>
            $"TlsVersion={tlsVersion}, Cipher={cipher}";

        /// <summary>Maps <see cref="SslProtocols"/> to the operational <c>TLSv1.x</c> label.</summary>
        public static string FormatSslProtocol(SslProtocols protocol) =>
            protocol switch
            {
                SslProtocols.Tls13 => "TLSv1.3",
                SslProtocols.Tls12 => "TLSv1.2",
                _ => protocol.ToString(),
            };

        /// <summary>Formats the negotiated cipher suite name.</summary>
        public static string FormatCipherSuite(TlsCipherSuite cipherSuite) => cipherSuite.ToString();

        /// <summary>Captures version and cipher labels from an authenticated <see cref="SslStream"/>.</summary>
        public static void Capture(SslStream sslStream, out string tlsVersion, out string cipher)
        {
            ArgumentNullException.ThrowIfNull(sslStream);
            tlsVersion = FormatSslProtocol(sslStream.SslProtocol);
            cipher = FormatCipherSuite(sslStream.NegotiatedCipherSuite);
        }
    }
}
