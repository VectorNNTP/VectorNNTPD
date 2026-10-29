using Microsoft.Extensions.Logging;

namespace VectorNNTPD.TransitService.Storage
{
    /// <summary>Source-generated logs for the long-lived placement connection. Not per DATA frame.</summary>
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
            Message = "Placement STORE completed fqdn={Fqdn} vatpPort={VatpPort} streamId={StreamId} outcome={Outcome}")]
        public static partial void StoreCompleted(ILogger logger, string Fqdn, int VatpPort, uint StreamId, string Outcome);

        [LoggerMessage(
            EventId = 2646,
            Level = LogLevel.Warning,
            Message = "Placement STORE rejected fqdn={Fqdn} vatpPort={VatpPort} streamId={StreamId} outcome={Outcome} failure={Failure}")]
        public static partial void StoreRejected(ILogger logger, string Fqdn, int VatpPort, uint StreamId, string Outcome, string? Failure);

        [LoggerMessage(
            EventId = 2647,
            Level = LogLevel.Warning,
            Message = "Placement STORE acknowledgement not observed fqdn={Fqdn} vatpPort={VatpPort} streamId={StreamId}")]
        public static partial void StoreAmbiguous(ILogger logger, string Fqdn, int VatpPort, uint StreamId);

        [LoggerMessage(
            EventId = 2648,
            Level = LogLevel.Information,
            Message = "Placement transport shutdown outstanding={Outstanding}")]
        public static partial void Shutdown(ILogger logger, int Outstanding);
    }
}
