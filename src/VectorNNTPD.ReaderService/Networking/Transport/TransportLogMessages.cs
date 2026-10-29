using System.Net;

namespace VectorNNTPD.ReaderService.Networking.Transport
{
    /// <summary>Source-generated NNTP connection transport log messages.</summary>
    internal static partial class TransportLogMessages
    {
        [LoggerMessage(
            EventId = 1500,
            Level = LogLevel.Debug,
            Message = "TLS handshake completed for {Remote}.")]
        public static partial void TlsHandshakeCompleted(ILogger logger, EndPoint? Remote);

        [LoggerMessage(
            EventId = 1501,
            Level = LogLevel.Debug,
            Message = "In-place TLS upgrade completed for {Remote}.")]
        public static partial void InPlaceTlsUpgradeCompleted(ILogger logger, EndPoint? Remote);

        [LoggerMessage(
            EventId = 1502,
            Level = LogLevel.Debug,
            Message = "In-place DEFLATE compression activated for {Remote}.")]
        public static partial void InPlaceDeflateActivated(ILogger logger, EndPoint? Remote);

        [LoggerMessage(
            EventId = 1503,
            Level = LogLevel.Debug,
            Message = "NNTP connection {Pump} pump ended with an error")]
        public static partial void PumpEndedWithError(ILogger logger, Exception exception, string Pump);
    }
}
