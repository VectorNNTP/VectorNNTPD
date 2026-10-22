namespace VectorNNTPD.StorageService.Storage
{

    /// <summary>Source-generated StorageServer advertisement publisher log messages.</summary>
    internal static partial class StorageServerAdvertisementLogMessages
    {
        [LoggerMessage(
            EventId = 2900,
            Level = LogLevel.Information,
            Message = "StorageServer advertisement publisher started (exchange={Exchange})")]
        public static partial void Started(ILogger logger, string Exchange);

        [LoggerMessage(
            EventId = 2901,
            Level = LogLevel.Information,
            Message = "StorageServer advertisement publisher stopped")]
        public static partial void Stopped(ILogger logger);

        [LoggerMessage(
            EventId = 2902,
            Level = LogLevel.Warning,
            Message = "StorageServer advertisement publish failed")]
        public static partial void PublishFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2904,
            Level = LogLevel.Information,
            Message = "StorageServer lifecycle Draining published (fqdn={Fqdn}, serverId={ServerId})")]
        public static partial void LifecycleDrainingPublished(ILogger logger, string Fqdn, int ServerId);

        [LoggerMessage(
            EventId = 2905,
            Level = LogLevel.Warning,
            Message = "StorageServer lifecycle announcement failed (state={State})")]
        public static partial void LifecycleAnnouncementFailed(ILogger logger, Exception exception, string State);
    }
}
