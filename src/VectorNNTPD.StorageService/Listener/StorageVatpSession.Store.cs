using System.Buffers;
using System.Diagnostics;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Transport.ArticleTransfer;
using VectorNNTPD.StorageService.Ingress;

namespace VectorNNTPD.StorageService.Listener
{

    public sealed partial class StorageVatpSession
    {
        private async Task<bool> HandleStoreAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
        {
            var streamId = frame.Header.StreamId;
            if (!_clientHelloComplete)
            {
                await WriteFailAsync(streamId, VatpErrorCode.InvalidStateTransition, cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            if (!VatpStorePayload.TryDecode(frame.Payload, out var articleId, out var decodeError))
            {
                await WriteFailAsync(streamId, decodeError, cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (streamId == VatpProtocol.ConnectionStreamId
                || _streams.ContainsKey(streamId)
                || _storeStreams.ContainsKey(streamId)
                || _streams.Count + _storeStreams.Count >= _limits.MaxStreamsPerConnection)
            {
                await WriteFailAsync(
                        streamId,
                        streamId == VatpProtocol.ConnectionStreamId
                            ? VatpErrorCode.InvalidStreamId
                            : VatpErrorCode.StreamTableError,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            // Capture the gate that TryEnter acquired. Do not re-read _storeAdmission on release.
            StoreAssemblyAdmission? acquiredAdmission = null;
            var admission = _storeAdmission;
            if (admission is not null)
            {
                if (!admission.TryEnter())
                {
                    await WriteFailAsync(streamId, VatpErrorCode.StreamTableError, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

                acquiredAdmission = admission;
            }

            var receive = new ArticleTransferReceiveStream(
                streamId,
                StoreReceiveCorrelation,
                articleId,
                _limits);
            if (!_storeStreams.TryAdd(streamId, new StoreStream(receive, acquiredAdmission)))
            {
                acquiredAdmission?.Exit();

                await WriteFailAsync(streamId, VatpErrorCode.StreamTableError, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            return true;
        }

        private async Task<bool> HandleStoreMetaAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
        {
            var streamId = frame.Header.StreamId;
            if (!_storeStreams.TryGetValue(streamId, out var store))
            {
                await WriteFailAsync(streamId, VatpErrorCode.InvalidFrameType, cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (store.MetaSeen || store.BodyAuthorized || store.AdmissionTask is not null)
            {
                RemoveStore(streamId);
                await WriteFailAsync(streamId, VatpErrorCode.InvalidStateTransition, cancellationToken).ConfigureAwait(false);
                return true;
            }

            Span<byte> metaBytes = stackalloc byte[VatpProtocol.MetaPayloadLength];
            if (frame.Payload.Length != VatpProtocol.MetaPayloadLength)
            {
                RemoveStore(streamId);
                await WriteFailAsync(streamId, VatpErrorCode.InvalidMeta, cancellationToken).ConfigureAwait(false);
                return true;
            }

            frame.Payload.CopyTo(metaBytes);
            if (!VatpMetaCodec.TryDecode(metaBytes, out var meta, out var decodeError))
            {
                RemoveStore(streamId);
                await WriteFailAsync(streamId, decodeError, cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (meta.ArtSize < 1 || meta.ArtSize > ArticleResourceLimits.MaxArticleBytes)
            {
                RemoveStore(streamId);
                await WriteFailAsync(streamId, VatpErrorCode.ArticleTooLarge, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var queue = _acceptedArticles;
            if (queue is null)
            {
                return await AcceptMetaWithoutReservationAsync(streamId, store, frame.Payload, meta, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (meta.ArtSize > queue.MaxQueuedBytes || queue.IsCompleted)
            {
                RemoveStore(streamId);
                if (meta.ArtSize > queue.MaxQueuedBytes && !queue.IsCompleted)
                {
                    await WriteEncodedAsync(
                            VatpFrameEncoder.EncodeResult(streamId, VatpStoreOutcome.RejectedCapacity),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

                await WriteFailAsync(streamId, VatpErrorCode.Cancelled, cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (queue.TryReserve(meta.ArtSize, out var reserved))
            {
                return await AuthorizeReservedBodyAsync(streamId, store, frame.Payload, meta, reserved, cancellationToken)
                    .ConfigureAwait(false);
            }

            var metaCopy = new byte[VatpProtocol.MetaPayloadLength];
            frame.Payload.CopyTo(metaCopy);
            lock (_storeGate)
            {
                if (store.MetaSeen || store.AdmissionTask is not null)
                {
                    RemoveStore(streamId);
                }
                else
                {
                    store.PendingMeta = metaCopy;
                    store.AdmissionCts = CancellationTokenSource.CreateLinkedTokenSource(_runCancellation);
                    // Log before starting the waiter. AdmissionWaiters can become visible as soon as
                    // ReserveAsync blocks; observers that wait on waiters must already see this log.
                    StorageVatpSessionLogMessages.StoreAdmissionWaiting(_logger, streamId, meta.ArtSize);
                    store.AdmissionTask = AdmitWhenCapacityAsync(streamId, store, meta.ArtSize, store.AdmissionCts.Token);
                    SyncAdmissionReadTimeout();
                }
            }

            if (store.AdmissionTask is null)
            {
                await WriteFailAsync(streamId, VatpErrorCode.InvalidStateTransition, cancellationToken).ConfigureAwait(false);
                return true;
            }

            return true;
        }

        private async Task AdmitWhenCapacityAsync(
            uint streamId,
            StoreStream store,
            int bytes,
            CancellationToken cancellationToken)
        {
            var queue = _acceptedArticles;
            if (queue is null)
            {
                return;
            }

            QueueReservation? reservation = null;
            try
            {
                reservation = await queue.ReserveAsync(bytes, cancellationToken).ConfigureAwait(false);
                byte[]? metaCopy;
                lock (_storeGate)
                {
                    if (store.Closed || !_storeStreams.ContainsKey(streamId) || store.PendingMeta is null)
                    {
                        metaCopy = null;
                    }
                    else if (reservation is null)
                    {
                        metaCopy = null;
                        store.AdmissionFailed = true;
                    }
                    else
                    {
                        metaCopy = store.PendingMeta;
                        store.ReservationHold.Bind(queue, reservation.Value);
                        reservation = null;
                    }
                }

                if (metaCopy is null)
                {
                    if (reservation is { } abandoned)
                    {
                        queue.Release(abandoned);
                    }

                    if (store.AdmissionFailed && !store.Closed)
                    {
                        RemoveStore(streamId);
                        await WriteFailAsync(streamId, VatpErrorCode.Cancelled, CancellationToken.None).ConfigureAwait(false);
                    }

                    return;
                }

                if (!VatpMetaCodec.TryDecode(metaCopy, out var meta, out var decodeError))
                {
                    RemoveStore(streamId);
                    await WriteFailAsync(streamId, decodeError, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                await AuthorizeReservedBodyAsync(
                        streamId,
                        store,
                        new ReadOnlySequence<byte>(metaCopy),
                        meta,
                        store.ReservationHold.Reservation,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (reservation is { } abandoned)
                {
                    queue.Release(abandoned);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                if (reservation is { } abandoned)
                {
                    queue.Release(abandoned);
                }
            }
            finally
            {
                lock (_storeGate)
                {
                    SyncAdmissionReadTimeout();
                }
            }
        }

        /// <summary>
        /// Suspends the read no-progress timeout while a STORE is waiting for queue capacity.
        /// Resumes it when no such wait remains, including after WINDOW is granted.
        /// Caller holds <see cref="_storeGate"/>.
        /// </summary>
        private void SyncAdmissionReadTimeout()
        {
            var pending = false;
            foreach (var store in _storeStreams.Values)
            {
                if (!store.Closed && !store.BodyAuthorized && store.AdmissionTask is { IsCompleted: false })
                {
                    pending = true;
                    break;
                }
            }

            _transport.SetReadProgressSuspended(pending);
        }

        private async Task<bool> AcceptMetaWithoutReservationAsync(
            uint streamId,
            StoreStream store,
            ReadOnlySequence<byte> payload,
            ArticleCanonicalTransferMeta meta,
            CancellationToken cancellationToken)
        {
            var apply = store.Receive.TryAcceptMeta(payload);
            if (!apply.Success)
            {
                RemoveStore(streamId);
                await WriteFailAsync(streamId, store.Receive.Failure, cancellationToken).ConfigureAwait(false);
                return true;
            }

            store.MetaArtHash = meta.ArtHash;
            store.MetaArtSize = meta.ArtSize;
            store.MetaSeen = true;
            store.BodyAuthorized = true;
            return true;
        }

        private async Task<bool> AuthorizeReservedBodyAsync(
            uint streamId,
            StoreStream store,
            ReadOnlySequence<byte> payload,
            ArticleCanonicalTransferMeta meta,
            QueueReservation reservation,
            CancellationToken cancellationToken)
        {
            // True when AdmitWhenCapacityAsync already bound this reservation before this call.
            // Combined with StoreReservationHold.TryRelease (exactly-once), prevents double Release
            // when RemoveStore races with this failure path.
            var reservationAlreadyBound = store.ReservationHold.IsBound;
            VatpErrorCode failure;
            lock (_storeGate)
            {
                if (store.Closed || store.BodyAuthorized)
                {
                    failure = VatpErrorCode.InvalidStateTransition;
                }
                else
                {
                    var apply = store.Receive.TryAcceptMeta(payload);
                    if (!apply.Success)
                    {
                        failure = store.Receive.Failure;
                    }
                    else
                    {
                        store.ReservationHold.Bind(_acceptedArticles, reservation);
                        store.MetaArtHash = meta.ArtHash;
                        store.MetaArtSize = meta.ArtSize;
                        store.MetaSeen = true;
                        store.BodyAuthorized = true;
                        Interlocked.Increment(ref StoreBodiesAllocated);
                        failure = VatpErrorCode.None;
                    }
                }
            }

            if (failure != VatpErrorCode.None)
            {
                if (!store.ReservationHold.TryRelease() && !reservationAlreadyBound)
                {
                    // Unbound TryReserve/ReserveAsync slot still owned by this call.
                    _acceptedArticles?.Release(reservation);
                }

                RemoveStore(streamId);
                await WriteFailAsync(streamId, failure, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var grant = _limits.InitialStreamWindowBytes;
            if (grant > 0)
            {
                await WriteEncodedAsync(
                        VatpFrameEncoder.EncodeWindow(streamId, (uint)grant),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return true;
        }

        private async Task<bool> HandleStoreDataAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
        {
            var streamId = frame.Header.StreamId;
            if (!_storeStreams.TryGetValue(streamId, out var store))
            {
                await WriteFailAsync(streamId, VatpErrorCode.InvalidFrameType, cancellationToken).ConfigureAwait(false);
                return false;
            }

            ArticleTransferApplyResult apply;
            var unauthorized = false;
            var dataStarted = Stopwatch.GetTimestamp();
            lock (_storeGate)
            {
                if (!store.BodyAuthorized || store.Closed)
                {
                    unauthorized = true;
                    apply = ArticleTransferApplyResult.Fail(VatpErrorCode.FlowControlViolation);
                }
                else
                {
                    apply = store.Receive.TryAcceptData(frame.Payload, frame.Header.HasFin);
                }
            }

            if (unauthorized)
            {
                RemoveStore(streamId);
                await WriteFailAsync(streamId, VatpErrorCode.FlowControlViolation, cancellationToken).ConfigureAwait(false);
                return true;
            }

            NoteDataAccept(
                Stopwatch.GetTimestamp() - dataStarted,
                apply.Success ? frame.Payload.Length : 0);
            if (!apply.Success)
            {
                var error = store.Receive.Failure;
                RemoveStore(streamId);
                await WriteFailAsync(streamId, error, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var payloadLength = checked((uint)frame.Payload.Length);
            if (payloadLength > 0 && store.Receive.TryAcceptWindow(payloadLength).Success)
            {
                var windowStarted = Stopwatch.GetTimestamp();
                await WriteEncodedAsync(VatpFrameEncoder.EncodeWindow(streamId, payloadLength), cancellationToken)
                    .ConfigureAwait(false);
                NoteWindowWrite(Stopwatch.GetTimestamp() - windowStarted);
            }

            if (store.Receive.IsTerminal)
            {
                var error = store.Receive.Failure == VatpErrorCode.None
                    ? VatpErrorCode.IncompleteTransfer
                    : store.Receive.Failure;
                RemoveStore(streamId);
                await WriteFailAsync(streamId, error, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }

        private async Task<bool> HandleStoreEndAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
        {
            var streamId = frame.Header.StreamId;
            if (!_storeStreams.TryGetValue(streamId, out var store))
            {
                await WriteFailAsync(streamId, VatpErrorCode.InvalidFrameType, cancellationToken).ConfigureAwait(false);
                return false;
            }

            var endStarted = Stopwatch.GetTimestamp();
            ArticleTransferApplyResult apply;
            var tookRecord = false;
            ArticleRecord record = default;
            lock (_storeGate)
            {
                apply = store.Receive.TryAcceptEnd();
                if (apply.Success)
                {
                    tookRecord = store.Receive.TryTakeRecord(out record);
                }
            }

            NoteEndValidate(Stopwatch.GetTimestamp() - endStarted);
            if (!tookRecord)
            {
                var error = store.Receive.Failure == VatpErrorCode.None
                    ? VatpErrorCode.IncompleteTransfer
                    : store.Receive.Failure;
                RemoveStore(streamId);
                await WriteFailAsync(streamId, error, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var mismatch = Mismatch(store, record);
            if (mismatch != VatpErrorCode.None)
            {
                RemoveStore(streamId);
                await WriteFailAsync(streamId, mismatch, cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (_acceptedArticles is null)
            {
                RemoveStore(streamId);
                await WriteFailAsync(streamId, VatpErrorCode.OpenRejected, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var queued = new AcceptedArticle(record.ArtId, record.ArtHash, record.ArtSize, record.ArtData);
            var committed = false;
            // Take hold ownership before TryCommit so concurrent RemoveStore cannot Release
            // a reservation the queue has already consumed (or is consuming).
            if (store.ReservationHold.TryTakeForCommit(out var commitQueue, out var commitReservation))
            {
                try
                {
                    committed = commitQueue!.TryCommit(commitReservation, queued);
                }
                catch
                {
                    // TryCommit throws before mutating the reservation table; return the slot.
                    commitQueue!.Release(commitReservation);
                    store.ReservationHold.MarkCommitOutcome(false);
                    RemoveStore(streamId);
                    throw;
                }

                store.ReservationHold.MarkCommitOutcome(committed);
            }

            if (!committed)
            {
                RemoveStore(streamId);
                await WriteFailAsync(streamId, VatpErrorCode.IncompleteTransfer, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var resultStarted = Stopwatch.GetTimestamp();
            store.AcceptStarted = true;
            RemoveStore(streamId);
            StorageVatpSessionLogMessages.StoreCompleted(
                _logger,
                record.ArtId.ToLowerHexString(),
                "Accepted");
            await WriteEncodedAsync(
                    VatpFrameEncoder.EncodeResult(streamId, VatpStoreOutcome.Accepted),
                    cancellationToken)
                .ConfigureAwait(false);
            NoteEnqueueToResult(Stopwatch.GetTimestamp() - resultStarted);
            return true;
        }

        private static VatpErrorCode Mismatch(StoreStream store, in ArticleRecord record)
        {
            if (!store.MetaSeen)
            {
                return VatpErrorCode.IncompleteTransfer;
            }

            if (!record.ArtId.Equals(store.Receive.ExpectedArtId))
            {
                return VatpErrorCode.ArticleIdMismatch;
            }

            if (record.ArtHash != store.MetaArtHash)
            {
                return VatpErrorCode.ArtHashMismatch;
            }

            if (record.ArtSize != store.MetaArtSize)
            {
                return VatpErrorCode.ArtSizeMismatch;
            }

            return VatpErrorCode.None;
        }

        private void RemoveStore(uint streamId)
        {
            if (!_storeStreams.TryRemove(streamId, out var store))
            {
                return;
            }

            store.Closed = true;
            // Exit the admission instance that TryEnter acquired — never re-read _storeAdmission.
            store.ReleaseAcquiredAdmission();

            try
            {
                store.AdmissionCts?.Cancel();
            }
            catch (ObjectDisposedException ex)
            {
                ExpectedException.Ignore(ex);
            }

            // Serialize with Bind (always under _storeGate) so TryRelease cannot miss a
            // mid-Bind publication. Monitor is reentrant for RemoveStore under the gate.
            lock (_storeGate)
            {
                if (!store.ReservationHold.Committed)
                {
                    store.ReservationHold.TryRelease();
                }
            }
        }

        private void ReleaseStoreSlots()
        {
            if (_storeStreams.Count == 0)
            {
                return;
            }

            var ids = new uint[_storeStreams.Count];
            _storeStreams.Keys.CopyTo(ids, 0);
            foreach (var streamId in ids)
            {
                RemoveStore(streamId);
            }
        }

        private sealed class StoreStream
        {
            private StoreAssemblyAdmission? _acquiredAdmission;

            public StoreStream(ArticleTransferReceiveStream receive, StoreAssemblyAdmission? acquiredAdmission)
            {
                Receive = receive;
                _acquiredAdmission = acquiredAdmission;
            }

            public ArticleTransferReceiveStream Receive { get; }

            /// <summary>Exactly-once hold for the queue reservation bound to this stream.</summary>
            public StoreReservationHold ReservationHold { get; } = new();

            /// <summary>Gets whether an assembly admission slot is still held for this stream.</summary>
            public bool SlotHeld => Volatile.Read(ref _acquiredAdmission) is not null;

            public bool MetaSeen { get; set; }

            public ulong MetaArtHash { get; set; }

            public int MetaArtSize { get; set; }

            public bool AcceptStarted { get; set; }

            public bool BodyAuthorized { get; set; }

            public bool Closed { get; set; }

            public bool AdmissionFailed { get; set; }

            public byte[]? PendingMeta { get; set; }

            public CancellationTokenSource? AdmissionCts { get; set; }

            public Task? AdmissionTask { get; set; }

            /// <summary>Exits the assembly admission slot acquired for this stream, if any.</summary>
            public void ReleaseAcquiredAdmission()
            {
                // Exactly-once: concurrent RemoveStore / failure paths must not double-Exit.
                Interlocked.Exchange(ref _acquiredAdmission, null)?.Exit();
            }
        }
    }
}
