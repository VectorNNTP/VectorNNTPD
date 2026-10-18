using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Acme;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.Acme;

namespace VectorNNTPD.BackFillerService.Tests.TestDoubles
{
    /// <summary>
    /// Marks ACME ready without contacting Let's Encrypt so host composition tests can start.
    /// Publishes a test certificate through the shared Common TLS publisher.
    /// </summary>
    internal sealed class ImmediateAcmeReadyApplicationService : IApplicationService
    {
        /// <summary>Marked ready after the self-signed publish on start.</summary>
        private readonly IAcmeCertificateReadiness _readiness;
        /// <summary>Receives exported PFX; does not perform ACME HTTP validation.</summary>
        private readonly IAcmeCertificatePublisher _publisher;
        /// <summary>Records ACME-ready startup stage.</summary>
        private readonly IBackFillerStartupJournal _journal;

        /// <summary>Wires readiness, publisher, and journal used on start.</summary>
        /// <param name="readiness">Marked ready after a self-signed publish.</param>
        /// <param name="publisher">Receives exported PFX bytes; not a real ACME issuance path.</param>
        /// <param name="journal">Records <see cref="BackFillerStartupStages.AcmeCertificateReady"/>.</param>
        public ImmediateAcmeReadyApplicationService(
            IAcmeCertificateReadiness readiness,
            IAcmeCertificatePublisher publisher,
            IBackFillerStartupJournal journal)
        {
            ArgumentNullException.ThrowIfNull(readiness);
            ArgumentNullException.ThrowIfNull(publisher);
            ArgumentNullException.ThrowIfNull(journal);
            _readiness = readiness;
            _publisher = publisher;
            _journal = journal;
        }

        /// <inheritdoc />
        public string Name => "AcmeCertificate";

        /// <inheritdoc />
        public Task? Execution => null;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            using var certificate = TestListenerCertificates.CreateSelfSigned();
            _publisher.PublishFromPfx(certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "test"), "test");
            _readiness.MarkReady();
            _journal.Record(BackFillerStartupStages.AcmeCertificateReady);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
