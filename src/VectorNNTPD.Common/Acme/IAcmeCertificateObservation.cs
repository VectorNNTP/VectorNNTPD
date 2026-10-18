namespace VectorNNTPD.Common.Acme
{
    /// <summary>
    /// Optional application hook notified after ACME startup and each renewal check.
    /// </summary>
    /// <remarks>
    /// Common does not decide watchdog policy. BackFiller registers an implementation that maps
    /// <see cref="CertificateStatus"/> onto its systemd health flag. A missing registration leaves issuance unchanged.
    /// </remarks>
    internal interface IAcmeCertificateObservation
    {
        /// <summary>Receives a fresh certificate snapshot.</summary>
        /// <param name="status">Validation result for the current clock.</param>
        /// <param name="renewalReplacedCertificate">True when this attempt persisted and published a replacement.</param>
        void OnObserved(CertificateStatus status, bool renewalReplacedCertificate);

        /// <summary>Receives a failure while re-reading the certificate after a renewal exception.</summary>
        /// <param name="exception">The read failure. The renewal loop has already logged it.</param>
        void OnUnreadable(Exception exception);
    }
}
