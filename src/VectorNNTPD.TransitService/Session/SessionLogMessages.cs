using System.Net;

namespace VectorNNTPD.TransitService.Session
{
    /// <summary>Source-generated session and transit-identification log messages.</summary>
    internal static partial class SessionLogMessages
    {
        [LoggerMessage(
            EventId = 1611,
            Level = LogLevel.Debug,
            Message = "NNTP session ended with an error for {Client}")]
        public static partial void SessionEndedWithError(ILogger logger, Exception exception, IPAddress Client);

        [LoggerMessage(
            EventId = 1612,
            Level = LogLevel.Debug,
            Message = "NNTP session ended; peer or transport disconnected for {Client}")]
        public static partial void SessionEndedByPeerDisconnect(ILogger logger, Exception exception, IPAddress Client);

        [LoggerMessage(
            EventId = 1700,
            Level = LogLevel.Information,
            Message = "Trusted Transit peers configured ({Count}): {Peers}")]
        public static partial void TransitPeersConfigured(ILogger logger, int Count, string Peers);

        [LoggerMessage(
            EventId = 1701,
            Level = LogLevel.Warning,
            Message = "Transit peer identification is ambiguous for {ClientAddress}; matching peers: {PeerNames}")]
        public static partial void TransitPeerAmbiguous(ILogger logger, IPAddress ClientAddress, string PeerNames);
    }
}
