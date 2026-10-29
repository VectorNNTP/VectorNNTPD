namespace VectorNNTPD.ReaderService.Session.CommandProcessor
{
    /// <summary>
    /// Bounded STREAM outstanding article TX scheduler above the shared
    /// <see cref="NntpResponseWriter"/> TX path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Limits how many STREAM article operations may be admitted concurrently (default 8; valid 4–16).
    /// Does not replace TX Channel capacity, Pipe thresholds, or transport backpressure.
    /// </para>
    /// <para>
    /// Producer model (Phase 3B; evidence:
    /// <c>docs/STREAM-TX-PRODUCER-CONCURRENCY-EXPERIMENT.md</c>):
    /// </para>
    /// <list type="number">
    /// <item>Acquire STREAM depth slot.</item>
    /// <item>Prepare framing/restuff/owned chunks <strong>outside</strong> the enqueue gate.</item>
    /// <item>Acquire short FIFO enqueue gate; admit <strong>all</strong> chunks of this article
    /// contiguously into the existing Channel; release gate.</item>
    /// <item>Await existing per-chunk Pipe flush completion (backpressure) outside the gate.</item>
    /// <item>Release depth slot in <c>finally</c>.</item>
    /// </list>
    /// <para>
    /// Callers must start operations in protocol order. The enqueue gate is FIFO among waiters and
    /// prevents chunk interleaving (<c>A1 B1 A2…</c>). There is no second TX Channel or pump.
    /// </para>
    /// <para>
    /// This type does not load or look up articles. Callers supply destuffed bytes (bench, peer feed,
    /// or other external source). Article storage/catalogue is out of scope for NNTPD.
    /// </para>
    /// </remarks>
    public sealed class NntpStreamArticleTxScheduler : IAsyncDisposable
    {
        /// <summary>Default STREAM outstanding article depth.</summary>
        public const int DefaultDepth = 8;

        /// <summary>Minimum allowed STREAM outstanding article depth.</summary>
        public const int MinDepth = 4;

        /// <summary>Maximum allowed STREAM outstanding article depth.</summary>
        public const int MaxDepth = 16;

        private readonly SemaphoreSlim _depth;
        private readonly SemaphoreSlim _enqueueGate = new(1, 1);
        private int _outstanding;
        private int _preparing;
        private int _enqueuing;
        private int _peakPreparing;
        private int _disposed;

        /// <summary>Initializes a scheduler with <see cref="DefaultDepth"/>.</summary>
        public NntpStreamArticleTxScheduler()
            : this(DefaultDepth)
        {
        }

        /// <summary>Initializes a scheduler with an explicit depth.</summary>
        /// <param name="depth">Outstanding article depth; must be in <see cref="MinDepth"/>–<see cref="MaxDepth"/>.</param>
        public NntpStreamArticleTxScheduler(int depth)
        {
            ThrowIfNotValidDepth(depth);
            Depth = depth;
            _depth = new SemaphoreSlim(depth, depth);
        }

        /// <summary>Gets the configured outstanding depth.</summary>
        public int Depth { get; }

        /// <summary>Gets the number of currently admitted (not yet completed) operations.</summary>
        public int Outstanding => Volatile.Read(ref _outstanding);

        /// <summary>Gets the peak number of concurrent preparation phases observed (diagnostics / tests).</summary>
        internal int PeakPreparing => Volatile.Read(ref _peakPreparing);

        /// <summary>Gets the number of operations currently holding the enqueue gate (0 or 1).</summary>
        internal int Enqueuing => Volatile.Read(ref _enqueuing);

        /// <summary>Test hook: invoked after preparation, before the enqueue gate (deterministic cancel tests).</summary>
        internal Func<CancellationToken, ValueTask>? TestAfterPrepare { get; set; }

        /// <summary>Test hook: invoked while holding the enqueue gate, before chunk admission.</summary>
        internal Func<CancellationToken, ValueTask>? TestWhileHoldingEnqueueGate { get; set; }

        /// <summary>Test hook: invoked after releasing the enqueue gate, before awaiting Pipe flush.</summary>
        internal Func<CancellationToken, ValueTask>? TestBeforeAwaitFlush { get; set; }

        /// <summary>Returns <see langword="true"/> when <paramref name="depth"/> is in the valid range.</summary>
        public static bool IsValidDepth(int depth) => depth is >= MinDepth and <= MaxDepth;

        /// <summary>Throws if <paramref name="depth"/> is outside <see cref="MinDepth"/>–<see cref="MaxDepth"/>.</summary>
        public static void ThrowIfNotValidDepth(int depth)
        {
            if (!IsValidDepth(depth))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(depth),
                    depth,
                    $"Stream outstanding article depth must be between {MinDepth} and {MaxDepth}.");
            }
        }

        /// <summary>
        /// Acquires one outstanding slot, runs <paramref name="operation"/>, then releases the slot
        /// (including on cancellation or fault).
        /// </summary>
        public async ValueTask ExecuteAsync(
            Func<CancellationToken, ValueTask> operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

            await _depth.WaitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _outstanding);
            try
            {
                await operation(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _outstanding);
                _depth.Release();
            }
        }

        /// <summary>
        /// Writes one framed article: prepare outside enqueue gate, admit all chunks under the gate,
        /// await Pipe flush outside the gate.
        /// </summary>
        public ValueTask WriteArticleAsync(
            NntpResponseWriter writer,
            ReadOnlyMemory<byte> destuffedArticle,
            NntpArticleTxFraming framing,
            CancellationToken cancellationToken = default) =>
            WriteArticleAsync(
                writer,
                destuffedArticle,
                framing,
                NntpArticleTxChunkBudget.DefaultBytes,
                cancellationToken);

        /// <summary>
        /// Writes one framed article with an explicit production chunk budget.
        /// </summary>
        public ValueTask WriteArticleAsync(
            NntpResponseWriter writer,
            ReadOnlyMemory<byte> destuffedArticle,
            NntpArticleTxFraming framing,
            int chunkBytes,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(writer);
            NntpArticleTxChunkBudget.ThrowIfNotProductionRange(chunkBytes);
            return WriteArticleCoreAsync(writer, destuffedArticle, framing, chunkBytes, cancellationToken);
        }

        /// <summary>Test-only write allowing non-production chunk sizes.</summary>
        internal ValueTask WriteArticleForTestsAsync(
            NntpResponseWriter writer,
            ReadOnlyMemory<byte> destuffedArticle,
            NntpArticleTxFraming framing,
            int chunkBytes,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 1);
            return WriteArticleCoreAsync(writer, destuffedArticle, framing, chunkBytes, cancellationToken);
        }

        private ValueTask WriteArticleCoreAsync(
            NntpResponseWriter writer,
            ReadOnlyMemory<byte> destuffedArticle,
            NntpArticleTxFraming framing,
            int chunkBytes,
            CancellationToken cancellationToken)
        {
            return ExecuteAsync(
                async token =>
                {
                    // 1) PREPARATION — outside enqueue gate (bounded by depth).
                    var preparing = Interlocked.Increment(ref _preparing);
                    UpdatePeakPreparing(preparing);
                    List<byte[]> chunks;
                    try
                    {
                        chunks = NntpResponseWriter.PrepareArticleChunks(destuffedArticle, framing, chunkBytes);
                        if (TestAfterPrepare is not null)
                        {
                            await TestAfterPrepare(token).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _preparing);
                    }

                    if (chunks.Count == 0)
                    {
                        return;
                    }

                    // 2) ARTICLE-ATOMIC ENQUEUE — all chunks contiguous; gate may wait on Channel.
                    var flushTasks = new Task[chunks.Count];
                    await _enqueueGate.WaitAsync(token).ConfigureAwait(false);
                    Interlocked.Increment(ref _enqueuing);
                    try
                    {
                        if (TestWhileHoldingEnqueueGate is not null)
                        {
                            await TestWhileHoldingEnqueueGate(token).ConfigureAwait(false);
                        }

                        for (var i = 0; i < chunks.Count; i++)
                        {
                            flushTasks[i] = await writer
                                .EnqueueOwnedPayloadAsync(chunks[i], token)
                                .ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _enqueuing);
                        _enqueueGate.Release();
                    }

                    // 3) PIPE / TX BACKPRESSURE — outside enqueue gate.
                    if (TestBeforeAwaitFlush is not null)
                    {
                        await TestBeforeAwaitFlush(token).ConfigureAwait(false);
                    }

                    await Task.WhenAll(flushTasks).ConfigureAwait(false);
                },
                cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return ValueTask.CompletedTask;
            }

            _depth.Dispose();
            _enqueueGate.Dispose();
            return ValueTask.CompletedTask;
        }

        private void UpdatePeakPreparing(int current)
        {
            int snap;
            while (current > (snap = Volatile.Read(ref _peakPreparing)))
            {
                if (Interlocked.CompareExchange(ref _peakPreparing, current, snap) == snap)
                {
                    break;
                }
            }
        }
    }
}
