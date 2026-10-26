namespace VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// Optional public switch for in-process silent-consumer monitoring under
    /// <c>BackFiller:SilentConsumerMonitor</c>.
    /// </summary>
    /// <remarks>
    /// Virtual host, Management base URL, credentials, and Article Work queues are not configured here.
    /// Those come from <c>RabbitMQ</c> options (hosts/vhost/Management) and the MySQL-backed provider catalog.
    /// Detection thresholds are code defaults, not operator JSON.
    /// </remarks>
    internal sealed class SilentConsumerMonitorOptions
    {
        /// <summary>Nested configuration key under <c>BackFiller</c>.</summary>
        internal const string SectionRelativeName = "SilentConsumerMonitor";

        /// <summary>
        /// When <see langword="false"/>, the hosted monitor loop exits after a start log.
        /// Default <see langword="true"/>.
        /// </summary>
        public bool Enabled { get; set; } = true;
    }
}
