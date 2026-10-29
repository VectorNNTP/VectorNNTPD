namespace VectorNNTPD.ReaderService.Transit
{
    /// <summary>Source-generated Transit peer-state diagnostics. Never includes secrets.</summary>
    internal static partial class TransitPeerStateLogMessages
    {
        [LoggerMessage(EventId = 1710, Level = LogLevel.Debug, Message = "Transit peer-state admission accepted identifier={Identifier} limit={MaxIncoming} node={NodeId}")]
        public static partial void TransitPeerAdmitted(ILogger logger, string identifier, int maxIncoming, string nodeId);

        [LoggerMessage(EventId = 1711, Level = LogLevel.Information, Message = "Transit peer-state admission rejected identifier={Identifier} limit={MaxIncoming} node={NodeId}")]
        public static partial void TransitPeerRejected(ILogger logger, string identifier, int maxIncoming, string nodeId);

        [LoggerMessage(EventId = 1712, Level = LogLevel.Warning, Message = "Transit peer-state admission failed closed; cluster membership unavailable identifier={Identifier} limit={MaxIncoming} node={NodeId}")]
        public static partial void TransitPeerUnavailable(ILogger logger, string identifier, int maxIncoming, string nodeId);

        [LoggerMessage(EventId = 1713, Level = LogLevel.Debug, Message = "Transit peer-state ownership released identifier={Identifier} node={NodeId}")]
        public static partial void TransitPeerReleased(ILogger logger, string identifier, string nodeId);

        [LoggerMessage(EventId = 1714, Level = LogLevel.Debug, Message = "Transit peer-state lease renewed identifier={Identifier} node={NodeId}")]
        public static partial void TransitPeerLeaseRenewed(ILogger logger, string identifier, string nodeId);

        [LoggerMessage(EventId = 1715, Level = LogLevel.Warning, Message = "Transit peer-state lease renewal failed identifier={Identifier} node={NodeId}")]
        public static partial void TransitPeerLeaseRenewFailed(ILogger logger, Exception exception, string identifier, string nodeId);

        [LoggerMessage(EventId = 1716, Level = LogLevel.Warning, Message = "Transit peer-state lease renewal skipped; membership unavailable identifier={Identifier} node={NodeId}")]
        public static partial void TransitPeerLeaseRenewUnavailable(ILogger logger, string identifier, string nodeId);

        [LoggerMessage(EventId = 1717, Level = LogLevel.Warning, Message = "Transit peer-state lease lost; ownership not extended identifier={Identifier} node={NodeId}")]
        public static partial void TransitPeerLeaseLost(ILogger logger, string identifier, string nodeId);

        [LoggerMessage(EventId = 1718, Level = LogLevel.Warning, Message = "Transit peer-state release-owner failed identifier={Identifier} owner={OwnerId}")]
        public static partial void TransitPeerReleaseOwnerFailed(ILogger logger, Exception exception, string identifier, string ownerId);

        [LoggerMessage(EventId = 1719, Level = LogLevel.Information, Message = "Transit peer-state lease renewal started interval={Interval}")]
        public static partial void TransitPeerRenewalStarted(ILogger logger, TimeSpan interval);

        [LoggerMessage(EventId = 1720, Level = LogLevel.Information, Message = "Transit peer-state lease renewal stopped")]
        public static partial void TransitPeerRenewalStopped(ILogger logger);

        [LoggerMessage(EventId = 1721, Level = LogLevel.Warning, Message = "Transit peer-state lease renewal pass failed")]
        public static partial void TransitPeerRenewalPassFailed(ILogger logger, Exception exception);
    }
}
