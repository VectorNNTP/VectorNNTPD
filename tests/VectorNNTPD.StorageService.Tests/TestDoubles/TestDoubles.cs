using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.Common.Networking.Certificates;

namespace VectorNNTPD.StorageService.Tests.TestDoubles
{

    internal static class TestListenerCertificates
    {
        internal static X509Certificate2 CreateSelfSigned(string commonName = "cache.test")
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
            san.AddDnsName("cache.test");
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

        internal static TlsCertificateContextProvider CreatePublishedProvider(string commonName = "cache.test")
        {
            var provider = new TlsCertificateContextProvider(NullLogger<TlsCertificateContextProvider>.Instance);
            using var certificate = CreateSelfSigned(commonName);
            provider.PublishFromPfx(certificate.Export(X509ContentType.Pfx, "test"), "test");
            return provider;
        }

        internal static TlsCertificateContextProvider CreateUnavailableProvider() =>
            new(NullLogger<TlsCertificateContextProvider>.Instance);
    }

    internal sealed class ImmediateAcmeReadyApplicationService : IApplicationService
    {
        private readonly IAcmeCertificateReadiness _readiness;
        private readonly IAcmeCertificatePublisher _publisher;

        public ImmediateAcmeReadyApplicationService(
            IAcmeCertificateReadiness readiness,
            IAcmeCertificatePublisher publisher)
        {
            ArgumentNullException.ThrowIfNull(readiness);
            ArgumentNullException.ThrowIfNull(publisher);
            _readiness = readiness;
            _publisher = publisher;
        }

        public string Name => "AcmeCertificate";

        public Task? Execution => null;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            using var certificate = TestListenerCertificates.CreateSelfSigned();
            _publisher.PublishFromPfx(certificate.Export(X509ContentType.Pfx, "test"), "test");
            _readiness.MarkReady();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    internal sealed class NoOpCloudflareDnsReconciler : ICloudflareDnsReconciler
    {
        public Task ReconcileAsync(
            string zoneId,
            string fqdn,
            ResolvedBindAddresses desired,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public bool DeletedPreExistingDuringLastReconcile => false;

        public Task RemoveAllRecordsForFqdnAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken,
            TimeSpan? operationTimeout = null) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListRecordIdsAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task DeleteRecordIdsAsync(
            string zoneId,
            string fqdn,
            IReadOnlyCollection<string> recordIds,
            CancellationToken cancellationToken,
            TimeSpan? operationTimeout = null) =>
            Task.CompletedTask;
    }

    internal sealed class FakeApplicationService : IApplicationService
    {
        private readonly Func<CancellationToken, Task>? _onStart;
        private readonly Func<CancellationToken, Task>? _onStop;

        public FakeApplicationService(
            string name,
            Func<CancellationToken, Task>? onStart = null,
            Func<CancellationToken, Task>? onStop = null)
        {
            Name = name;
            _onStart = onStart;
            _onStop = onStop;
        }

        public string Name { get; }

        public Task? Execution => null;

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            return _onStart?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return _onStop?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
    }

}
