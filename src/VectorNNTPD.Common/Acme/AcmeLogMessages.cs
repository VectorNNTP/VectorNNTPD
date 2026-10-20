using System.Net;

namespace VectorNNTPD.Common.Acme
{
    /// <summary>Source-generated ACME certificate, issuer, and DNS-01 log messages.</summary>
    internal static partial class AcmeLogMessages
    {
        /// <summary>
        /// Written when certificate startup finds the TLS listener disabled and returns without creating ACME state.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 1900,
            Level = LogLevel.Information,
            Message = "TLS disabled (BindPortTls=0); ACME certificate service idle")]
        internal static partial void TlsDisabledIdle(ILogger logger);

        /// <summary>
        /// Written immediately before the startup certificate ensure when TLS is enabled.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Directory">Configured ACME directory URL.</param>
        [LoggerMessage(
            EventId = 1901,
            Level = LogLevel.Information,
            Message = "Ensuring ACME certificate for TLS (directory={Directory})")]
        internal static partial void EnsuringCertificate(ILogger logger, string Directory);

        /// <summary>
        /// Written when startup provisioning throws after TLS was enabled, other than cancellation of the start token.
        /// The exception is rethrown after this event.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Failure">Sanitized failure text from <see cref="AcmeFailureSanitizer"/>.</param>
        [LoggerMessage(
            EventId = 1902,
            Level = LogLevel.Error,
            Message = "ACME certificate provisioning failed ({Failure})")]
        internal static partial void ProvisioningFailed(ILogger logger, string Failure);

        /// <summary>
        /// Written when waiting for the renewal loop during stop throws something other than <see cref="OperationCanceledException"/>.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">The wait failure.</param>
        [LoggerMessage(
            EventId = 1903,
            Level = LogLevel.Debug,
            Message = "ACME renewal loop ended with an error during stop")]
        internal static partial void RenewalLoopStopError(ILogger logger, Exception exception);

        /// <summary>
        /// Written after a renewal iteration persists a new certificate and publishes its PFX.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 1904,
            Level = LogLevel.Information,
            Message = "ACME renewal completed successfully")]
        internal static partial void RenewalCompleted(ILogger logger);

        /// <summary>
        /// Written when one renewal-loop iteration fails and the loop stays alive for the next interval.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Failure">Sanitized failure text from <see cref="AcmeFailureSanitizer"/>.</param>
        [LoggerMessage(
            EventId = 1905,
            Level = LogLevel.Warning,
            Message = "ACME renewal check failed ({Failure}); will retry next interval")]
        internal static partial void RenewalCheckFailed(ILogger logger, string Failure);

        /// <summary>
        /// Written when one authoritative TXT query fails and that server is skipped for the intersection.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">The lookup failure.</param>
        /// <param name="Name">TXT owner name that was queried.</param>
        /// <param name="Server">Authoritative endpoint that failed.</param>
        [LoggerMessage(
            EventId = 1906,
            Level = LogLevel.Debug,
            Message = "Authoritative TXT lookup failed for {Name} via {Server}")]
        internal static partial void AuthoritativeTxtLookupFailed(
            ILogger logger,
            Exception exception,
            string Name,
            IPEndPoint Server);

        /// <summary>
        /// Source-generated event for a failed resolution of an authoritative nameserver hostname.
        /// No caller in the current ACME implementation invokes this method.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">The resolution failure.</param>
        /// <param name="NsName">Nameserver hostname that failed to resolve.</param>
        [LoggerMessage(
            EventId = 1907,
            Level = LogLevel.Debug,
            Message = "Failed resolving authoritative NS address for {NsName}")]
        private static partial void AuthoritativeNsResolveFailed(ILogger logger, Exception exception, string NsName);

        /// <summary>
        /// Written after every DNS-01 TXT record in the current order has been created in Cloudflare.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="DomainCount">Number of challenge TXT records placed.</param>
        [LoggerMessage(
            EventId = 1908,
            Level = LogLevel.Information,
            Message = "ACME DNS-01 TXT records placed for {DomainCount} identifier(s)")]
        internal static partial void Dns01RecordsPlaced(ILogger logger, int DomainCount);

        /// <summary>
        /// Written after authoritative visibility confirms every placed challenge TXT value.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 1909,
            Level = LogLevel.Information,
            Message = "ACME DNS-01 authoritative visibility confirmed")]
        internal static partial void Dns01VisibilityConfirmed(ILogger logger);

        /// <summary>
        /// Written after each dns-01 challenge URL has been posted and before the order-ready poll.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="TimeoutSeconds">Readiness wait budget in whole seconds.</param>
        [LoggerMessage(
            EventId = 1910,
            Level = LogLevel.Information,
            Message = "ACME DNS-01 challenges triggered; waiting for authorization (timeout={TimeoutSeconds}s)")]
        internal static partial void Dns01ChallengesTriggered(ILogger logger, int TimeoutSeconds);

        /// <summary>
        /// Written when the order-ready poll returns, before the CSR is finalized.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 1911,
            Level = LogLevel.Information,
            Message = "ACME order ready for finalization")]
        internal static partial void OrderReady(ILogger logger);

        /// <summary>
        /// Written after the PEM chain has been downloaded and before DNS-01 cleanup and PFX validation.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 1912,
            Level = LogLevel.Information,
            Message = "ACME certificate finalized")]
        internal static partial void CertificateFinalized(ILogger logger);

        /// <summary>
        /// Written after <c>newAccount</c> returns an account URL during local account creation.
        /// The URL itself is not included in this event.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 1913,
            Level = LogLevel.Information,
            Message = "ACME account registered (uri configured).")]
        internal static partial void AccountRegistered(ILogger logger);

        /// <summary>
        /// Written when startup finds a usable certificate that is not yet inside the renewal window.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="NotAfter">Leaf not-after, UTC.</param>
        [LoggerMessage(
            EventId = 1914,
            Level = LogLevel.Information,
            Message = "Reusing existing server certificate; notAfter={NotAfter:o}")]
        internal static partial void ReusingExistingCertificate(ILogger logger, DateTimeOffset NotAfter);

        /// <summary>
        /// Written when the on-disk certificate is still usable but <see cref="CertificateStatus.DueForRenewal"/> is true, before replacement issuance.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 1915,
            Level = LogLevel.Information,
            Message = "Existing certificate due for renewal; issuing replacement")]
        internal static partial void ExistingCertificateDueForRenewal(ILogger logger);

        /// <summary>
        /// Written when no usable certificate is on disk, before issuance.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Reason"><see cref="CertificateStatus.Reason"/> or the storage/certificate failure category.</param>
        [LoggerMessage(
            EventId = 1916,
            Level = LogLevel.Information,
            Message = "No usable server certificate ({Reason}); requesting issuance")]
        internal static partial void NoUsableCertificate(ILogger logger, string Reason);

        /// <summary>
        /// Written when renewal issuance fails and a previously usable certificate is kept in memory.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Failure">Sanitized failure text from <see cref="AcmeFailureSanitizer"/>.</param>
        [LoggerMessage(
            EventId = 1917,
            Level = LogLevel.Warning,
            Message = "Certificate renewal failed ({Failure}); preserving existing certificate")]
        internal static partial void RenewalFailedPreservingExisting(ILogger logger, string Failure);

        /// <summary>
        /// Written after a new certificate is persisted and the in-memory generation counter is incremented.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Generation">In-process count of successful persists on this manager, not the filesystem generation id.</param>
        /// <param name="NotAfter">Leaf not-after, UTC.</param>
        [LoggerMessage(
            EventId = 1918,
            Level = LogLevel.Information,
            Message = "Server certificate ready generation={Generation} notAfter={NotAfter:o}")]
        internal static partial void ServerCertificateReady(ILogger logger, int Generation, DateTimeOffset NotAfter);

        /// <summary>
        /// Written when <c>newAccount</c> returns a Location URL and the client binds that URL as <c>kid</c>.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="AccountUrl">Account Location URL.</param>
        [LoggerMessage(
            EventId = 1919,
            Level = LogLevel.Debug,
            Message = "ACME protocol account registered accountUrl={AccountUrl}")]
        internal static partial void AcmeProtocolAccountRegistered(ILogger logger, string AccountUrl);

        /// <summary>
        /// Written when a JWS POST is rejected with <see cref="VectorNNTPD.Common.Acme.Protocol.AcmeErrorTypes.BadNonce"/> and will be signed again.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Url">Request URL that rejected the nonce.</param>
        [LoggerMessage(
            EventId = 1920,
            Level = LogLevel.Debug,
            Message = "ACME badNonce; retrying {Url}")]
        internal static partial void AcmeBadNonceRetry(ILogger logger, Uri Url);

        /// <summary>
        /// Written when an ACME HTTP call throws <see cref="HttpRequestException"/> or a non-caller <see cref="TaskCanceledException"/> and will be retried.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Delay">Backoff applied before the next attempt.</param>
        /// <param name="exception">The transport failure.</param>
        [LoggerMessage(
            EventId = 1921,
            Level = LogLevel.Debug,
            Message = "ACME transport error; retrying in {Delay}")]
        internal static partial void AcmeTransportRetry(ILogger logger, TimeSpan Delay, Exception exception);

        /// <summary>
        /// Written when the CA returns 500, 502, 503, or 504 and the delay is inside the retry window.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="StatusCode">HTTP status code.</param>
        /// <param name="Delay">Delay taken from Retry-After when it is positive and at most five minutes; otherwise exponential backoff.</param>
        [LoggerMessage(
            EventId = 1922,
            Level = LogLevel.Debug,
            Message = "ACME server status={StatusCode}; retrying in {Delay}")]
        internal static partial void AcmeServerErrorRetry(ILogger logger, int StatusCode, TimeSpan Delay);

        /// <summary>
        /// Written when a state-changing ACME POST may have been accepted but no trustworthy response was observed.
        /// The client must recover without replaying the same request.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Url">Request URL whose remote outcome is uncertain.</param>
        [LoggerMessage(
            EventId = 1923,
            Level = LogLevel.Warning,
            Message = "ACME request to {Url} may have been accepted; outcome is uncertain and the request will not be replayed")]
        internal static partial void AcmeAmbiguousOutcome(ILogger logger, Uri Url);
    }
}
