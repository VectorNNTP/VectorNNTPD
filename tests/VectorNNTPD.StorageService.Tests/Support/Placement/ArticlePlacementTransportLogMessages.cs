using Microsoft.Extensions.Logging;

namespace VectorNNTPD.StorageService.Tests.Support.Placement
{
    /// <summary>Source-generated logs for the long-lived placement connection.</summary>
    internal static partial class ArticlePlacementTransportLogMessages
    {
        [LoggerMessage(
            EventId = 2640,
            Level = LogLevel.Information,
            Message = "Placement connection established fqdn={Fqdn} vatpPort={VatpPort} outstandingLimit={OutstandingLimit}")]
        public static partial void Established(ILogger logger, string Fqdn, int VatpPort, int OutstandingLimit);

        [LoggerMessage(
            EventId = 2641,
            Level = LogLevel.Debug,
            Message = "Placement connection reused fqdn={Fqdn} vatpPort={VatpPort} activeStores={ActiveStores}")]
        public static partial void Reused(ILogger logger, string Fqdn, int VatpPort, int ActiveStores);

        [LoggerMessage(
            EventId = 2642,
            Level = LogLevel.Warning,
            Message = "Placement reconnect deferred fqdn={Fqdn} vatpPort={VatpPort} backoffMs={BackoffMs} failure={Failure}")]
        public static partial void ReconnectDeferred(ILogger logger, string Fqdn, int VatpPort, long BackoffMs, string Failure);

        [LoggerMessage(
            EventId = 2643,
            Level = LogLevel.Warning,
            Message = "Placement connection failed fqdn={Fqdn} vatpPort={VatpPort} failure={Failure}")]
        public static partial void ConnectionFailed(ILogger logger, string Fqdn, int VatpPort, string Failure);

        [LoggerMessage(
            EventId = 2644,
            Level = LogLevel.Information,
            Message = "Placement connection closed fqdn={Fqdn} vatpPort={VatpPort} outstanding={Outstanding}")]
        public static partial void Closed(ILogger logger, string Fqdn, int VatpPort, int Outstanding);

        [LoggerMessage(
            EventId = 2645,
            Level = LogLevel.Debug,
            Message = "Placement TAKETHIS completed fqdn={Fqdn} vatpPort={VatpPort} outcome={Outcome}")]
        public static partial void TakeThisCompleted(ILogger logger, string Fqdn, int VatpPort, string Outcome);

        [LoggerMessage(
            EventId = 2646,
            Level = LogLevel.Warning,
            Message = "Placement TAKETHIS rejected fqdn={Fqdn} vatpPort={VatpPort} outcome={Outcome} failure={Failure}")]
        public static partial void TakeThisRejected(ILogger logger, string Fqdn, int VatpPort, string Outcome, string? Failure);

        [LoggerMessage(
            EventId = 2647,
            Level = LogLevel.Warning,
            Message = "Placement TAKETHIS acknowledgement not observed fqdn={Fqdn} vatpPort={VatpPort}")]
        public static partial void TakeThisAmbiguous(ILogger logger, string Fqdn, int VatpPort);

        [LoggerMessage(
            EventId = 2648,
            Level = LogLevel.Information,
            Message = "Placement transport shutdown outstanding={Outstanding}")]
        public static partial void Shutdown(ILogger logger, int Outstanding);
    }
}
