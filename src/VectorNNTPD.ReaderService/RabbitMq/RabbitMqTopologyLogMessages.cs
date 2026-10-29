namespace VectorNNTPD.ReaderService.RabbitMq
{
    /// <summary>Source-generated RabbitMQ topology log messages. Never includes credentials.</summary>
    internal static partial class RabbitMqTopologyLogMessages
    {
        [LoggerMessage(
            EventId = 2820,
            Level = LogLevel.Information,
            Message = "Establishing NNTPD RabbitMQ topology (fanoutExchanges={ExchangeCount})")]
        public static partial void Establishing(ILogger logger, int ExchangeCount);

        [LoggerMessage(
            EventId = 2821,
            Level = LogLevel.Information,
            Message = "NNTPD RabbitMQ topology established (fanoutExchanges={ExchangeCount}, generation={Generation})")]
        public static partial void Established(ILogger logger, int ExchangeCount, long Generation);

        [LoggerMessage(
            EventId = 2822,
            Level = LogLevel.Error,
            Message = "NNTPD RabbitMQ topology declaration failed (endpoint={Endpoint}, exchange={Exchange}, queue={Queue})")]
        public static partial void DeclarationFailed(
            ILogger logger,
            Exception exception,
            string Endpoint,
            string Exchange,
            string Queue);

        [LoggerMessage(
            EventId = 2823,
            Level = LogLevel.Error,
            Message = "RabbitMQ connection is not ready for topology declaration")]
        public static partial void ConnectionNotReady(ILogger logger);

        [LoggerMessage(
            EventId = 2824,
            Level = LogLevel.Information,
            Message = "Declared OverviewDB handoff queue {Queue} (generation={Generation})")]
        public static partial void OverviewQueueDeclared(ILogger logger, string Queue, long Generation);

        [LoggerMessage(
            EventId = 2825,
            Level = LogLevel.Information,
            Message = "Declared cache broadcast exchange {Exchange} (generation={Generation})")]
        public static partial void CacheBroadcastDeclared(ILogger logger, string Exchange, long Generation);

        [LoggerMessage(
            EventId = 2826,
            Level = LogLevel.Information,
            Message = "Declared cache requests fanout exchange {Exchange} (generation={Generation})")]
        public static partial void CacheRequestsDeclared(ILogger logger, string Exchange, long Generation);
    }
}
