namespace VectorNNTPD.ReaderService.Authentication
{
    /// <summary>Source-generated authentication diagnostics. Never includes secrets.</summary>
    internal static partial class AuthenticationLogMessages
    {
        [LoggerMessage(EventId = 2302, Level = LogLevel.Debug, Message = "Authentication rejected: invalid credentials mechanism={Mechanism} user={Username} ip={ClientIp}")]
        public static partial void InvalidCredentials(ILogger logger, string mechanism, string username, string clientIp);

        [LoggerMessage(EventId = 2303, Level = LogLevel.Information, Message = "Authentication succeeded mechanism={Mechanism} user={Username} ip={ClientIp} byteLimit={ByteLimit} rateLimitBps={RateLimitBps} customer={CustomerId}")]
        public static partial void Succeeded(
            ILogger logger,
            string mechanism,
            string username,
            string clientIp,
            long byteLimit,
            int rateLimitBps,
            string customerId);

        [LoggerMessage(EventId = 2304, Level = LogLevel.Error, Message = "Authentication backend failure mechanism={Mechanism} user={Username}")]
        public static partial void BackendFailed(ILogger logger, Exception exception, string mechanism, string username);

        [LoggerMessage(EventId = 2305, Level = LogLevel.Error, Message = "nntpusers lookup failed user={Username}")]
        public static partial void UserLookupFailed(ILogger logger, Exception exception, string username);

        [LoggerMessage(EventId = 2306, Level = LogLevel.Information, Message = "Authentication admission rejected user={Username} reason={Reason} ip={ClientIp}")]
        public static partial void AdmissionRejected(ILogger logger, string username, string reason, string clientIp);
    }
}
