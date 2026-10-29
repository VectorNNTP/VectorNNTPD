using System.ComponentModel.DataAnnotations;

namespace VectorNNTPD.ReaderService.Configuration
{
    /// <summary>
    /// Article ingestion worker and per-article size settings for the Transit ingestion path.
    /// </summary>
    /// <remarks>
    /// Used by <c>TAKETHIS</c> and <c>IHAVE</c> as the
    /// <c>network → queue → background ingestion worker</c> boundary.
    /// The worker does not write the article body to local durable storage.
    /// Queue admission is bounded by <see cref="NntpdOptions.TransitQueueMemoryLimit"/>,
    /// not by <see cref="QueueCapacity"/>.
    /// </remarks>
    public sealed class ArticleIngestionOptions
    {
        /// <summary>
        /// Legacy default article-count setting. Not an admission bound.
        /// </summary>
        public const int DefaultQueueCapacity = 256;

        /// <summary>Default maximum article payload size in bytes (4 MiB).</summary>
        public const int DefaultMaxArticleBytes = 4 * 1024 * 1024;

        /// <summary>
        /// Gets or sets a leftover article-count setting retained for binding compatibility.
        /// </summary>
        /// <remarks>
        /// Not an admission bound. The historical 256-entry cap was a memory-safety
        /// choke and has been replaced by <see cref="NntpdOptions.TransitQueueMemoryLimit"/>.
        /// Valid range remains <c>1–100000</c> so existing configuration files still validate.
        /// </remarks>
        [Range(1, 100_000)]
        public int QueueCapacity { get; set; } = DefaultQueueCapacity;

        /// <summary>
        /// Gets or sets the maximum accepted IHAVE/TAKETHIS article size in bytes
        /// (after dot-unstuffing).
        /// </summary>
        /// <remarks>
        /// Valid range is <c>1–104857600</c> (100 MiB). Default is 4 MiB.
        /// This is not the POST destuffed limit; POST uses
        /// <see cref="NntpdOptions.MaxArticleSize"/>.
        /// </remarks>
        [Range(1, 100 * 1024 * 1024)]
        public int MaxArticleBytes { get; set; } = DefaultMaxArticleBytes;

        /// <summary>Default outstanding STORE streams on one placement connection.</summary>
        public const int DefaultMaxOutstandingStoresPerConnection = 2;

        /// <summary>Default minimum concurrent ingestion workers.</summary>
        public const int DefaultMinWorkers = 2;

        /// <summary>Default maximum concurrent ingestion workers.</summary>
        public const int DefaultMaxWorkers = 32;

        /// <summary>Default maximum outstanding OverviewDB publisher confirms.</summary>
        public const int DefaultMaxPublishConcurrency = 32;

        /// <summary>Default worker-pool scaling sample interval in seconds.</summary>
        public const int DefaultScaleIntervalSeconds = 2;

        /// <summary>Default sustained high-pressure threshold (byte utilisation / wait signal).</summary>
        public const double DefaultScaleUpPressureThreshold = 0.40;

        /// <summary>Default sustained low-pressure threshold.</summary>
        public const double DefaultScaleDownPressureThreshold = 0.10;

        /// <summary>Default consecutive high-pressure intervals required before +1 worker.</summary>
        public const int DefaultScaleUpConsecutiveIntervals = 2;

        /// <summary>Default consecutive low-pressure intervals required before −1 worker.</summary>
        public const int DefaultScaleDownConsecutiveIntervals = 3;

        /// <summary>Default OverviewDB work-queue byte budget (1 GiB).</summary>
        public const long DefaultOverviewDbWorkQueueMemoryLimit = 1L * 1024 * 1024 * 1024;

        /// <summary>Default minimum OverviewDB RabbitMQ publisher workers.</summary>
        public const int DefaultOverviewDbMinPublisherWorkers = 2;

        /// <summary>Default maximum OverviewDB RabbitMQ publisher workers.</summary>
        public const int DefaultOverviewDbMaxPublisherWorkers = 32;

        /// <summary>Default outstanding OverviewDB publisher confirmation window.</summary>
        public const int DefaultOverviewDbPublisherBatchSize = 100;

        /// <summary>Default bounded OverviewDB publisher shutdown drain seconds.</summary>
        public const int DefaultOverviewDbPublisherShutdownSeconds = 2;

        /// <summary>
        /// Gets or sets how many STORE streams may be outstanding on one placement connection.
        /// </summary>
        /// <remarks>
        /// <para>Valid values are 1 and 2. One keeps a single STORE in flight. Two is the measured safe overlap.</para>
        /// <para>This does not enable placement. The spool writer still skips placement while that path is disabled.</para>
        /// </remarks>
        [Range(1, 2)]
        public int MaxOutstandingStoresPerConnection { get; set; } = DefaultMaxOutstandingStoresPerConnection;

        /// <summary>
        /// Gets or sets the minimum number of concurrent ingestion workers.
        /// </summary>
        /// <remarks>Valid range is <c>1–512</c>. Default is <see cref="DefaultMinWorkers"/>.</remarks>
        [Range(1, 512)]
        public int MinWorkers { get; set; } = DefaultMinWorkers;

        /// <summary>
        /// Gets or sets the maximum number of concurrent ingestion workers.
        /// </summary>
        /// <remarks>
        /// Must be greater than or equal to <see cref="MinWorkers"/>. Valid range is <c>1–512</c>.
        /// Default is <see cref="DefaultMaxWorkers"/>.
        /// </remarks>
        [Range(1, 512)]
        public int MaxWorkers { get; set; } = DefaultMaxWorkers;

        /// <summary>
        /// Gets or sets the maximum number of outstanding OverviewDB publisher confirms
        /// (legacy channel-pool bound retained for binding compatibility).
        /// </summary>
        /// <remarks>
        /// Prefer <see cref="OverviewDbPublisherBatchSize"/> for the asynchronous
        /// confirmation window. Valid range is <c>1–512</c>.
        /// Default is <see cref="DefaultMaxPublishConcurrency"/>.
        /// </remarks>
        [Range(1, 512)]
        public int MaxPublishConcurrency { get; set; } = DefaultMaxPublishConcurrency;

        /// <summary>
        /// Gets or sets the outstanding OverviewDB RabbitMQ confirmation window.
        /// </summary>
        /// <remarks>
        /// Maximum unconfirmed publishes in flight. Publishers stop dequeuing/publishing
        /// when the window is full and resume as confirms free capacity.
        /// Valid range is <c>1–10000</c>. Default is <see cref="DefaultOverviewDbPublisherBatchSize"/>.
        /// </remarks>
        [Range(1, 10_000)]
        public int OverviewDbPublisherBatchSize { get; set; } = DefaultOverviewDbPublisherBatchSize;

        /// <summary>
        /// Gets or sets the bounded OverviewDB publisher shutdown drain period in seconds.
        /// </summary>
        /// <remarks>
        /// After this period, outstanding OverviewDB confirms are abandoned.
        /// Valid range is <c>1–60</c>. Default is <see cref="DefaultOverviewDbPublisherShutdownSeconds"/>.
        /// </remarks>
        [Range(1, 60)]
        public int OverviewDbPublisherShutdownSeconds { get; set; } = DefaultOverviewDbPublisherShutdownSeconds;

        /// <summary>
        /// Gets or sets the in-process OverviewDB work-queue byte budget.
        /// </summary>
        /// <remarks>
        /// Bounded handoff between article workers and OverviewDB publisher workers.
        /// Not durable; not a RabbitMQ queue. Valid range is <c>1–9223372036854775807</c>.
        /// Default is <see cref="DefaultOverviewDbWorkQueueMemoryLimit"/>.
        /// </remarks>
        [Range(1, long.MaxValue)]
        public long OverviewDbWorkQueueMemoryLimit { get; set; } = DefaultOverviewDbWorkQueueMemoryLimit;

        /// <summary>
        /// Gets or sets the minimum OverviewDB RabbitMQ publisher workers.
        /// </summary>
        /// <remarks>Valid range is <c>1–512</c>. Default is <see cref="DefaultOverviewDbMinPublisherWorkers"/>.</remarks>
        [Range(1, 512)]
        public int OverviewDbMinPublisherWorkers { get; set; } = DefaultOverviewDbMinPublisherWorkers;

        /// <summary>
        /// Gets or sets the maximum OverviewDB RabbitMQ publisher workers.
        /// </summary>
        /// <remarks>
        /// Must be greater than or equal to <see cref="OverviewDbMinPublisherWorkers"/>.
        /// Scales independently of <see cref="MinWorkers"/>/<see cref="MaxWorkers"/>.
        /// Valid range is <c>1–512</c>. Default is <see cref="DefaultOverviewDbMaxPublisherWorkers"/>.
        /// </remarks>
        [Range(1, 512)]
        public int OverviewDbMaxPublisherWorkers { get; set; } = DefaultOverviewDbMaxPublisherWorkers;

        /// <summary>
        /// Gets or sets the worker-pool scaling sample interval in seconds.
        /// </summary>
        /// <remarks>
        /// Shared by article and OverviewDB publisher pool scalers.
        /// Valid range is <c>1–3600</c>. Default is <see cref="DefaultScaleIntervalSeconds"/>.
        /// </remarks>
        [Range(1, 3600)]
        public int ScaleIntervalSeconds { get; set; } = DefaultScaleIntervalSeconds;

        /// <summary>
        /// Gets or sets the sustained pressure threshold that triggers scale-up.
        /// </summary>
        /// <remarks>
        /// Pressure is the greater of queue byte utilisation and a waiting-producer
        /// signal. Must be strictly greater than <see cref="ScaleDownPressureThreshold"/>.
        /// Valid range is <c>0–1</c>.
        /// </remarks>
        [Range(0, 1)]
        public double ScaleUpPressureThreshold { get; set; } = DefaultScaleUpPressureThreshold;

        /// <summary>
        /// Gets or sets the sustained pressure threshold that triggers scale-down.
        /// </summary>
        /// <remarks>
        /// Must be strictly less than <see cref="ScaleUpPressureThreshold"/>.
        /// Valid range is <c>0–1</c>.
        /// </remarks>
        [Range(0, 1)]
        public double ScaleDownPressureThreshold { get; set; } = DefaultScaleDownPressureThreshold;

        /// <summary>
        /// Gets or sets how many consecutive high-pressure samples are required before adding one worker.
        /// </summary>
        [Range(1, 100)]
        public int ScaleUpConsecutiveIntervals { get; set; } = DefaultScaleUpConsecutiveIntervals;

        /// <summary>
        /// Gets or sets how many consecutive low-pressure samples are required before removing one worker.
        /// </summary>
        [Range(1, 100)]
        public int ScaleDownConsecutiveIntervals { get; set; } = DefaultScaleDownConsecutiveIntervals;
    }
}
