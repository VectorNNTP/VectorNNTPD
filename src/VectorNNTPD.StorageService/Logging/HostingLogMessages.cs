namespace VectorNNTPD.StorageService.Logging
{

    /// <summary>
    /// Source-generated structured log messages for Generic Host integration events.
    /// </summary>
    internal static partial class HostingLogMessages
    {
        [LoggerMessage(
            EventId = 1200,
            Level = LogLevel.Information,
            Message = "Host starting application {ApplicationName}")]
        public static partial void HostStartingApplication(ILogger logger, string ApplicationName);

        [LoggerMessage(
            EventId = 1201,
            Level = LogLevel.Information,
            Message = "Application entered Running state after {ElapsedMs} ms. Host startup continuing")]
        public static partial void ApplicationEnteredRunning(ILogger logger, long ElapsedMs);

        [LoggerMessage(
            EventId = 1202,
            Level = LogLevel.Information,
            Message = "Hosted service execution canceled due to host shutdown")]
        public static partial void HostedServiceExecutionCanceled(ILogger logger);

        [LoggerMessage(
            EventId = 1203,
            Level = LogLevel.Critical,
            Message = "Background hosted service observed unexpected application-service termination")]
        public static partial void UnexpectedApplicationServiceTermination(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 1204,
            Level = LogLevel.Information,
            Message = "Host stopping application {ApplicationName}")]
        public static partial void HostStoppingApplication(ILogger logger, string ApplicationName);

        [LoggerMessage(
            EventId = 1205,
            Level = LogLevel.Information,
            Message = "Application shutdown requested exactly once for {ApplicationName}")]
        public static partial void ShutdownRequestedOnce(ILogger logger, string ApplicationName);

        [LoggerMessage(
            EventId = 1206,
            Level = LogLevel.Warning,
            Message = "Unexpected service termination observed, but {Option} is disabled")]
        public static partial void UnexpectedTerminationOptionDisabled(ILogger logger, string Option);

        [LoggerMessage(
            EventId = 1207,
            Level = LogLevel.Critical,
            Message = "Requesting host stop due to unexpected application-service termination")]
        public static partial void RequestingHostStop(ILogger logger);

        [LoggerMessage(
            EventId = 1208,
            Level = LogLevel.Error,
            Message = "Application shutdown completed with failure")]
        public static partial void ShutdownCompletedWithFailure(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 1209,
            Level = LogLevel.Information,
            Message = "Application logging initialized Application={Application} Provider={Provider} Category={Category} Environment={Environment} ContentRoot={ContentRoot}")]
        public static partial void LoggingInitialized(
            ILogger logger,
            string Application,
            string Provider,
            string Category,
            string Environment,
            string ContentRoot);

        [LoggerMessage(
            EventId = 1210,
            Level = LogLevel.Debug,
            Message = "StorageServer bind addresses configured Tokens={Tokens} IPv4={IPv4} IPv6={IPv6}")]
        public static partial void BindAddressesConfigured(ILogger logger, string Tokens, string IPv4, string IPv6);

        [LoggerMessage(
            EventId = 1211,
            Level = LogLevel.Debug,
            Message = "StorageServer TLS listener endpoints planned Endpoints={Endpoints}")]
        public static partial void TlsListenerEndpointsPlanned(ILogger logger, string Endpoints);

        [LoggerMessage(
            EventId = 1212,
            Level = LogLevel.Debug,
            Message = "StorageServer Cloudflare DNS addresses A={IPv4} AAAA={IPv6}")]
        public static partial void CloudflareDnsAddressesResolved(ILogger logger, string IPv4, string IPv6);
    }
}
