using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.ReaderService.Session.Framing;

namespace VectorNNTPD.ReaderService.Session.CommandProcessor
{
    /// <summary>
    /// Writes NNTP single-line and multi-line responses to a <see cref="PipeWriter"/> through a
    /// single ordered outbound pump.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All writes are serialized onto one pump so response bytes remain ordered. Ordinary
    /// <see cref="WriteLineAsync(int, string, CancellationToken)"/> awaits pump flush completion
    /// (existing command semantics).
    /// <see cref="EnqueueLineAsync(int, string, CancellationToken)"/> only waits until the line is
    /// accepted by the ordered queue,
    /// so TAKETHIS can continue reading the next pipelined article without waiting for network
    /// delivery of the prior 239/439.
    /// </para>
    /// <para>
    /// The pump coalesces fire-and-forget lines into batches of at most
    /// <see cref="CoalesceResponseBatchSize"/> before one <see cref="PipeWriter.FlushAsync"/>.
    /// Logical response bytes, CRLF framing, and channel order are unchanged; only write
    /// granularity changes. Awaiting writes (status lines, article chunks, STARTTLS/COMPRESS/QUIT)
    /// flush any held batch first.
    /// </para>
    /// <para>
    /// Large article bodies use the WriteArticleAsync APIs: externally supplied destuffed bytes →
    /// restuff → bounded owned chunks → the same ordered Channel (no second TX pipeline;
    /// mode-independent). The writer does not own article storage or lookup.
    /// </para>
    /// </remarks>
    public sealed class NntpResponseWriter : IAsyncDisposable
    {
        /// <summary>
        /// Maximum fire-and-forget responses held into one <see cref="PipeWriter.FlushAsync"/>.
        /// </summary>
        /// <remarks>
        /// Chosen from the measured N=8 reverse-path cell in
        /// <c>.artifacts/STREAM-TCP-WIRE-MECHANISM-AUDIT.md</c> (~200 × 264-byte TCP segments/s
        /// versus N=1 ~1450 × 33-byte segments/s). Not a configuration setting.
        /// </remarks>
        internal const int CoalesceResponseBatchSize = 8;

        private static readonly byte[] DotCrlf = ".\r\n"u8.ToArray();

        private readonly PipeWriter _output;
        private readonly Channel<WriteRequest> _channel;
        private readonly Task _pump;
        // codeql[cs/missed-using-statement] Owned by NntpResponseWriter until Dispose; a using at this declaration would cancel the pump when the constructor returns.
        private readonly CancellationTokenSource _pumpCts = new();
        private int _disposed;
        private long _channelEnqueueCount;
        private long _pipeFlushCount;
        private long _ownedPayloadBytesEnqueued;
        private int _coalescedUnflushed;
        private int _directExclusive;
        private long _directPipeFlushCount;
        private IAccountByteSink _byteSink = NullAccountByteSink.Instance;

        private readonly struct WriteRequest
        {
            public WriteRequest(
                ReadOnlyMemory<byte> payload,
                TaskCompletionSource? completed,
                bool flushAfter = false)
            {
                Payload = payload;
                Completed = completed;
                FlushAfter = flushAfter;
            }

            /// <summary>
            /// Payload bytes valid until the pump finishes writing this item. Immortal static
            /// responses may share the same backing array across requests; the pump only reads.
            /// </summary>
            public ReadOnlyMemory<byte> Payload { get; }

            public TaskCompletionSource? Completed { get; }

            /// <summary>
            /// When <see langword="true"/>, the pump flushes after this fire-and-forget line
            /// instead of holding it in a TAKETHIS coalesce batch.
            /// </summary>
            public bool FlushAfter { get; }
        }

        /// <summary>Initializes a new instance of the <see cref="NntpResponseWriter"/> class.</summary>
        public NntpResponseWriter(PipeWriter output)
            : this(output, channelCapacity: 4096)
        {
        }

        /// <summary>Test/harness constructor with an explicit bounded Channel capacity.</summary>
        internal NntpResponseWriter(PipeWriter output, int channelCapacity)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentOutOfRangeException.ThrowIfLessThan(channelCapacity, 1);
            _output = output;
            var channelOptions = new BoundedChannelOptions(channelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = true,
            };
            ChannelAllowsSynchronousContinuations = channelOptions.AllowSynchronousContinuations;
            _channel = Channel.CreateBounded<WriteRequest>(channelOptions);
            _pump = Task.Run(() => PumpAsync(_pumpCts.Token), CancellationToken.None);
        }

        /// <summary>
        /// Gets the TX response Channel AllowSynchronousContinuations value applied at construction.
        /// </summary>
        internal bool ChannelAllowsSynchronousContinuations { get; }

        /// <summary>
        /// Installs the account byte sink. Counting starts after the next successful
        /// <c>PipeWriter.Advance</c>, never at Channel enqueue.
        /// </summary>
        public void SetByteSink(IAccountByteSink sink)
        {
            ArgumentNullException.ThrowIfNull(sink);
            Volatile.Write(ref _byteSink, sink);
        }

        /// <summary>Gets the number of Channel items accepted (diagnostics / tests).</summary>
        internal long ChannelEnqueueCount => Volatile.Read(ref _channelEnqueueCount);

        /// <summary>Gets the existing TX <see cref="PipeWriter"/> this writer pumps into. Not a second pipe.</summary>
        internal PipeWriter UnderlyingOutput => _output;

        /// <summary>Gets whether SPEEDTEST currently holds exclusive TX PipeWriter ownership.</summary>
        internal bool DirectPipeExclusive => Volatile.Read(ref _directExclusive) == 1;

        /// <summary>Gets <see cref="PipeWriter.FlushAsync"/> calls made on the SPEEDTEST direct-pipe lease.</summary>
        internal long DirectPipeFlushCount => Volatile.Read(ref _directPipeFlushCount);

        /// <summary>Gets the number of <see cref="PipeWriter.FlushAsync"/> calls from the TX pump.</summary>
        internal long PipeFlushCount => Volatile.Read(ref _pipeFlushCount);

        /// <summary>Gets total owned payload bytes accepted into the Channel.</summary>
        internal long OwnedPayloadBytesEnqueued => Volatile.Read(ref _ownedPayloadBytesEnqueued);

        /// <summary>
        /// Gets whether any fire-and-forget lines have been accepted and not yet flushed.
        /// </summary>
        internal bool HasCoalescedUnflushed => Volatile.Read(ref _coalescedUnflushed) > 0;

        /// <summary>
        /// Writes a single-line response (<c>code text</c>), waiting until the ordered pump has
        /// flushed it into the outbound pipe (subject to pipe backpressure).
        /// </summary>
        public ValueTask WriteLineAsync(int code, string text, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(code);
            ArgumentNullException.ThrowIfNull(text);
            return WriteAndAwaitFlushAsync(EncodeLine(code, text), cancellationToken);
        }

        /// <summary>
        /// Writes a complete pre-encoded wire response (including CRLF) and awaits pump flush.
        /// </summary>
        /// <remarks>
        /// Does not copy <paramref name="wireLine"/>. The memory must remain unchanged until this
        /// write has been flushed into the outbound pipe. Immortal static responses satisfy that
        /// for the process lifetime. Dynamically composed responses must be an owned buffer
        /// (not Pipe memory, not session scratch). Channel order, coalesce-flush-first, and TCS
        /// barrier semantics match <see cref="WriteLineAsync(int, string, CancellationToken)"/>.
        /// </remarks>
        public ValueTask WriteLineAsync(ReadOnlyMemory<byte> wireLine, CancellationToken cancellationToken = default)
        {
            return WriteAndAwaitFlushAsync(wireLine, cancellationToken);
        }

        /// <summary>
        /// Enqueues a single-line response without waiting for outbound pipe/network flush.
        /// </summary>
        /// <remarks>
        /// Ordering is preserved. Returns when the ordered queue accepts the line (may wait if the
        /// response queue is saturated). Used by TAKETHIS so receive can proceed immediately.
        /// </remarks>
        public ValueTask EnqueueLineAsync(int code, string text, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(code);
            ArgumentNullException.ThrowIfNull(text);
            return EnqueueAsync(new WriteRequest(EncodeLine(code, text), completed: null), cancellationToken);
        }

        /// <summary>
        /// Enqueues a complete pre-encoded wire response without waiting for outbound flush.
        /// </summary>
        /// <remarks>
        /// Does not copy <paramref name="wireLine"/>. The memory must remain unchanged until the
        /// coalesced batch that contains this item is flushed. Dynamically composed responses
        /// must be an owned buffer (not Pipe memory, not session scratch). Coalesce/order
        /// semantics match <see cref="EnqueueLineAsync(int, string, CancellationToken)"/>.
        /// </remarks>
        public ValueTask EnqueueLineAsync(ReadOnlyMemory<byte> wireLine, CancellationToken cancellationToken = default) =>
            EnqueueAsync(new WriteRequest(wireLine, completed: null), cancellationToken);

        /// <summary>
        /// Enqueues a pre-encoded wire line and flushes it without waiting for a coalesce batch.
        /// Returns when the Channel accepts the line (may wait if the writer is saturated).
        /// </summary>
        /// <remarks>
        /// Used by TAKETHIS so 239/439 are not held for <see cref="CoalesceResponseBatchSize"/>
        /// siblings. Channel order and pump serialization are unchanged. CHECK still uses
        /// <see cref="EnqueueLineAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.
        /// </remarks>
        public ValueTask EnqueueLineImmediateAsync(
            ReadOnlyMemory<byte> wireLine,
            CancellationToken cancellationToken = default) =>
            EnqueueAsync(new WriteRequest(wireLine, completed: null, flushAfter: true), cancellationToken);

        /// <summary>
        /// Flushes any held fire-and-forget lines without writing additional response bytes.
        /// </summary>
        /// <remarks>
        /// No-op when the coalesce buffer is empty so MODE READER / WriteLineAsync paths are not
        /// delayed. Used when the session is about to wait for more inbound octets, and by tests.
        /// </remarks>
        internal ValueTask FlushCoalescedAsync(CancellationToken cancellationToken = default)
        {
            if (!HasCoalescedUnflushed)
            {
                return ValueTask.CompletedTask;
            }

            return WriteAndAwaitFlushAsync(ReadOnlyMemory<byte>.Empty, cancellationToken);
        }

        /// <summary>Begins a multi-line response and writes the initial status line.</summary>
        public async ValueTask WriteMultilineStartAsync(
            int code,
            string text,
            CancellationToken cancellationToken = default)
        {
            await WriteLineAsync(code, text, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Writes one multi-line body line (applies dot-stuffing) without terminating the block.</summary>
        public ValueTask WriteMultilineDataAsync(string line, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(line);
            if (line.StartsWith('.'))
            {
                line = "." + line;
            }

            return WriteAndAwaitFlushAsync(EncodeAscii(line + "\r\n"), cancellationToken);
        }

        /// <summary>Terminates a multi-line response with <c>.CRLF</c> and flushes.</summary>
        public ValueTask WriteMultilineEndAsync(CancellationToken cancellationToken = default) =>
            WriteAndAwaitFlushAsync(DotCrlf, cancellationToken);

        /// <summary>
        /// Writes a precomputed byte payload to the session output pipe and flushes (honouring pipe backpressure).
        /// </summary>
        /// <remarks>
        /// Does not copy <paramref name="payload"/>. The memory must remain unchanged until this
        /// write has been flushed. Intended for BENCHIT reuse of an immutable wire buffer. Still
        /// uses the production ordered writer path; does not bypass transport pumps.
        /// </remarks>
        public ValueTask WriteBytesAndFlushAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default) =>
            WriteAndAwaitFlushAsync(payload, cancellationToken);

        /// <summary>
        /// Writes one framed article through the shared bounded article TX path (default 64 KiB chunks).
        /// </summary>
        /// <param name="storedDestuffedArticle">
        /// Destuffed article payload bytes supplied by the caller (no terminating <c>.\r\n</c>);
        /// see <see cref="ArticleWireReconstructor"/>. Not an NNTPD storage API.
        /// </param>
        /// <param name="framing">Mode-independent wire framing (e.g. 220 ARTICLE, 222 BODY, TAKETHIS).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <remarks>
        /// Materialized restuff → owned chunks → existing ordered Channel. Does not inspect session mode.
        /// Awaits each chunk's flush into the outbound Pipe (not peer socket delivery).
        /// </remarks>
        public ValueTask WriteArticleAsync(
            ReadOnlyMemory<byte> storedDestuffedArticle,
            NntpArticleTxFraming framing,
            CancellationToken cancellationToken = default) =>
            WriteArticleAsync(
                storedDestuffedArticle,
                framing,
                NntpArticleTxChunkBudget.DefaultBytes,
                cancellationToken);

        /// <summary>
        /// Writes one framed article through the shared bounded article TX path with an explicit chunk budget.
        /// </summary>
        /// <param name="storedDestuffedArticle">Caller-supplied destuffed article payload bytes.</param>
        /// <param name="framing">Mode-independent wire framing.</param>
        /// <param name="chunkBytes">Production chunk budget (64–256 KiB).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public ValueTask WriteArticleAsync(
            ReadOnlyMemory<byte> storedDestuffedArticle,
            NntpArticleTxFraming framing,
            int chunkBytes,
            CancellationToken cancellationToken = default)
        {
            NntpArticleTxChunkBudget.ThrowIfNotProductionRange(chunkBytes);
            return WriteArticleCoreAsync(storedDestuffedArticle, framing, chunkBytes, cancellationToken);
        }

        /// <summary>
        /// Test-only article TX allowing non-production chunk sizes for boundary coverage.
        /// </summary>
        internal ValueTask WriteArticleForTestsAsync(
            ReadOnlyMemory<byte> storedDestuffedArticle,
            NntpArticleTxFraming framing,
            int chunkBytes,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 1);
            return WriteArticleCoreAsync(storedDestuffedArticle, framing, chunkBytes, cancellationToken);
        }

        /// <summary>
        /// Convenience: ARTICLE-style (220) response from caller-supplied full destuffed article bytes.
        /// </summary>
        /// <param name="storedDestuffedArticle">Full destuffed article (headers + body); external input.</param>
        /// <param name="messageId">Message-id for the 220 status line.</param>
        /// <param name="articleNumber">Article number for the status line, or 0 when mid-selected.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <remarks>
        /// Name reflects RFC 3977 ARTICLE wire shape, not an NNTPD customer repository.
        /// </remarks>
        public ValueTask WriteCustomerArticleAsync(
            ReadOnlyMemory<byte> storedDestuffedArticle,
            string messageId,
            long articleNumber = 0,
            CancellationToken cancellationToken = default) =>
            WriteArticleAsync(
                storedDestuffedArticle,
                NntpArticleTxFraming.CustomerArticle(messageId, articleNumber),
                cancellationToken);

        /// <summary>
        /// Convenience: BODY-style (222) response; strips headers at the first <c>\r\n\r\n</c>.
        /// </summary>
        /// <param name="storedDestuffedArticle">
        /// Full destuffed article; headers stripped at the first blank line.
        /// When no blank line exists, an empty body is transmitted (still with 222 + terminator).
        /// </param>
        /// <param name="messageId">Message-id for the 222 status line.</param>
        /// <param name="articleNumber">Article number for the status line, or 0 when mid-selected.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <remarks>
        /// Same Channel → pump → PipeWriter path as ARTICLE. Does not create a BODY-specific TX stack.
        /// Name reflects RFC 3977 BODY wire shape, not storage ownership.
        /// </remarks>
        public ValueTask WriteCustomerBodyAsync(
            ReadOnlyMemory<byte> storedDestuffedArticle,
            string messageId,
            long articleNumber = 0,
            CancellationToken cancellationToken = default)
        {
            _ = ArticleWireReconstructor.TrySplitHeadersAndBody(
                storedDestuffedArticle.Span,
                out _,
                out var bodySpan);
            // Copy body span to owned memory before await (span cannot cross await).
            var body = bodySpan.IsEmpty ? ReadOnlyMemory<byte>.Empty : bodySpan.ToArray();
            return WriteArticleAsync(
                body,
                NntpArticleTxFraming.CustomerBody(messageId, articleNumber),
                cancellationToken);
        }

        /// <summary>
        /// Convenience: BODY-style (222) response from an already-separated destuffed body payload.
        /// </summary>
        /// <param name="storedDestuffedBody">Destuffed body bytes only (no headers; no terminator).</param>
        /// <param name="messageId">Message-id for the 222 status line.</param>
        /// <param name="articleNumber">Article number for the status line, or 0 when mid-selected.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public ValueTask WriteCustomerBodyFromBodyAsync(
            ReadOnlyMemory<byte> storedDestuffedBody,
            string messageId,
            long articleNumber = 0,
            CancellationToken cancellationToken = default) =>
            WriteArticleAsync(
                storedDestuffedBody,
                NntpArticleTxFraming.CustomerBody(messageId, articleNumber),
                cancellationToken);

        /// <summary>
        /// Convenience: HEAD-style (221) response from destuffed header bytes (including blank-line separator when present).
        /// </summary>
        /// <param name="storedDestuffedHeaders">Header block only; no body; no terminator.</param>
        /// <param name="messageId">Message-id for the 221 status line.</param>
        /// <param name="articleNumber">Article number for the status line, or 0 when mid-selected.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public ValueTask WriteCustomerHeadAsync(
            ReadOnlyMemory<byte> storedDestuffedHeaders,
            string messageId,
            long articleNumber = 0,
            CancellationToken cancellationToken = default) =>
            WriteArticleAsync(
                storedDestuffedHeaders,
                NntpArticleTxFraming.CustomerHead(messageId, articleNumber),
                cancellationToken);

        /// <summary>
        /// Convenience: STAT-style (223) single-line response (no multiline body).
        /// </summary>
        /// <param name="messageIdBytes">Message-id octets including angle brackets.</param>
        /// <param name="articleNumber">Article number, or 0 when mid-selected.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public ValueTask WriteCustomerStatAsync(
            ReadOnlySpan<byte> messageIdBytes,
            long articleNumber = 0,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(articleNumber);
            var number = articleNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var prefix = System.Text.Encoding.ASCII.GetBytes("223 " + number + " ");
            var owned = NntpResponseCompose.Concat(prefix, messageIdBytes, NntpResponses.Crlf.Span);
            return WriteLineAsync(owned, cancellationToken);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _channel.Writer.TryComplete();
            await _pumpCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
                // Expected on dispose.
            }
            finally
            {
                _pumpCts.Dispose();
            }
        }

        private async ValueTask WriteArticleCoreAsync(
            ReadOnlyMemory<byte> storedDestuffedArticle,
            NntpArticleTxFraming framing,
            int chunkBytes,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

            var chunks = PrepareArticleChunks(storedDestuffedArticle, framing, chunkBytes);
            foreach (var chunk in chunks)
            {
                // Ownership of `chunk` transfers to the Channel; do not mutate after enqueue.
                await WriteAndAwaitFlushAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Builds owned framed wire chunks (header + restuff) without touching the TX Channel.
        /// </summary>
        /// <remarks>
        /// Used by <see cref="NntpStreamArticleTxScheduler"/> so preparation can run outside the
        /// article-atomic enqueue gate. Caller owns the returned arrays until each is enqueued.
        /// </remarks>
        internal static List<byte[]> PrepareArticleChunks(
            ReadOnlyMemory<byte> storedDestuffedArticle,
            NntpArticleTxFraming framing,
            int chunkBytes)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 1);
            var header = framing.BuildHeaderBytes();
            var body = ArticleWireReconstructor.RestuffArticle(storedDestuffedArticle.Span);
            var totalLength = header.Length + body.Length;
            if (totalLength == 0)
            {
                return [];
            }

            var chunks = new List<byte[]>((totalLength + chunkBytes - 1) / chunkBytes);
            var offset = 0;
            while (offset < totalLength)
            {
                var take = Math.Min(chunkBytes, totalLength - offset);
                var chunk = new byte[take];
                CopyFramedWire(header, body, offset, chunk.AsSpan());
                chunks.Add(chunk);
                offset += take;
            }

            return chunks;
        }

        /// <summary>
        /// Enqueues an owned payload into the ordered TX Channel and returns a task that completes
        /// when the pump has flushed that item into the outbound Pipe (not peer delivery).
        /// </summary>
        /// <remarks>
        /// Does not await flush before returning. Used with a short article-atomic enqueue gate so all
        /// chunks of one article can be admitted contiguously before awaiting backpressure.
        /// </remarks>
        internal async ValueTask<Task> EnqueueOwnedPayloadAsync(
            byte[] payload,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(payload);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await EnqueueAsync(new WriteRequest(payload, completed), cancellationToken).ConfigureAwait(false);
            return WaitForFlushAsync(completed, cancellationToken);
        }

        private static async Task WaitForFlushAsync(TaskCompletionSource completed, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                completed);
            await completed.Task.ConfigureAwait(false);
        }

        private static void CopyFramedWire(byte[] header, byte[] body, int absoluteOffset, Span<byte> destination)
        {
            var written = 0;
            while (written < destination.Length)
            {
                if (absoluteOffset < header.Length)
                {
                    var fromHeader = Math.Min(header.Length - absoluteOffset, destination.Length - written);
                    header.AsSpan(absoluteOffset, fromHeader).CopyTo(destination[written..]);
                    absoluteOffset += fromHeader;
                    written += fromHeader;
                    continue;
                }

                var bodyOffset = absoluteOffset - header.Length;
                var fromBody = Math.Min(body.Length - bodyOffset, destination.Length - written);
                body.AsSpan(bodyOffset, fromBody).CopyTo(destination[written..]);
                absoluteOffset += fromBody;
                written += fromBody;
            }
        }

        private async ValueTask WriteAndAwaitFlushAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await EnqueueAsync(new WriteRequest(payload, completed), cancellationToken).ConfigureAwait(false);
            using var registration = cancellationToken.Register(static state =>
            {
                ((TaskCompletionSource)state!).TrySetCanceled();
            }, completed);
            await completed.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// DIAGNOSTIC-ONLY. Parks Channel/pump production and returns a lease that writes the
        /// existing TX <see cref="PipeWriter"/>. SPEEDTEST is the only caller.
        /// </summary>
        internal async ValueTask<DirectTxPipeLease> AcquireDirectTxPipeAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            await FlushCoalescedAsync(cancellationToken).ConfigureAwait(false);
            if (Interlocked.CompareExchange(ref _directExclusive, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "TX PipeWriter is already exclusively leased; ResponseWriter and SPEEDTEST cannot produce concurrently.");
            }

            return new DirectTxPipeLease(this);
        }

        internal void ReleaseDirectTxPipe() => Volatile.Write(ref _directExclusive, 0);

        internal async ValueTask WriteDirectToOutputAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (Volatile.Read(ref _directExclusive) != 1)
            {
                throw new InvalidOperationException("Direct TX PipeWriter write requires an exclusive SPEEDTEST lease.");
            }

            var remaining = payload;
            while (!remaining.IsEmpty)
            {
                var memory = _output.GetMemory(Math.Min(remaining.Length, 64 * 1024));
                var toCopy = Math.Min(memory.Length, remaining.Length);
                remaining.Span[..toCopy].CopyTo(memory.Span);
                _output.Advance(toCopy);
                Volatile.Read(ref _byteSink).ObserveCopied(toCopy);
                remaining = remaining[toCopy..];

                if (_output.UnflushedBytes >= 64 * 1024)
                {
                    await FlushOutputAsync(cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _directPipeFlushCount);
                }
            }

            if (_output.UnflushedBytes > 0)
            {
                await FlushOutputAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _directPipeFlushCount);
            }
        }

        private async ValueTask EnqueueAsync(WriteRequest request, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (Volatile.Read(ref _directExclusive) == 1)
            {
                throw new InvalidOperationException(
                    "TX PipeWriter is exclusively leased to SPEEDTEST; ResponseWriter Channel cannot write concurrently.");
            }

            try
            {
                await _channel.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _channelEnqueueCount);
                Interlocked.Add(ref _ownedPayloadBytesEnqueued, request.Payload.Length);
                if (request.Completed is null)
                {
                    Interlocked.Increment(ref _coalescedUnflushed);
                }
            }
            catch (ChannelClosedException ex)
            {
                throw new ObjectDisposedException(nameof(NntpResponseWriter), ex);
            }
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            var batch = new WriteRequest[CoalesceResponseBatchSize + 1];
            var count = 0;
            var coalesceCount = 0;

            void CancelHeld()
            {
                var coalesced = 0;
                for (var i = 0; i < count; i++)
                {
                    if (batch[i].Completed is null)
                    {
                        coalesced++;
                    }

                    batch[i].Completed?.TrySetCanceled();
                }

                if (coalesced > 0)
                {
                    Interlocked.Add(ref _coalescedUnflushed, -coalesced);
                }

                count = 0;
                coalesceCount = 0;
            }

            try
            {
                await foreach (var request in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        if (request.Completed is not null)
                        {
                            batch[count++] = request;
                            await FlushHeldAsync(batch, count, cancellationToken).ConfigureAwait(false);
                            count = 0;
                            coalesceCount = 0;
                            continue;
                        }

                        batch[count++] = request;
                        coalesceCount++;

                        if (request.FlushAfter)
                        {
                            await FlushHeldAsync(batch, count, cancellationToken).ConfigureAwait(false);
                            count = 0;
                            coalesceCount = 0;
                            continue;
                        }

                        while (coalesceCount < CoalesceResponseBatchSize &&
                               _channel.Reader.TryRead(out var queued))
                        {
                            if (queued.Completed is not null || queued.FlushAfter)
                            {
                                batch[count++] = queued;
                                await FlushHeldAsync(batch, count, cancellationToken).ConfigureAwait(false);
                                count = 0;
                                coalesceCount = 0;
                                break;
                            }

                            batch[count++] = queued;
                            coalesceCount++;
                        }

                        if (coalesceCount >= CoalesceResponseBatchSize)
                        {
                            await FlushHeldAsync(batch, count, cancellationToken).ConfigureAwait(false);
                            count = 0;
                            coalesceCount = 0;
                        }
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception ex)
                    {
                        for (var i = 0; i < count; i++)
                        {
                            batch[i].Completed?.TrySetException(ex);
                        }

                        count = 0;
                        coalesceCount = 0;
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }

                if (count > 0 && !cancellationToken.IsCancellationRequested)
                {
                    await FlushHeldAsync(batch, count, cancellationToken).ConfigureAwait(false);
                    count = 0;
                    coalesceCount = 0;
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
                // Dispose path.
            }
            finally
            {
                CancelHeld();
                while (_channel.Reader.TryRead(out var leftover))
                {
                    if (leftover.Completed is null)
                    {
                        Interlocked.Decrement(ref _coalescedUnflushed);
                    }

                    leftover.Completed?.TrySetCanceled();
                }
            }
        }

        private async ValueTask FlushHeldAsync(
            WriteRequest[] batch,
            int count,
            CancellationToken cancellationToken)
        {
            var coalesced = 0;
            for (var i = 0; i < count; i++)
            {
                if (batch[i].Completed is null)
                {
                    coalesced++;
                }
            }

            try
            {
                for (var i = 0; i < count; i++)
                {
                    await CopyPayloadToPipeAsync(batch[i].Payload, cancellationToken).ConfigureAwait(false);
                }

                if (_output.UnflushedBytes > 0)
                {
                    await FlushOutputAsync(cancellationToken).ConfigureAwait(false);
                }

                for (var i = 0; i < count; i++)
                {
                    batch[i].Completed?.TrySetResult();
                }
            }
            finally
            {
                if (coalesced > 0)
                {
                    Interlocked.Add(ref _coalescedUnflushed, -coalesced);
                }
            }
        }

        private async ValueTask CopyPayloadToPipeAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            var remaining = payload;
            while (!remaining.IsEmpty)
            {
                var memory = _output.GetMemory(Math.Min(remaining.Length, 64 * 1024));
                var toCopy = Math.Min(memory.Length, remaining.Length);
                remaining.Span[..toCopy].CopyTo(memory.Span);
                _output.Advance(toCopy);
                Volatile.Read(ref _byteSink).ObserveCopied(toCopy);
                remaining = remaining[toCopy..];

                if (_output.UnflushedBytes >= 64 * 1024)
                {
                    await FlushOutputAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async ValueTask FlushOutputAsync(CancellationToken cancellationToken)
        {
            var flush = _output.FlushAsync(cancellationToken);
            var result = flush.IsCompletedSuccessfully
                ? flush.Result
                : await flush.ConfigureAwait(false);

            Interlocked.Increment(ref _pipeFlushCount);

            if (result.IsCanceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (result.IsCompleted)
            {
                throw new InvalidOperationException("NNTP output pipe completed while writing.");
            }
        }

        private static byte[] EncodeLine(int code, string text) =>
            EncodeAscii($"{code} {text}\r\n");

        private static byte[] EncodeAscii(string text)
        {
            var byteCount = Encoding.ASCII.GetByteCount(text);
            var bytes = new byte[byteCount];
            Encoding.ASCII.GetBytes(text, bytes);
            return bytes;
        }
    }
}
