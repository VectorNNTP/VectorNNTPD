namespace VectorNNTPD.ReaderService.SessionState
{
    /// <summary>Source-generated session-state diagnostics. Never includes secrets.</summary>
    internal static partial class SessionStateLogMessages
    {
        [LoggerMessage(EventId = 2310, Level = LogLevel.Debug, Message = "Session-state admission accepted locally user={Username} ip={SourceIp} limit={SrcIpLimit} node={NodeId}")]
        public static partial void SessionAdmittedLocal(ILogger logger, string username, string sourceIp, int srcIpLimit, string nodeId);

        [LoggerMessage(EventId = 2311, Level = LogLevel.Debug, Message = "Session-state admission accepted via cluster membership user={Username} ip={SourceIp} limit={SrcIpLimit} node={NodeId} path={Path}")]
        public static partial void SessionAdmittedDistributed(ILogger logger, string username, string sourceIp, int srcIpLimit, string nodeId, string path);

        [LoggerMessage(EventId = 2312, Level = LogLevel.Information, Message = "Session-state source-address admission rejected user={Username} ip={SourceIp} limit={SrcIpLimit} node={NodeId}")]
        public static partial void SourceAddressRejected(ILogger logger, string username, string sourceIp, int srcIpLimit, string nodeId);

        [LoggerMessage(EventId = 2323, Level = LogLevel.Information, Message = "Session admission rejected user={Username} ip={SourceIp} limit={SessionLimit} node={NodeId}")]
        public static partial void SessionAdmissionRejected(ILogger logger, string username, string sourceIp, int sessionLimit, string nodeId);

        [LoggerMessage(EventId = 2313, Level = LogLevel.Warning, Message = "Session-state admission failed closed; cluster membership unavailable user={Username} ip={SourceIp} limit={SrcIpLimit} node={NodeId}")]
        public static partial void SessionStateUnavailable(ILogger logger, string username, string sourceIp, int srcIpLimit, string nodeId);

        [LoggerMessage(EventId = 2314, Level = LogLevel.Debug, Message = "Session-state node ownership released user={Username} ip={SourceIp} node={NodeId}")]
        public static partial void SessionStateReleased(ILogger logger, string username, string sourceIp, string nodeId);

        [LoggerMessage(EventId = 2315, Level = LogLevel.Debug, Message = "Session-state lease renewed user={Username} ip={SourceIp} node={NodeId}")]
        public static partial void SessionStateLeaseRenewed(ILogger logger, string username, string sourceIp, string nodeId);

        [LoggerMessage(EventId = 2316, Level = LogLevel.Warning, Message = "Session-state lease renewal failed user={Username} ip={SourceIp} node={NodeId}")]
        public static partial void SessionStateLeaseRenewFailed(ILogger logger, Exception exception, string username, string sourceIp, string nodeId);

        [LoggerMessage(EventId = 2317, Level = LogLevel.Warning, Message = "Session-state lease renewal skipped; membership unavailable user={Username} ip={SourceIp} node={NodeId}")]
        public static partial void SessionStateLeaseRenewUnavailable(ILogger logger, string username, string sourceIp, string nodeId);

        [LoggerMessage(EventId = 2318, Level = LogLevel.Warning, Message = "Session-state lease lost; local hot path invalidated user={Username} ip={SourceIp} node={NodeId}")]
        public static partial void SessionStateLeaseLost(ILogger logger, string username, string sourceIp, string nodeId);

        [LoggerMessage(EventId = 2319, Level = LogLevel.Warning, Message = "Session-state release failed user={Username} ip={SourceIp}")]
        public static partial void SessionStateReleaseFailed(ILogger logger, Exception exception, string username, string sourceIp);

        [LoggerMessage(EventId = 2320, Level = LogLevel.Information, Message = "Session-state lease renewal started interval={Interval}")]
        public static partial void SessionStateRenewalStarted(ILogger logger, TimeSpan interval);

        [LoggerMessage(EventId = 2321, Level = LogLevel.Information, Message = "Session-state lease renewal stopped")]
        public static partial void SessionStateRenewalStopped(ILogger logger);

        [LoggerMessage(EventId = 2322, Level = LogLevel.Warning, Message = "Session-state lease renewal pass failed")]
        public static partial void SessionStateRenewalPassFailed(ILogger logger, Exception exception);
    }
}
