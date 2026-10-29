using VectorNNTPD.TransitService.History;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Diagnostics;

namespace VectorNNTPD.TransitService.Session
{
    /// <summary>
    /// Per-session bounded CHECK execution window: overlap Redis lookups, emit responses in send order.
    /// </summary>
    /// <remarks>
    /// Only CHECK uses this window. Non-CHECK commands drain it first and then run on the serial
    /// dispatcher. A slot is occupied from admission until
    /// <see cref="NntpResponseWriter.EnqueueLineAsync(ReadOnlyMemory{byte}, CancellationToken)"/>
    /// accepts the response (or the slot is cancelled on shutdown). Slots own a copy of the
    /// Message-ID so STREAM/READER parser scratch can be reused. Every CHECK response passes through
    /// the emit gate; there is no immediate path that bypasses it.
    /// </remarks>
    internal sealed class CheckPipeline
    {
        /// <summary>
        /// Outstanding CHECK executions per session. Architectural constant; not configurable.
        /// </summary>
        public const int Depth = 16;

        private readonly NntpSession _session;
        private readonly NntpResponseWriter _response;
        private readonly ILogger _logger;
        private readonly Slot?[] _slots;
        private readonly object _gate = new();
        // codeql[cs/missed-using-statement] Owned by CheckPipeline until ShutdownAsync; a using at this declaration would release it when the constructor returns.
        private readonly SemaphoreSlim _emitGate = new(1, 1);
        // codeql[cs/missed-using-statement] Owned by CheckPipeline until ShutdownAsync; a using at this declaration would cancel in-flight work when the constructor returns.
        private readonly CancellationTokenSource _lifetimeCts = new();
        private int _head;
        private int _count;
        private int _peakOccupied;
        private int _shutDown;
        private int _inFlightCompletions;
        private TaskCompletionSource _progress = NewProgress();

        /// <summary>Initializes a new instance of the <see cref="CheckPipeline"/> class.</summary>
        public CheckPipeline(NntpSession session, NntpResponseWriter response)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(response);
            _session = session;
            _response = response;
            _logger = NntpCommandLoggers.For(typeof(Check));
            _slots = new Slot?[Depth];
        }

        /// <summary>Gets the number of occupied slots (lookup, waiting for prefix, or awaiting TX accept).</summary>
        public int Occupied
        {
            get
            {
                lock (_gate)
                {
                    return _count;
                }
            }
        }

        /// <summary>Gets whether the window cannot accept another CHECK without waiting.</summary>
        public bool IsFull => Occupied >= Depth;

        /// <summary>Gets the peak occupied slot count observed on this session.</summary>
        public int PeakOccupied => Volatile.Read(ref _peakOccupied);

        /// <summary>Gets the number of <c>CompleteWhen</c> tasks still running (at most <see cref="Depth"/>).</summary>
        internal int InFlightCompletions => Volatile.Read(ref _inFlightCompletions);

        /// <summary>
        /// Test-only: awaited after response composition and before
        /// <see cref="NntpResponseWriter.EnqueueLineAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.
        /// The slot remains occupied. Used to model blocked TX without filling the writer Channel.
        /// </summary>
        internal Func<ValueTask>? BeforeEnqueueProbe { get; set; }

        /// <summary>
        /// Waits until a slot is free. Does not parse or drop commands; the caller must not read RX
        /// while this is pending so the input Pipe can apply backpressure.
        /// </summary>
        public async ValueTask WaitForCapacityAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    ThrowIfShutDown();
                    if (_count < Depth)
                    {
                        return;
                    }

                    wait = _progress.Task;
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Starts one authorized, syntactically valid CHECK. The Message-ID is copied into the slot
        /// so parser scratch can be reused immediately.
        /// </summary>
        public async ValueTask SubmitAsync(
            NntpCommand command,
            ReadOnlyMemory<byte> line,
            CancellationToken cancellationToken)
        {
            ThrowIfShutDown();
            var probe = _session.FeedProbe;
            probe?.RecordCheck();
            _session.RecordPeerCheck();
            _session.SetActivityState(FeedSessionState.WaitingHistory);
            var owned = command.ArgumentMemory(line).ToArray();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var lookup = Check.LookupAsync(_session, owned, cancellationToken);
            var emit = false;
            lock (_gate)
            {
                ThrowIfShutDown();
                if (_count >= Depth)
                {
                    throw new InvalidOperationException("CHECK pipeline accepted a command without a free slot.");
                }

                var slot = new Slot
                {
                    MessageId = owned,
                    StartedTimestamp = started,
                    Index = (_head + _count) % Depth,
                };
                _slots[slot.Index] = slot;
                _count++;
                _session.BeginCommandWork();
                if (_count > _peakOccupied)
                {
                    _peakOccupied = _count;
                }

                if (lookup.IsCompletedSuccessfully)
                {
                    slot.Completed = true;
                    slot.Result = lookup.Result;
                    probe?.RecordHistory(lookup.Result, System.Diagnostics.Stopwatch.GetTimestamp() - started);
                    emit = HeadIsReadyNoLock();
                }
                else
                {
                    _ = CompleteWhenAsync(slot, lookup, cancellationToken);
                }
            }

            if (emit)
            {
                await EmitReadySerializedAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Waits until every slot has been accepted by the TX writer (or cancelled). Required before
        /// any non-CHECK command.
        /// </summary>
        public async ValueTask DrainAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (_count == 0)
                    {
                        return;
                    }

                    wait = _progress.Task;
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Stops emission, cancels in-flight enqueue waits, and waits until every slot is released.
        /// Must run before the session disposes the writer.
        /// </summary>
        public async ValueTask ShutdownAsync()
        {
            if (Interlocked.Exchange(ref _shutDown, 1) == 1)
            {
                return;
            }

            try
            {
                await _lifetimeCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }

            lock (_gate)
            {
                for (var i = 0; i < Depth; i++)
                {
                    if (_slots[i] is { } slot)
                    {
                        slot.Cancelled = true;
                    }
                }

                ReleaseCancelledPrefixNoLock();
                SignalProgressNoLock();
            }

            try
            {
                while (true)
                {
                    Task wait;
                    lock (_gate)
                    {
                        if (_count == 0)
                        {
                            return;
                        }

                        wait = _progress.Task;
                    }

                    await wait.ConfigureAwait(false);
                }
            }
            finally
            {
                _lifetimeCts.Dispose();
            }
        }

        private async Task CompleteWhenAsync(
            Slot slot,
            ValueTask<HistoryLookupResult> lookup,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _inFlightCompletions);
            try
            {
                HistoryLookupResult result;
                var historyStart = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    result = await lookup.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested || Volatile.Read(ref _shutDown) == 1)
                {
                    CancelAndRelease(slot);
                    return;
                }
                catch (OperationCanceledException)
                {
                    result = HistoryLookupResult.Unavailable;
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception)
                {
                    result = HistoryLookupResult.Unavailable;
                }

                _session.FeedProbe?.RecordHistory(
                    result,
                    System.Diagnostics.Stopwatch.GetTimestamp() - historyStart);

                var emit = false;
                lock (_gate)
                {
                    if (!IsPresentNoLock(slot))
                    {
                        return;
                    }

                    if (Volatile.Read(ref _shutDown) == 1)
                    {
                        slot.Completed = true;
                        slot.Cancelled = true;
                        ReleaseCancelledPrefixNoLock();
                        SignalProgressNoLock();
                        return;
                    }

                    slot.Completed = true;
                    slot.Result = result;
                    emit = HeadIsReadyNoLock();
                    SignalProgressNoLock();
                }

                if (emit)
                {
                    try
                    {
                        await EmitReadySerializedAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        cancellationToken.IsCancellationRequested || Volatile.Read(ref _shutDown) == 1)
                    {
                        CancelAndRelease(slot);
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception)
                    {
                        CancelAndRelease(slot);
                    }
                }
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception)
            {
                CancelAndRelease(slot);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightCompletions);
            }
        }

        private async ValueTask EmitReadySerializedAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
            var token = linked.Token;
            await _emitGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var emitted = 0;
                while (true)
                {
                    Slot? slot;
                    lock (_gate)
                    {
                        if (Volatile.Read(ref _shutDown) == 1)
                        {
                            ReleaseCancelledPrefixNoLock();
                            SignalProgressNoLock();
                            return;
                        }

                        slot = TryBeginEmitHeadNoLock();
                    }

                    if (slot is null)
                    {
                        if (emitted > 0
                            && _response.HasCoalescedUnflushed
                            && Volatile.Read(ref _shutDown) == 0
                            && !token.IsCancellationRequested)
                        {
                            await _response.FlushCoalescedAsync(token).ConfigureAwait(false);
                        }

                        return;
                    }

                    try
                    {
                        await EmitOccupiedAsync(slot, token).ConfigureAwait(false);
                        lock (_gate)
                        {
                            ReleaseHeadIfNoLock(slot);
                            SignalProgressNoLock();
                        }

                        emitted++;
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception)
                    {
                        lock (_gate)
                        {
                            slot.Cancelled = true;
                            slot.Emitting = false;
                            ReleaseCancelledPrefixNoLock();
                            SignalProgressNoLock();
                        }

                        throw;
                    }
                }
            }
            finally
            {
                _emitGate.Release();
            }
        }

        private async ValueTask EmitOccupiedAsync(Slot slot, CancellationToken cancellationToken)
        {
            var deferOnDuplicate = Check.DeferOnDuplicate(_session);
            var owned = Check.Compose(slot.Result, slot.MessageId, deferOnDuplicate);
            if (BeforeEnqueueProbe is { } probe)
            {
                await probe().ConfigureAwait(false);
                if (Volatile.Read(ref _shutDown) == 1 || cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            if (Volatile.Read(ref _shutDown) == 1 || cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            await _response.EnqueueLineAsync(owned, cancellationToken).ConfigureAwait(false);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                NntpCommandExecution.WriteCompletion(
                    _logger,
                    _session,
                    "CHECK",
                    System.Diagnostics.Stopwatch.GetElapsedTime(slot.StartedTimestamp),
                    statusLine: NntpCommandStatusText.FormatCheck(slot.Result, slot.MessageId, deferOnDuplicate));
            }
        }

        private bool HeadIsReadyNoLock()
        {
            if (_count == 0)
            {
                return false;
            }

            var slot = _slots[_head];
            return slot is { Completed: true, Cancelled: false, Emitting: false };
        }

        private Slot? TryBeginEmitHeadNoLock()
        {
            if (!HeadIsReadyNoLock())
            {
                return null;
            }

            var slot = _slots[_head]!;
            slot.Emitting = true;
            return slot;
        }

        private void ReleaseHeadIfNoLock(Slot slot)
        {
            if (_count == 0 || !ReferenceEquals(_slots[_head], slot))
            {
                return;
            }

            _slots[_head] = null;
            _head = (_head + 1) % Depth;
            _count--;
            _session.EndCommandWork();
            slot.Emitting = false;
        }

        private void CancelAndRelease(Slot slot)
        {
            lock (_gate)
            {
                if (!IsPresentNoLock(slot))
                {
                    return;
                }

                slot.Completed = true;
                slot.Cancelled = true;
                slot.Emitting = false;
                ReleaseCancelledPrefixNoLock();
                SignalProgressNoLock();
            }
        }

        private void ReleaseCancelledPrefixNoLock()
        {
            while (_count > 0)
            {
                var slot = _slots[_head];
                if (slot is null || slot.Emitting || !slot.Cancelled)
                {
                    break;
                }

                _slots[_head] = null;
                _head = (_head + 1) % Depth;
                _count--;
                _session.EndCommandWork();
            }
        }

        private bool IsPresentNoLock(Slot slot) =>
            (uint)slot.Index < Depth && ReferenceEquals(_slots[slot.Index], slot);

        private void ThrowIfShutDown()
        {
            if (Volatile.Read(ref _shutDown) == 1)
            {
                throw new OperationCanceledException("CHECK pipeline has shut down.");
            }
        }

        private void SignalProgressNoLock()
        {
            var previous = _progress;
            _progress = NewProgress();
            previous.TrySetResult();
        }

        private static TaskCompletionSource NewProgress() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class Slot
        {
            public required byte[] MessageId { get; init; }

            public required int Index { get; init; }

            public long StartedTimestamp { get; init; }

            public HistoryLookupResult Result { get; set; }

            public bool Completed { get; set; }

            public bool Cancelled { get; set; }

            public bool Emitting { get; set; }
        }
    }
}
