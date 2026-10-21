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

        /// <summary>Clock used when re-checking material not-after against a possibly stale status snapshot.</summary>
        private readonly TimeProvider _time;

        /// <summary>One while keep-alives are blocked for the certificate. Used so the log is written only on a change.</summary>
        private int _blocked;

        /// <summary>Retains the health flag, logger, and clock.</summary>
        /// <param name="health">Watchdog health updated by each observation.</param>
        /// <param name="logger">Logger for events 1924 and 1925.</param>
        /// <param name="time">
        /// Clock for not-after checks. Null uses <see cref="TimeProvider.System"/>.
        /// Should match the clock injected into <paramref name="health"/>.
        /// </param>
        public AcmeCertificateWatchdog(
            BackFillerApplicationHealth health,
            ILogger<AcmeCertificateWatchdog> logger,
            TimeProvider? time = null)
        {
            ArgumentNullException.ThrowIfNull(health);
            ArgumentNullException.ThrowIfNull(logger);
            _health = health;
            _logger = logger;
            _time = time ?? TimeProvider.System;
        }

        /// <summary>
        /// True when watchdog keep-alives must stop for this certificate snapshot.
        /// A usable, unexpired certificate never blocks, including when renewal is due and the last attempt
        /// did not replace it. Missing, unreadable, and expired material always blocks. Expiry is evaluated
        /// against <paramref name="utcNow"/> (or the system clock) so a status whose <see cref="CertificateStatus.Usable"/>
        /// was computed earlier cannot keep keep-alives alive past <see cref="CertificateMaterial.NotAfter"/>.
        /// </summary>
        /// <param name="status">Validation snapshot.</param>
        /// <param name="renewalReplacedCertificate">
        /// True when this attempt persisted and published a replacement. Unused while <paramref name="status"/> is usable;
        /// retained for callers that already report replacement outcomes.
        /// </param>
        /// <param name="utcNow">
        /// Instant used for the not-after check. Null uses <see cref="DateTimeOffset.UtcNow"/>.
        /// </param>
        /// <returns>True when keep-alives must stop.</returns>
        internal static bool CertificateRequiresWatchdogBlock(
            CertificateStatus status,
            bool renewalReplacedCertificate,
            DateTimeOffset? utcNow = null)
        {
            ArgumentNullException.ThrowIfNull(status);
            _ = renewalReplacedCertificate;
            if (!status.Usable || status.Material is null)
            {
                return true;
            }

            var now = utcNow ?? DateTimeOffset.UtcNow;
            return now >= status.Material.NotAfter;
        }

        /// <inheritdoc />
        public void OnObserved(CertificateStatus status, bool renewalReplacedCertificate)
        {
            var now = _time.GetUtcNow();
            if (CertificateRequiresWatchdogBlock(status, renewalReplacedCertificate, now))
            {
                _health.MarkCertificateUnavailable();
                if (Interlocked.Exchange(ref _blocked, 1) == 0)
                {
                    var reason = status.Usable && status.Material is not null && now >= status.Material.NotAfter
                        ? "expired"
                        : status.Reason;
                    AcmeCertificateWatchdogLogMessages.CertificateWatchdogBlocked(_logger, reason);
                }

                return;
            }

            _health.MarkCertificateAvailable(status.Material!.NotAfter);
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
