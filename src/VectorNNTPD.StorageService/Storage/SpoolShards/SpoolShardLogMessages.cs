using Microsoft.Extensions.Logging;

namespace VectorNNTPD.StorageService.Storage.SpoolShards
{

    /// <summary>Source-generated spool shard initialization logs. One message per spool, plus one summary.</summary>
    internal static partial class SpoolShardLogMessages
    {
        [LoggerMessage(
            EventId = 5620,
            Level = LogLevel.Information,
            Message = "Spool shard initialization completed for {Path}: created={Created} present={AlreadyPresent} elapsedMilliseconds={ElapsedMilliseconds}")]
        public static partial void SpoolCompleted(ILogger logger, string path, long created, long alreadyPresent, long elapsedMilliseconds);

        [LoggerMessage(
            EventId = 5621,
            Level = LogLevel.Error,
            Message = "Spool shard initialization failed for {Path}: created={Created} present={AlreadyPresent} failure={Failure}")]
        public static partial void SpoolFailed(ILogger logger, string path, long created, long alreadyPresent, string failure);

        [LoggerMessage(
            EventId = 5622,
            Level = LogLevel.Information,
            Message = "Spool shard initialization summary spools={Spools} created={Created} present={AlreadyPresent} elapsedMilliseconds={ElapsedMilliseconds}")]
        public static partial void Summary(ILogger logger, int spools, long created, long alreadyPresent, long elapsedMilliseconds);
    }
}
