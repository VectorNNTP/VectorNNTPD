using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VectorNNTPD.ReaderService.Tests.Transport.Vatp;

internal static class VatpTestCertificates
{
    internal static X509Certificate2 CreateServerCertificate(string commonName = "backfiller.test")
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
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(commonName);
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(
            created.Export(X509ContentType.Pfx, "test"),
            "test",
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }
}
