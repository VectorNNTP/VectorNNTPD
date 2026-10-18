using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Networking.Certificates;
using System.Net;

namespace VectorNNTPD.BackFillerService.Tests.TestDoubles
{
    /// <summary>
    /// Creates self-signed TLS certificates and TlsCertificateContextProvider fixtures for listener tests.
    /// </summary>

    internal static class TestListenerCertificates
    {
        /// <summary>
        /// Builds an exportable RSA-2048 self-signed server certificate with localhost and backfiller.test names.
        /// </summary>
        /// <param name="commonName">Subject CN. Defaults to backfiller.test.</param>
        internal static X509Certificate2 CreateSelfSigned(string commonName = "backfiller.test")
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=" + commonName,
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    critical: true));
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                    critical: false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddDnsName("backfiller.test");
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var created = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(30));
            return X509CertificateLoader.LoadPkcs12(
                created.Export(X509ContentType.Pfx, "test"),
                "test",
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        }

        /// <summary>Provider that has already published <see cref="CreateSelfSigned"/> so listener tests can handshake.</summary>
        /// <param name="commonName">Subject CN of the published certificate.</param>
        internal static TlsCertificateContextProvider CreatePublishedProvider(string commonName = "backfiller.test")
        {
            var provider = new TlsCertificateContextProvider(NullLogger<TlsCertificateContextProvider>.Instance);
            using var certificate = CreateSelfSigned(commonName);
            provider.PublishFromPfx(certificate.Export(X509ContentType.Pfx, "test"), "test");
            return provider;
        }

        /// <summary>Provider with no published certificate, so listener tests can observe the unavailable path.</summary>
        internal static TlsCertificateContextProvider CreateUnavailableProvider() =>
            new(NullLogger<TlsCertificateContextProvider>.Instance);
    }
}
