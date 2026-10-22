namespace VectorNNTPD.StorageService.Configuration
{

    /// <summary>
    /// Bounds and control intervals for post-acknowledgement spool persistence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// VATP RESULT is still sent when an article enters <c>AcceptedArticleQueue</c>.
    /// These settings bound later disk work. They do not make that RESULT durable.
    /// </para>
    /// <para>
    /// At 10 Gbps the payload rate is 1.25 GB/s. An average 750 KiB article
    /// (<c>750 × 1024 = 768_000</c> bytes) is about 1,628 articles/s. The default
    /// 1 GiB queue holds about 1,398 such articles. STORE admission reserves that
    /// article's META <c>ArtSize</c> before WINDOW credit is granted, so a full queue
    /// does not receive the body. <c>TryCommit</c> moves the reservation into the queue
    /// without adding the bytes twice. A dequeued article leaves the budget.
    /// Path rewrite keeps a rented second buffer until publish returns, then returns it
    /// to a pool capped at eight arrays per size. Live memory is still the original plus
    /// that buffer. The pool retains at most eight arrays of each size that has been used.
    /// </para>
    /// </remarks>
    public sealed class SpoolPersistenceOptions
    {
        /// <summary>Default queued-byte budget. 1 GiB.</summary>
        public const long DefaultMaxQueuedBytes = 1L << 30;

        /// <summary>Default minimum persistence workers.</summary>
        public const int DefaultMinWorkers = 1;

        /// <summary>Default maximum persistence workers.</summary>
        public const int DefaultMaxWorkers = 8;

        /// <summary>Default simultaneous writes allowed on one spool.</summary>
        public const int DefaultMaxInFlightPerSpool = 2;

        /// <summary>Default queue depth that can start a scale-up.</summary>
        public const int DefaultScaleUpQueueDepth = 32;

        /// <summary>Default queue depth that can start a scale-down.</summary>
        public const int DefaultScaleDownQueueDepth = 2;

        /// <summary>Default time a scale condition must hold before the worker count changes.</summary>
        public const int DefaultScaleDwellMilliseconds = 2000;

        /// <summary>Default control-loop period.</summary>
        public const int DefaultControlIntervalMilliseconds = 250;

        /// <summary>
        /// Default oldest-item age that counts as backlog even when depth is below
        /// <see cref="DefaultScaleUpQueueDepth"/>. The age is the enqueue time of the
        /// item that made the queue non-empty, so it can overstate age until the queue drains.
        /// </summary>
        public const int DefaultScaleUpAgeMilliseconds = 500;

        /// <summary>Default delay before retrying an article when no spool can take it.</summary>
        public const int DefaultIneligibleRetryMilliseconds = 500;

        /// <summary>
        /// Gets or sets the maximum article bytes held by the queue.
        /// An article larger than this budget is rejected. Default is <see cref="DefaultMaxQueuedBytes"/>.
        /// </summary>
        public long MaxQueuedBytes { get; set; } = DefaultMaxQueuedBytes;

        /// <summary>Gets or sets the worker count used at start and the floor for scale-down.</summary>
        public int MinWorkers { get; set; } = DefaultMinWorkers;

        /// <summary>Gets or sets the global worker ceiling. Each worker holds at most one article.</summary>
        public int MaxWorkers { get; set; } = DefaultMaxWorkers;

        /// <summary>
        /// Gets or sets the maximum in-flight publishes on one spool.
        /// A spool at this limit is not selected until a publish finishes.
        /// </summary>
        public int MaxInFlightPerSpool { get; set; } = DefaultMaxInFlightPerSpool;

        /// <summary>Gets or sets the queued-article count that arms scale-up.</summary>
        public int ScaleUpQueueDepth { get; set; } = DefaultScaleUpQueueDepth;

        /// <summary>Gets or sets the queued-article count that arms scale-down when storage is not saturated.</summary>
        public int ScaleDownQueueDepth { get; set; } = DefaultScaleDownQueueDepth;

        /// <summary>Gets or sets how long a scale condition must remain true before the count changes by one.</summary>
        public int ScaleDwellMilliseconds { get; set; } = DefaultScaleDwellMilliseconds;

        /// <summary>Gets or sets the control-loop delay. Production evaluates on this period.</summary>
        public int ControlIntervalMilliseconds { get; set; } = DefaultControlIntervalMilliseconds;

        /// <summary>Gets or sets the oldest-item age, in milliseconds, that arms scale-up together with a non-empty queue.</summary>
        public int ScaleUpAgeMilliseconds { get; set; } = DefaultScaleUpAgeMilliseconds;

        /// <summary>Gets or sets the wait before a worker retries an article that found no eligible spool.</summary>
        public int IneligibleRetryMilliseconds { get; set; } = DefaultIneligibleRetryMilliseconds;
    }
}
