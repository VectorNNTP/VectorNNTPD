using VectorNNTPD.BackFillerService.Hosting.Systemd;
using VectorNNTPD.Common.Acme;

namespace VectorNNTPD.BackFillerService.Acme
{
    /// <summary>
    /// Maps ACME certificate snapshots onto <see cref="BackFillerApplicationHealth"/> watchdog suppression.
    /// </summary>
    internal sealed class AcmeCertificateWatchdog : IAcmeCertificateObservation
    {
        /// <summary>Health flag updated when the published certificate blocks or clears keep-alives.</summary>
        private readonly BackFillerApplicationHealth _health;

        /// <summary>Logger for the block and restore events.</summary>
        private readonly ILogger<AcmeCertificateWatchdog> _logger;

        /// <summary>One while keep-alives are blocked for the certificate. Used so the log is written only on a change.</summary>
        private int _blocked;

        /// <summary>Retains the health flag and logger.</summary>
        /// <param name="health">Watchdog health updated by each observation.</param>
        /// <param name="logger">Logger for events 1924 and 1925.</param>
        public AcmeCertificateWatchdog(BackFillerApplicationHealth health, ILogger<AcmeCertificateWatchdog> logger)
        {
            ArgumentNullException.ThrowIfNull(health);
            ArgumentNullException.ThrowIfNull(logger);
            _health = health;
            _logger = logger;
        }

        /// <summary>
        /// True when watchdog keep-alives must stop for this certificate snapshot.
        /// A usable certificate outside the renewal window does not block. A usable certificate inside the window
        /// blocks only when this attempt did not replace it. Missing, unreadable, and expired material always blocks.
        /// </summary>
        /// <param name="status">Fresh validation snapshot.</param>
        /// <param name="renewalReplacedCertificate">True when this attempt persisted and published a replacement.</param>
        /// <returns>True when keep-alives must stop.</returns>
        internal static bool CertificateRequiresWatchdogBlock(CertificateStatus status, bool renewalReplacedCertificate)
        {
            ArgumentNullException.ThrowIfNull(status);
            if (!status.Usable || status.Material is null)
            {
                return true;
            }

            return status.DueForRenewal && !renewalReplacedCertificate;
        }

        /// <inheritdoc />
        public void OnObserved(CertificateStatus status, bool renewalReplacedCertificate)
        {
            if (CertificateRequiresWatchdogBlock(status, renewalReplacedCertificate))
            {
                _health.MarkCertificateUnavailable();
                if (Interlocked.Exchange(ref _blocked, 1) == 0)
                {
                    AcmeCertificateWatchdogLogMessages.CertificateWatchdogBlocked(_logger, status.Reason);
                }

                return;
            }

            _health.MarkCertificateAvailable();
            if (Interlocked.Exchange(ref _blocked, 0) == 1)
            {
                AcmeCertificateWatchdogLogMessages.CertificateWatchdogRestored(_logger);
            }
        }

        /// <inheritdoc />
        public void OnUnreadable(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            _health.MarkCertificateUnavailable();
            if (Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                AcmeCertificateWatchdogLogMessages.CertificateWatchdogBlocked(_logger, exception.GetType().Name);
            }
        }
    }

    /// <summary>Source-generated certificate watchdog log messages. Event ids match the previous ACME service messages.</summary>
    internal static partial class AcmeCertificateWatchdogLogMessages
    {
        /// <summary>Error event 1924 when the published certificate starts blocking keep-alives.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Reason">Certificate status category such as <c>expired</c> or <c>renewal_threshold</c>.</param>
        [LoggerMessage(
            EventId = 1924,
            Level = LogLevel.Error,
            Message = "Published TLS certificate is blocking watchdog keep-alives ({Reason})")]
        internal static partial void CertificateWatchdogBlocked(ILogger logger, string Reason);

        /// <summary>Information event 1925 when a usable certificate clears the watchdog block.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 1925,
            Level = LogLevel.Information,
            Message = "Published TLS certificate is usable; watchdog keep-alives may resume")]
        internal static partial void CertificateWatchdogRestored(ILogger logger);
    }
}
