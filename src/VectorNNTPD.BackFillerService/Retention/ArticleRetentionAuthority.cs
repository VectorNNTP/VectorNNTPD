using System.Runtime.InteropServices;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.Common.Articles;
using CanonicalBufferLease = VectorNNTPD.Common.Articles.CanonicalArticleBuffer.CanonicalBufferLease;
using VectorNNTPD.Common.Articles.Parsing;
using NntpArticleIdentity = VectorNNTPD.Common.Articles.Processing.NntpArticleIdentity;

namespace VectorNNTPD.BackFillerService.Retention
{
    /// <summary>
    /// In-memory retention authority. One process-wide owner of retained CanonicalV1 article lifetime.
    /// The sole retained byte representation is <see cref="ArticleRecord.ArtData"/>.
    /// Openable RequestIds pin an entry against FIFO capacity reclaim until cancel, TTL, committed NNTP delivery, or dispose.
    /// </summary>
    /// <remarks>
    /// Index, counter, and admission mutations run under <see cref="_gate"/>.
    /// <see cref="TryClaimForDelivery"/> builds its dictionary key before taking that lock.
    /// The locked claim path does not walk <see cref="ArticleRecord.ArtData"/>.
    /// Frameability is checked on the claimed bytes after the lock is released, because the caller retains
    /// the ArtData array and may change it after admission. A failed check rolls the claim back before the
    /// method returns.
    /// <see cref="SweepInterval"/> is fixed at construction.
    /// ArtData is referenced, not copied. A delivery reader does not pin FIFO reclaim.
    /// An openable RequestId or an active <see cref="ArticleDeliveryClaim"/> does.
    /// TTL expiry and
    /// <see cref="DisposeAsync"/> drop the authority's ArtData reference and the retained-byte
    /// count immediately. A delivery claim keeps the captured bytes until it settles and does not
    /// extend <see cref="RetainedEntry.ExpiresUtc"/> or the accounting. FIFO physical release of an
    /// unexpired entry still waits until its reader count is zero. Cancel unpins the entry only when
    /// no openable RequestId remains and no delivery claim is active.
    /// TTL removal and <see cref="DisposeAsync"/> drop every RequestId on the entry.
    /// <see cref="BeginShutdown"/> rejects new <see cref="RetainCanonical"/> calls and leaves existing
    /// entries openable.
    /// </remarks>
    internal sealed class ArticleRetentionAuthority : IArticleRetentionAuthority, IAsyncDisposable
    {
        /// <summary>
        /// Non-recursive lock for the indexes, byte counters, admission flag, and reader acquire/release.
        /// No caller enters it again on the same call stack. <see cref="SweepInterval"/> does not use it.
        /// </summary>
        private readonly Lock _gate = new();

        /// <summary>Exact Message-ID to its live entry. Comparison is ordinal. At most one entry per Message-ID.</summary>
        private readonly Dictionary<string, RetainedEntry> _byMessageId = new(StringComparer.Ordinal);

        /// <summary>Active NNTP claims. The value is the indexed entry. Mutated only under <see cref="_gate"/>.</summary>
        private readonly Dictionary<ArticleDeliveryClaim, RetainedEntry> _deliveryClaims = [];

        /// <summary>
        /// ArticleId hex to the Message-ID that owns it. Used to reject a different Message-ID that
        /// presents the same ArticleId. Removed when the entry is unindexed.
        /// </summary>
        private readonly Dictionary<string, string> _messageIdByArticleId = new(StringComparer.Ordinal);

        /// <summary>
        /// Openable RequestId to the entry that holds it. A RequestId maps to at most one entry.
        /// Removed when that RequestId is consumed, cancelled, or cleared by unindex or dispose.
        /// </summary>
        private readonly Dictionary<Guid, RetainedEntry> _byRequestId = [];

        /// <summary>
        /// Admission order, oldest at <see cref="LinkedList{T}.First"/>. FIFO reclaim and TTL sweep
        /// walk from that end. <see cref="RetainedEntry.ExpiresUtc"/> is the admission time plus
        /// <see cref="_ttl"/>, so this order matches expiry order when <see cref="_time"/> does not
        /// move backward. <see cref="ExpireEligibleLocked"/> then stops at the first unexpired node.
        /// </summary>
        private readonly LinkedList<RetainedEntry> _insertionOrder = [];

        /// <summary>Clock for admission time, <see cref="RetainedEntry.ExpiresUtc"/>, and sweep. Not <see cref="DateTimeOffset.UtcNow"/>.</summary>
        private readonly TimeProvider _time;

        /// <summary>Retention event logger. Article bodies are not written.</summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Maximum owned ArtData bytes. A single buffer larger than this is
        /// <see cref="ArticleRetentionKind.PayloadExceedsCapacity"/> and does not trigger reclaim.
        /// </summary>
        private readonly long _maximumBytes;

        /// <summary>Added to the admission timestamp to produce <see cref="RetainedEntry.ExpiresUtc"/>. Greater than zero.</summary>
        private readonly TimeSpan _ttl;

        /// <summary>Per-entry cap on openable RequestIds, from 1 through 256 inclusive.</summary>
        private readonly int _maxOpenableRequestIdsPerArticle;

        /// <summary>Listener host copied onto each newly retained entry. Existing entries keep the value from their admission.</summary>
        private readonly string _fqdn;

        /// <summary>TLS listener port copied onto each newly retained entry. Existing entries keep the value from their admission.</summary>
        private readonly int _bindPort;

        /// <summary>
        /// Sum of <see cref="RetainedEntry.PayloadBytes"/> for entries not yet physically disposed,
        /// including logically removed entries that still have a reader. Subtracted only under
        /// <see cref="_gate"/> and never below zero.
        /// </summary>
        private long _retainedBytes;

        /// <summary>
        /// Entries not yet physically disposed. Logical removal does not decrement this until
        /// <see cref="DisposePhysicallyLocked"/> succeeds. Never below zero.
        /// </summary>
        private int _physicalCount;

        /// <summary>
        /// When <see langword="true"/>, <see cref="RetainCanonical"/> returns
        /// <see cref="ArticleRetentionKind.ShuttingDown"/>. Set by <see cref="BeginShutdown"/> and
        /// <see cref="DisposeAsync"/>. Cancel and sweep do not consult it.
        /// </summary>
        private bool _admissionClosed;

        /// <summary>
        /// When <see langword="true"/>, the locked sections of <see cref="RetainCanonical"/>,
        /// <see cref="TryClaimForDelivery"/>, and <see cref="TryCancelPendingRequest"/> throw
        /// <see cref="ObjectDisposedException"/>. <see cref="ArticleRetentionKind.InvalidPayload"/> and
        /// <see cref="ArticleRetentionKind.PayloadExceedsCapacity"/> are returned before that check.
        /// <see cref="SweepExpired"/> returns zero. <see cref="DisposeAsync"/> is idempotent.
        /// </summary>
        private bool _disposed;

        /// <summary>Creates the authority from validated runtime options.</summary>
        /// <param name="runtime">
        /// Host snapshot. <see cref="BackFillerRuntimeOptions.ArticleRetention"/>,
        /// <see cref="BackFillerRuntimeOptions.Fqdn"/>, and <see cref="BackFillerRuntimeOptions.BindPortTls"/>
        /// are forwarded to the explicit constructor.
        /// </param>
        /// <param name="time">Clock for admission timestamps, TTL, and sweep.</param>
        /// <param name="logger">Retention logger. Article bodies are not written.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="runtime"/>, <paramref name="time"/>, or <paramref name="logger"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException"><see cref="BackFillerRuntimeOptions.Fqdn"/> is null or whitespace.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The retention policy or <see cref="BackFillerRuntimeOptions.BindPortTls"/> fails the explicit constructor.
        /// </exception>
        internal ArticleRetentionAuthority(
            BackFillerRuntimeOptions runtime,
            TimeProvider time,
            ILogger<ArticleRetentionAuthority> logger)
            : this(
                (runtime ?? throw new ArgumentNullException(nameof(runtime))).ArticleRetention,
                runtime.Fqdn,
                runtime.BindPortTls,
                time,
                logger)
        {
        }

        /// <summary>Creates the authority with an explicit retention policy (tests and host).</summary>
        /// <param name="retention">Capacity, TTL, sweep interval, and per-article RequestId bound.</param>
        /// <param name="fqdn">Listener host stored on every newly retained entry. Not null or whitespace.</param>
        /// <param name="bindPort">TLS listener port stored on every newly retained entry. Inclusive range 1 through 65535.</param>
        /// <param name="time">Clock for admission timestamps, TTL, and sweep.</param>
        /// <param name="logger">Retention logger. Article bodies are not written.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="retention"/>, <paramref name="time"/>, or <paramref name="logger"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException"><paramref name="fqdn"/> is null or whitespace.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <see cref="BackFillerArticleRetentionRuntimeOptions.MaximumRetainedPayloadBytes"/> is not positive,
        /// <see cref="BackFillerArticleRetentionRuntimeOptions.RetentionTtl"/> or
        /// <see cref="BackFillerArticleRetentionRuntimeOptions.SweepInterval"/> is not positive,
        /// <see cref="BackFillerArticleRetentionRuntimeOptions.MaxOpenableRequestIdsPerArticle"/> is outside 1 through 256,
        /// or <paramref name="bindPort"/> is outside 1 through 65535.
        /// </exception>
        internal ArticleRetentionAuthority(
            BackFillerArticleRetentionRuntimeOptions retention,
            string fqdn,
            int bindPort,
            TimeProvider time,
            ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(retention);
            ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
            ArgumentNullException.ThrowIfNull(time);
            ArgumentNullException.ThrowIfNull(logger);
            if (retention.MaximumRetainedPayloadBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(retention), "MaximumRetainedPayloadBytes must be greater than zero.");
            }

            if (retention.RetentionTtl <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(retention), "RetentionTtl must be greater than zero.");
            }

            if (retention.SweepInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(retention), "SweepInterval must be greater than zero.");
            }

            if (retention.MaxOpenableRequestIdsPerArticle is < 1 or > 256)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(retention),
                    "MaxOpenableRequestIdsPerArticle must be between 1 and 256.");
            }

            ArgumentOutOfRangeException.ThrowIfLessThan(bindPort, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(bindPort, 65535);

            _maximumBytes = retention.MaximumRetainedPayloadBytes;
            _ttl = retention.RetentionTtl;
            SweepInterval = retention.SweepInterval;
            _maxOpenableRequestIdsPerArticle = retention.MaxOpenableRequestIdsPerArticle;
            _fqdn = fqdn;
            _bindPort = bindPort;
            _time = time;
            _logger = logger;
        }

        /// <inheritdoc />
        public TimeSpan SweepInterval { get; }

        /// <summary>Gets currently owned retained ArtData bytes, read under <see cref="_gate"/>.</summary>
        /// <remarks>Includes bytes of logically removed entries that still have a delivery reader.</remarks>
        public long RetainedPayloadBytes
        {
            get
            {
                lock (_gate)
                {
                    return _retainedBytes;
                }
            }
        }

        /// <summary>
        /// Gets the number of entries not yet physically disposed, read under <see cref="_gate"/>.
        /// </summary>
        /// <remarks>
        /// TTL expiry and disposal decrement this count when the authority drops ArtData, even if a
        /// delivery reader still exists. FIFO removal of an unexpired entry waits until that reader is released.
        /// </remarks>
        public int RetainedCount
        {
            get
            {
                lock (_gate)
                {
                    return _physicalCount;
                }
            }
        }

        /// <summary>
        /// Retains <paramref name="record"/> or attaches <paramref name="requestId"/> to the live Message-ID entry.
        /// </summary>
        /// <param name="messageId">Exact Message-ID. Not normalized. Null or whitespace is rejected.</param>
        /// <param name="requestId">ArticleWork Success capability. <see cref="Guid.Empty"/> is rejected.</param>
        /// <param name="record">
        /// Candidate record. Stored only when <see cref="ArticleRetentionKind.Retained"/> is returned.
        /// <see cref="ArticleParseStatus.CanonicalV1"/>, a positive <see cref="ArticleRecord.ArtSize"/>,
        /// and ArtData that is the record's whole array at offset zero are required.
        /// </param>
        /// <param name="selectedDateHeaderName">
        /// Date-family header name stored on a new entry. An <see cref="ArticleRetentionKind.AlreadyPresent"/>
        /// result keeps the header name from the first admission.
        /// </param>
        /// <param name="endpointFqdn">
        /// FQDN captured with this article's shared snapshot. Null or white space uses the FQDN supplied at construction.
        /// </param>
        /// <returns>
        /// The admission kind, the identity when one was computed, the listener endpoint when the article
        /// remains available, the owned byte total, and bytes physically released during this attempt.
        /// </returns>
        /// <exception cref="ArgumentException"><paramref name="messageId"/> is null or whitespace.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="requestId"/> is <see cref="Guid.Empty"/>.</exception>
        /// <exception cref="ObjectDisposedException">Thrown from the locked section when <see cref="_disposed"/> is set.</exception>
        /// <remarks>
        /// <see cref="ArticleRetentionKind.InvalidPayload"/> and
        /// <see cref="ArticleRetentionKind.PayloadExceedsCapacity"/> are decided before the lock and do not
        /// throw <see cref="ObjectDisposedException"/>. Those results do not mutate indexes.
        /// An exact Message-ID hit is resolved before ArticleId collision. An expired hit is removed and
        /// admission continues with <paramref name="record"/>; a live hit keeps the first
        /// <see cref="ArticleRecord"/> and does not store <paramref name="record"/>.
        /// A RequestId already mapped to a different Message-ID is
        /// <see cref="ArticleRetentionKind.ArticleIdCollision"/>.
        /// An expired ArticleId collision is removed so the new Message-ID can be indexed.
        /// FIFO reclaim then runs only for a new entry. The incoming array is not copied.
        /// Byte accounting uses <see cref="ArticleRecord.ArtSize"/>, which is the logical article length.
        /// A rented array may be longer. Storing a new entry adds one buffer consumer. Expiry, FIFO
        /// reclaim, capacity eviction, and a committed delivery each release that consumer once.
        /// A delivery claim holds a separate consumer until the claim settles.
        /// </remarks>
        public ArticleRetentionResult RetainCanonical(
            string messageId,
            Guid requestId,
            ArticleRecord record,
            NntpArticleHeaderName selectedDateHeaderName,
            string? endpointFqdn = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            var admittedFqdn = string.IsNullOrWhiteSpace(endpointFqdn) ? _fqdn : endpointFqdn;
            // ThrowIfEqual changes both the message and ActualValue, so replacing the existing ArgumentOutOfRangeException would be a
            // behavioral change for no functional benefit
#pragma warning disable CA1512 // Use ArgumentOutOfRangeException throw helper
            if (requestId == Guid.Empty)
            {
                throw new ArgumentOutOfRangeException(nameof(requestId));
            }
#pragma warning restore CA1512 // Use ArgumentOutOfRangeException throw helper

            if (record.ParseStatus != ArticleParseStatus.CanonicalV1
                || record.ArtSize <= 0
                || !TryGetOwnedArtData(in record, out _))
            {
                ArticleRetentionLogMessages.Rejected(
                    _logger,
                    ArticleRetentionKind.InvalidPayload,
                    null,
                    record.ArtSize,
                    RetainedPayloadBytes);
                return new ArticleRetentionResult(
                    ArticleRetentionKind.InvalidPayload,
                    null,
                    null,
                    null,
                    RetainedPayloadBytes,
                    0);
            }

            if (record.ArtSize > _maximumBytes)
            {
                ArticleRetentionLogMessages.Rejected(
                    _logger,
                    ArticleRetentionKind.PayloadExceedsCapacity,
                    null,
                    record.ArtSize,
                    RetainedPayloadBytes);
                return new ArticleRetentionResult(
                    ArticleRetentionKind.PayloadExceedsCapacity,
                    null,
                    null,
                    null,
                    RetainedPayloadBytes,
                    0);
            }

            var identity = ArticleIdentity.From(messageId, record.ArtId);
            var now = _time.GetUtcNow();
            using var pooledReturns = CanonicalArticleBuffer.ReturnScope.Enter();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_admissionClosed)
                {
                    return RejectLocked(ArticleRetentionKind.ShuttingDown, identity, record.ArtSize, 0);
                }

                if (_byRequestId.TryGetValue(requestId, out var requestOwner)
                    && !string.Equals(requestOwner.Identity.MessageId, messageId, StringComparison.Ordinal))
                {
                    return RejectLocked(ArticleRetentionKind.ArticleIdCollision, identity, record.ArtSize, 0);
                }

                if (_byMessageId.TryGetValue(messageId, out var existing))
                {
                    if (IsExpiredLocked(existing, now))
                    {
                        RemoveExpiredLocked(existing);
                    }
                    else
                    {
                        // First-wins ArtData: keep the retained ArticleRecord; add RequestId as another
                        // openable capability. Never revoke an existing RequestId to admit a newer one.
                        if (!AttachRequestIdLocked(existing, requestId))
                        {
                            return RejectLocked(
                                ArticleRetentionKind.OpenableRequestIdLimitExceeded,
                                existing.Identity,
                                record.ArtSize,
                                0);
                        }

                        return new ArticleRetentionResult(
                            ArticleRetentionKind.AlreadyPresent,
                            existing.Identity,
                            existing.Fqdn,
                            existing.ListenerPort,
                            _retainedBytes,
                            0);
                    }
                }

                if (_messageIdByArticleId.TryGetValue(identity.ArticleIdHex, out var colliding)
                    && !string.Equals(colliding, messageId, StringComparison.Ordinal))
                {
                    if (_byMessageId.TryGetValue(colliding, out var collidingEntry) && IsExpiredLocked(collidingEntry, now))
                    {
                        RemoveExpiredLocked(collidingEntry);
                    }
                    else
                    {
                        return RejectLocked(ArticleRetentionKind.ArticleIdCollision, identity, record.ArtSize, 0);
                    }
                }

                var released = ReclaimForAdmissionLocked(record.ArtSize, now);
                if (_retainedBytes + record.ArtSize > _maximumBytes)
                {
                    return RejectLocked(ArticleRetentionKind.CapacityUnavailable, identity, record.ArtSize, released);
                }

                RetainedEntry? entry = null;
                var messageIndexed = false;
                var articleIndexed = false;
                var requestAttached = false;
                var accounted = false;
                CanonicalBufferLease? lease = null;
                try
                {
                    entry = new RetainedEntry(
                        identity,
                        admittedFqdn,
                        _bindPort,
                        record,
                        selectedDateHeaderName,
                        now + _ttl);
                    entry.Node = _insertionOrder.AddLast(entry);
                    ThrowPublicationFault(PublicationFaultAfterLink);
                    _byMessageId.Add(messageId, entry);
                    messageIndexed = true;
                    ThrowPublicationFault(PublicationFaultAfterMessageId);
                    _messageIdByArticleId.Add(identity.ArticleIdHex, messageId);
                    articleIndexed = true;
                    ThrowPublicationFault(PublicationFaultAfterArticleId);
                    requestAttached = AttachRequestIdLocked(entry, requestId);
                    AddBytesLocked(record.ArtSize);
                    _physicalCount++;
                    accounted = true;
                    ThrowPublicationFault(PublicationFaultAfterAccounting);
                    if (!record.TryCreateBufferLease(out lease))
                    {
                        RollbackPublication(
                            entry,
                            messageId,
                            identity.ArticleIdHex,
                            requestId,
                            messageIndexed,
                            articleIndexed,
                            requestAttached,
                            accounted,
                            lease);
                        return RejectLocked(ArticleRetentionKind.InvalidPayload, identity, record.ArtSize, released);
                    }

                    entry.HoldBuffer(lease);
                    lease = null;
                    ThrowPublicationFault(PublicationFaultAfterLease);
                    var result = new ArticleRetentionResult(
                        ArticleRetentionKind.Retained,
                        identity,
                        entry.Fqdn,
                        entry.ListenerPort,
                        _retainedBytes,
                        released);
                    ArticleRetentionLogMessages.Retained(_logger, identity.ArticleIdHex, record.ArtSize, _retainedBytes);
                    return result;
                }
                catch
                {
                    if (entry is not null)
                    {
                        RollbackPublication(
                            entry,
                            messageId,
                            identity.ArticleIdHex,
                            requestId,
                            messageIndexed,
                            articleIndexed,
                            requestAttached,
                            accounted,
                            lease);
                    }
                    else
                    {
                        lease?.Dispose();
                    }

                    throw;
                }
            }
        }

        /// <summary>Detaches one openable RequestId. Does not unindex the Message-ID or release ArtData.</summary>
        /// <param name="requestId">RequestId to remove. <see cref="Guid.Empty"/> is rejected.</param>
        /// <returns>
        /// <see langword="true"/> when the RequestId was removed from its entry.
        /// <see langword="false"/> when it is not currently openable.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="requestId"/> is <see cref="Guid.Empty"/>.</exception>
        /// <exception cref="ObjectDisposedException">The authority has been disposed.</exception>
        /// <remarks>
        /// Other RequestIds on the same entry stay openable. The entry becomes FIFO-reclaimable only
        /// when the last openable RequestId is gone. An active reader is not affected.
        /// </remarks>
        public bool TryCancelPendingRequest(Guid requestId)
        {
            // ThrowIfEqual changes both the message and ActualValue, so replacing the existing ArgumentOutOfRangeException would be a
            // behavioral change for no functional benefit
#pragma warning disable CA1512 // Use ArgumentOutOfRangeException throw helper
            if (requestId == Guid.Empty)
            {
                throw new ArgumentOutOfRangeException(nameof(requestId));
            }
#pragma warning restore CA1512 // Use ArgumentOutOfRangeException throw helper

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_byRequestId.TryGetValue(requestId, out var entry))
                {
                    return false;
                }

                return DetachRequestIdLocked(entry, requestId);
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// The dictionary key is created before <see cref="_gate"/> is taken. An empty octet span still enters
        /// the lock so a disposed authority throws <see cref="ObjectDisposedException"/> instead of
        /// <see cref="ArticleDeliveryClaimKind.NotFound"/>.
        /// <see cref="NntpArticleIdentity.MatchesRequest(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/> stays inside
        /// the lock because it compares only the Message-ID, not <see cref="ArticleRecord.ArtData"/>.
        /// The article scan runs after the claim is published and the lock is released. The claim keeps the
        /// buffer alive for that scan. <see cref="UnpublishedDeliveryClaim"/> disposes that claim on every
        /// return and exception until <see cref="UnpublishedDeliveryClaim.Transfer"/> has built the caller's
        /// result and cleared the holder. A second lock then rejects the claim when the entry expired, was
        /// removed, or the authority was disposed during the scan.
        /// </remarks>
        public ArticleDeliveryClaimResult TryClaimForDelivery(ReadOnlySpan<byte> messageIdOctets)
        {
            var key = messageIdOctets.IsEmpty ? null : CreateDeliveryKey(messageIdOctets);
            using var pooledReturns = CanonicalArticleBuffer.ReturnScope.Enter();
            using var pending = new UnpublishedDeliveryClaim();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (key is null || !_byMessageId.TryGetValue(key, out var entry))
                {
                    return ArticleDeliveryClaimResult.NotFound();
                }

                if (IsExpiredLocked(entry, _time.GetUtcNow()))
                {
                    RemoveExpiredLocked(entry);
                    return ArticleDeliveryClaimResult.NotFound();
                }

                if (entry.IsLogicallyRemoved || entry.Record is not { } record)
                {
                    return ArticleDeliveryClaimResult.NotFound();
                }

                if (entry.IsDeliveryClaimed || entry.ReaderCount > 0)
                {
                    return ArticleDeliveryClaimResult.InDelivery();
                }

                if (!NntpArticleIdentity.MatchesRequest(record.MessageId, messageIdOctets))
                {
                    return ArticleDeliveryClaimResult.NotFrameable();
                }

                if (!entry.TryAcquire() || !entry.TryBeginDeliveryClaim())
                {
                    if (entry.ReaderCount > 0)
                    {
                        _ = entry.TryRelease();
                    }

                    return ArticleDeliveryClaimResult.NotFound();
                }

                CanonicalBufferLease? lease = null;
                var adopted = false;
                var added = false;
                try
                {
                    if (!record.TryCreateBufferLease(out lease))
                    {
                        return ArticleDeliveryClaimResult.NotFound();
                    }

                    pending.Adopt(new ArticleDeliveryClaim(this, record, entry.Identity.MessageId, lease));
                    adopted = true;
                    lease = null;
                    _deliveryClaims.Add(pending.Claim, entry);
                    added = true;
                }
                finally
                {
                    if (!added)
                    {
                        ReleaseDeliveryHoldLocked(entry);
                    }

                    if (!adopted)
                    {
                        lease?.Dispose();
                    }
                }
            }

            if (!ArticleWorkDeliveryFraming.IsFrameable(pending.Claim.ArticleBytes.Span))
            {
                return ArticleDeliveryClaimResult.NotFrameable();
            }

            var release = false;
            var disposedDuringScan = false;
            lock (_gate)
            {
                disposedDuringScan = _disposed;
                release = disposedDuringScan
                    || !_deliveryClaims.TryGetValue(pending.Claim, out var held)
                    || held.IsLogicallyRemoved
                    || IsExpiredLocked(held, _time.GetUtcNow());
            }

            if (disposedDuringScan)
            {
                throw new ObjectDisposedException(GetType().Name);
            }

            if (release)
            {
                return ArticleDeliveryClaimResult.NotFound();
            }

            return pending.Transfer();
        }

        /// <summary>
        /// Commits or rolls back <paramref name="claim"/>. A second call for the same claim does nothing.
        /// </summary>
        /// <param name="claim">Claim returned by <see cref="TryClaimForDelivery"/>.</param>
        /// <param name="commit">
        /// <see langword="true"/> removes an indexed, unexpired entry.
        /// <see langword="false"/> releases the claim and leaves an indexed, unexpired entry in place.
        /// </param>
        /// <remarks>
        /// Takes <see cref="_gate"/> only for the index update. Does not throw
        /// <see cref="ObjectDisposedException"/>: a claim that outlives <see cref="DisposeAsync"/>
        /// releases its reader and does not reindex the entry. An entry that is already expired is
        /// removed by the existing expiry path and is not returned to the index.
        /// </remarks>
        private void CompleteDelivery(ArticleDeliveryClaim claim, bool commit)
        {
            lock (_gate)
            {
                if (!claim.TryMarkSettled() || !_deliveryClaims.Remove(claim, out var entry))
                {
                    return;
                }

                var expired = !entry.IsLogicallyRemoved && IsExpiredLocked(entry, _time.GetUtcNow());
                if (commit && !entry.IsLogicallyRemoved && !expired)
                {
                    UnindexLocked(entry);
                    _ = DetachAuthorityPayloadLocked(entry);
                }
                else if (expired)
                {
                    _ = RemoveExpiredLocked(entry);
                }

                ReleaseDeliveryHoldLocked(entry);
            }

        }

        /// <summary>Clears the delivery claim and drops the reader acquired for it.</summary>
        /// <param name="entry">Entry whose claim is ending. Already logically removed entries are not reindexed.</param>
        /// <remarks>
        /// Called under <see cref="_gate"/>. When the entry is logically removed and this was the last reader,
        /// <see cref="DisposePhysicallyLocked"/> runs and does not subtract bytes that were already detached.
        /// </remarks>
        private void ReleaseDeliveryHoldLocked(RetainedEntry entry)
        {
            entry.EndDeliveryClaim();
            if (!entry.TryRelease())
            {
                return;
            }

            if (entry.IsLogicallyRemoved)
            {
                _ = DisposePhysicallyLocked(entry);
            }
        }

        /// <summary>
        /// Builds the ordinal dictionary key for <paramref name="messageIdOctets"/> without trimming or case-folding.
        /// </summary>
        /// <param name="messageIdOctets">Non-empty candidate Message-ID octets.</param>
        /// <returns>One character per octet. Byte <c>n</c> becomes <c>(char)n</c>, including values above 127.</returns>
        /// <remarks>
        /// <c>string.Create</c> writes the string in one allocation.
        /// The callback runs before this method returns, so the span remains valid for the copy.
        /// </remarks>
        private static string CreateDeliveryKey(ReadOnlySpan<byte> messageIdOctets)
        {
            return string.Create(messageIdOctets.Length, messageIdOctets, static (chars, octets) =>
            {
                for (var i = 0; i < octets.Length; i++)
                {
                    chars[i] = (char)octets[i];
                }
            });
        }

        /// <summary>Reclaims TTL-expired entries in insertion order.</summary>
        /// <returns>
        /// Payload bytes the authority released on this pass. Zero when the authority is disposed or nothing expired.
        /// An active transfer does not keep those bytes in the retained total.
        /// </returns>
        /// <remarks>
        /// Does not throw <see cref="ObjectDisposedException"/>. Does not close admission.
        /// Writes <see cref="ArticleRetentionLogMessages.Swept"/> only when the returned total is positive.
        /// The walk stops at the first entry with <see cref="RetainedEntry.ExpiresUtc"/> still in the future.
        /// That is complete when admission timestamps are non-decreasing, because every entry uses the same
        /// <see cref="_ttl"/>. A clock that moves backward can leave a later, already-expired node in the list.
        /// </remarks>
        public long SweepExpired()
        {
            using var pooledReturns = CanonicalArticleBuffer.ReturnScope.Enter();
            lock (_gate)
            {
                if (_disposed)
                {
                    return 0;
                }

                var released = ExpireEligibleLocked(_time.GetUtcNow());
                if (released > 0)
                {
                    ArticleRetentionLogMessages.Swept(_logger, released, _retainedBytes);
                }

                return released;
            }
        }

        /// <summary>Stops new <see cref="RetainCanonical"/> admissions by setting <see cref="_admissionClosed"/>.</summary>
        /// <remarks>
        /// Idempotent. Does not throw when <see cref="_disposed"/> is already set. Does not cancel
        /// RequestIds, dispose entries, or reject <see cref="TryClaimForDelivery"/>.
        /// </remarks>
        public void BeginShutdown()
        {
            lock (_gate)
            {
                _admissionClosed = true;
            }
        }

        /// <summary>
        /// Marks the authority disposed, closes admission, and unindexes every retained entry.
        /// </summary>
        /// <returns>A completed task. The cleanup runs synchronously under <see cref="_gate"/>.</returns>
        /// <remarks>
        /// Idempotent. A second call does not throw. Every authority-owned ArtData reference is dropped
        /// and removed from the retained-byte total before this method returns.
        /// An in-flight delivery claim keeps its captured bytes and does not reindex the entry.
        /// After this method, locked <see cref="RetainCanonical"/>, <see cref="TryClaimForDelivery"/>, and
        /// <see cref="TryCancelPendingRequest"/> calls throw <see cref="ObjectDisposedException"/>,
        /// and <see cref="SweepExpired"/> returns zero.
        /// </remarks>
        public ValueTask DisposeAsync()
        {
            using var pooledReturns = CanonicalArticleBuffer.ReturnScope.Enter();
            lock (_gate)
            {
                if (_disposed)
                {
                    return ValueTask.CompletedTask;
                }

                _disposed = true;
                _admissionClosed = true;
                foreach (var entry in _byMessageId.Values.ToArray())
                {
                    UnindexLocked(entry);
                    _ = DetachAuthorityPayloadLocked(entry);
                }

                _insertionOrder.Clear();
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Reclaims space for a new admission: TTL-expired entries first, then oldest FIFO
        /// entries that have no openable RequestIds. Entries with openable Success capabilities
        /// are skipped so those RequestIds stay attached until cancel, TTL, committed NNTP delivery, or dispose.
        /// </summary>
        /// <param name="requiredBytes">ArtData length of the entry about to be admitted.</param>
        /// <param name="now">Admission clock reading used for TTL expiry.</param>
        /// <returns>
        /// Bytes physically disposed by TTL expiry and by FIFO reclaim. An unpinned entry that still
        /// has a reader is unindexed but contributes nothing until that reader releases it.
        /// </returns>
        /// <remarks>
        /// Called under <see cref="_gate"/>. Stops when <see cref="_retainedBytes"/> can hold
        /// <paramref name="requiredBytes"/> or no unpinned FIFO candidate remains. An outstanding
        /// reader is not a pin. <see cref="BeginShutdown"/> is not a reclaim path.
        /// </remarks>
        private long ReclaimForAdmissionLocked(int requiredBytes, DateTimeOffset now)
        {
            var released = ExpireEligibleLocked(now);
            while (_retainedBytes + requiredBytes > _maximumBytes)
            {
                var candidate = FindOldestFifoReclaimableLocked();
                if (candidate is null)
                {
                    break;
                }

                UnindexLocked(candidate);
                if (DisposePhysicallyLocked(candidate))
                {
                    released += candidate.PayloadBytes;
                }
            }

            return released;
        }

        /// <summary>
        /// Oldest insertion-order entry with zero openable RequestIds and no NNTP delivery claim.
        /// Entries that still carry Success capabilities, or an active delivery claim, are not FIFO-reclaimable.
        /// </summary>
        /// <returns>
        /// The oldest unpinned entry, or <see langword="null"/> when every indexed entry is pinned.
        /// Does not remove the entry. An active reader alone does not keep it pinned.
        /// </returns>
        private RetainedEntry? FindOldestFifoReclaimableLocked()
        {
            for (var node = _insertionOrder.First; node is not null; node = node.Next)
            {
                if (node.Value.OpenableRequestIdCount == 0 && !node.Value.IsDeliveryClaimed)
                {
                    return node.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Unindexes every expired entry from the oldest insertion-order node until the first
        /// <see cref="RetainedEntry.ExpiresUtc"/> still in the future.
        /// </summary>
        /// <param name="now">Clock reading compared with <see cref="RetainedEntry.ExpiresUtc"/>.</param>
        /// <returns>
        /// Bytes the authority detached. An active transfer does not delay that subtraction.
        /// </returns>
        /// <remarks>
        /// The early stop is valid when <see cref="_time"/> does not move backward between admissions.
        /// <see cref="RetainedEntry.ExpiresUtc"/> is the admission timestamp plus the same <see cref="_ttl"/>,
        /// so a later node then expires later. A backward clock step can leave a later expired node unvisited.
        /// </remarks>
        private long ExpireEligibleLocked(DateTimeOffset now)
        {
            var released = 0L;
            var node = _insertionOrder.First;
            while (node is not null)
            {
                var entry = node.Value;
                var next = node.Next;
                if (entry.ExpiresUtc > now)
                {
                    break;
                }

                if (RemoveExpiredLocked(entry))
                {
                    released += entry.WasPhysicallyDisposedThisRemoval ? entry.PayloadBytes : 0;
                }

                node = next;
            }

            return released;
        }

        /// <summary>Unindexes <paramref name="entry"/> and attempts physical disposal.</summary>
        /// <param name="entry">Expired entry. May already be logically removed.</param>
        /// <returns>
        /// Always <see langword="true"/> after the attempt. Byte credit is
        /// <see cref="RetainedEntry.WasPhysicallyDisposedThisRemoval"/>, not this boolean.
        /// </returns>
        private bool RemoveExpiredLocked(RetainedEntry entry)
        {
            UnindexLocked(entry);
            var detached = DetachAuthorityPayloadLocked(entry);
            entry.WasPhysicallyDisposedThisRemoval = detached;
            return true;
        }

        /// <summary>Reports whether <paramref name="entry"/> is due at <paramref name="now"/>.</summary>
        /// <param name="entry">Indexed or previously indexed entry.</param>
        /// <param name="now">Clock reading. Equal to <see cref="RetainedEntry.ExpiresUtc"/> is expired.</param>
        /// <returns><see langword="true"/> when <see cref="RetainedEntry.ExpiresUtc"/> is less than or equal to <paramref name="now"/>.</returns>
        private static bool IsExpiredLocked(RetainedEntry entry, DateTimeOffset now) =>
            entry.ExpiresUtc <= now;

        /// <summary>
        /// Removes <paramref name="entry"/> from the Message-ID, ArticleId, RequestId, and FIFO indexes once.
        /// </summary>
        /// <param name="entry">Entry to drop from lookup. ArtData is left in place when readers remain.</param>
        /// <remarks>
        /// Called under <see cref="_gate"/>. A second call returns at
        /// <see cref="RetainedEntry.TryMarkLogicallyRemoved"/> and does not clear indexes again.
        /// </remarks>
        private void UnindexLocked(RetainedEntry entry)
        {
            if (!entry.TryMarkLogicallyRemoved())
            {
                return;
            }

            _byMessageId.Remove(entry.Identity.MessageId);
            _messageIdByArticleId.Remove(entry.Identity.ArticleIdHex);
            ClearAllRequestIdsLocked(entry);
            if (entry.Node is not null)
            {
                _insertionOrder.Remove(entry.Node);
                entry.Node = null;
            }
        }

        /// <summary>
        /// Adds <paramref name="requestId"/> as an openable capability for <paramref name="entry"/>.
        /// Idempotent when already attached. Returns <see langword="false"/> when the bound is full.
        /// </summary>
        /// <param name="entry">Live entry that will own the capability.</param>
        /// <param name="requestId">RequestId to attach. Not removed from any other entry by this method.</param>
        /// <returns>
        /// <see langword="false"/> when <paramref name="entry"/> is already at
        /// <see cref="_maxOpenableRequestIdsPerArticle"/> and does not already contain
        /// <paramref name="requestId"/>. Otherwise updates <see cref="_byRequestId"/> and returns
        /// <see langword="true"/>.
        /// </returns>
        private bool AttachRequestIdLocked(RetainedEntry entry, Guid requestId)
        {
            if (!entry.TryAddOpenableRequestId(requestId, _maxOpenableRequestIdsPerArticle))
            {
                return false;
            }

            _byRequestId[requestId] = entry;
            return true;
        }

        /// <summary>Removes one openable RequestId from the entry and reverse map.</summary>
        /// <param name="entry">Entry expected to own <paramref name="requestId"/>.</param>
        /// <param name="requestId">Capability to detach.</param>
        /// <returns>
        /// <see langword="false"/> when <paramref name="entry"/> does not contain <paramref name="requestId"/>.
        /// Otherwise returns <see langword="true"/> after removing it from the entry, and also removes it from
        /// <see cref="_byRequestId"/> when that map still points at <paramref name="entry"/>.
        /// </returns>
        private bool DetachRequestIdLocked(RetainedEntry entry, Guid requestId)
        {
            if (!entry.RemoveOpenableRequestId(requestId))
            {
                return false;
            }

            if (_byRequestId.TryGetValue(requestId, out var owner) && ReferenceEquals(owner, entry))
            {
                _byRequestId.Remove(requestId);
            }

            return true;
        }

        /// <summary>Removes every openable RequestId for an entry (TTL / unindex / dispose).</summary>
        /// <param name="entry">Entry whose capability set is snapshotted and cleared.</param>
        /// <remarks>
        /// Each cleared id is removed from <see cref="_byRequestId"/> only when the map still points at
        /// <paramref name="entry"/>.
        /// </remarks>
        private void ClearAllRequestIdsLocked(RetainedEntry entry)
        {
            foreach (var requestId in entry.TakeAllOpenableRequestIds().Where(candidate =>
                         _byRequestId.TryGetValue(candidate, out var owner) && ReferenceEquals(owner, entry)))
            {
                _byRequestId.Remove(requestId);
            }
        }

        /// <summary>
        /// Accepts ArtData only when it is a prefix of one backing array, starting at offset zero,
        /// with length equal to <see cref="ArticleRecord.ArtSize"/> and greater than zero.
        /// A rented array may be longer than that prefix. A non-zero offset is rejected.
        /// </summary>
        /// <param name="record">Candidate record. Not copied.</param>
        /// <param name="payload">The backing array when the method returns <see langword="true"/>; otherwise <see langword="null"/>.</param>
        /// <returns>
        /// <see langword="true"/> when retention can take that array by reference.
        /// A non-zero offset, a length other than <see cref="ArticleRecord.ArtSize"/>, or an empty buffer returns <see langword="false"/>.
        /// </returns>
        private static bool TryGetOwnedArtData(in ArticleRecord record, out byte[] payload)
        {
            if (MemoryMarshal.TryGetArray(record.ArtData, out ArraySegment<byte> segment)
                && segment.Array is not null
                && segment.Offset == 0
                && segment.Count == record.ArtSize
                && segment.Count > 0)
            {
                payload = segment.Array;
                return true;
            }

            payload = null!;
            return false;
        }

        /// <summary>
        /// Drops <paramref name="entry"/>'s ArtData reference when no reader remains, and subtracts its bytes.
        /// </summary>
        /// <param name="entry">Logically removed entry, or an entry being disposed with no readers.</param>
        /// <returns>
        /// <see langword="false"/> when a reader is outstanding or the entry was already physically disposed.
        /// <see langword="true"/> after <see cref="_retainedBytes"/> and <see cref="_physicalCount"/> are reduced.
        /// </returns>
        private bool DisposePhysicallyLocked(RetainedEntry entry)
        {
            if (entry.ReaderCount > 0)
            {
                return false;
            }

            return DetachAuthorityPayloadLocked(entry);
        }

        /// <summary>
        /// Drops the authority's ArtData reference and subtracts its bytes once, even when a transfer reader remains.
        /// </summary>
        /// <param name="entry">Entry whose authority-owned record should be released.</param>
        /// <returns>
        /// <see langword="false"/> when this entry was already detached.
        /// <see langword="true"/> after <see cref="_retainedBytes"/> and <see cref="_physicalCount"/> are reduced.
        /// </returns>
        private bool DetachAuthorityPayloadLocked(RetainedEntry entry)
        {
            if (!entry.TryDetachAuthorityPayload())
            {
                return false;
            }

            SubtractBytesLocked(entry.PayloadBytes);
            _physicalCount = Math.Max(0, _physicalCount - 1);
            return true;
        }

        /// <summary>
        /// Returns whether <paramref name="exception"/> is a process-fatal runtime failure that must leave this method.
        /// </summary>
        /// <param name="exception">Exception observed by a retention caller.</param>
        /// <returns>
        /// <see langword="true"/> for <see cref="OutOfMemoryException"/> (including <see cref="InsufficientMemoryException"/>),
        /// <see cref="StackOverflowException"/>, <see cref="AccessViolationException"/>,
        /// <see cref="AppDomainUnloadedException"/>, and <see cref="SEHException"/>.
        /// </returns>
        /// <remarks>
        /// These types exist on <c>net10.0</c>. <c>ThreadAbortException</c> does not.
        /// <see cref="StackOverflowException"/> is rarely catchable; when the runtime does deliver it, it is not logged and dropped.
        /// <see cref="ArticleRetentionSweepService"/> uses this predicate so a fatal failure from
        /// <see cref="SweepExpired"/> is not logged and dropped by the hosted sweep.
        /// </remarks>
        internal static bool IsProcessFatal(Exception exception) =>
            exception is OutOfMemoryException
                or StackOverflowException
                or AccessViolationException
                or AppDomainUnloadedException
                or SEHException;

        /// <summary>Test stage that throws after the entry is linked and before any index insert.</summary>
        internal const string PublicationFaultAfterLink = "after-link";

        /// <summary>Test stage that throws after the Message-ID index insert.</summary>
        internal const string PublicationFaultAfterMessageId = "after-message-id";

        /// <summary>Test stage that throws after the ArticleId index insert.</summary>
        internal const string PublicationFaultAfterArticleId = "after-article-id";

        /// <summary>Test stage that throws after retained-byte accounting.</summary>
        internal const string PublicationFaultAfterAccounting = "after-accounting";

        /// <summary>Test stage that throws after the retention buffer lease is stored on the entry.</summary>
        internal const string PublicationFaultAfterLease = "after-lease";

        /// <summary>
        /// Test hook for <see cref="RetainCanonical"/> publication. Null unless a test sets one stage name.
        /// Async-local so the handler's await still observes the stage, and one test does not inject it into another.
        /// </summary>
        private static readonly AsyncLocal<string?> PublicationFault = new();

        /// <summary>Gets or sets the publication stage that should throw. Tests clear it in a finally.</summary>
        internal static string? PublicationFaultStage
        {
            get => PublicationFault.Value;
            set => PublicationFault.Value = value;
        }

        /// <summary>Throws when <see cref="PublicationFaultStage"/> equals <paramref name="stage"/>.</summary>
        /// <param name="stage">Stage the insert has just completed.</param>
        private static void ThrowPublicationFault(string stage)
        {
            if (PublicationFault.Value == stage)
            {
                throw new InvalidOperationException("Injected publication fault: " + stage);
            }
        }

        /// <summary>
        /// Removes a partial insert and drops a lease that was acquired for it.
        /// Called under <see cref="_gate"/> before that lock is released.
        /// </summary>
        /// <param name="entry">Entry that may be linked, indexed, or holding a buffer lease.</param>
        /// <param name="messageId">Message-ID key passed to the failed insert.</param>
        /// <param name="articleIdHex">ArticleId hex key passed to the failed insert.</param>
        /// <param name="requestId">RequestId passed to the failed insert.</param>
        /// <param name="messageIndexed">Whether <paramref name="messageId"/> was inserted.</param>
        /// <param name="articleIndexed">Whether <paramref name="articleIdHex"/> was inserted.</param>
        /// <param name="requestAttached">Whether <paramref name="requestId"/> was attached.</param>
        /// <param name="accounted">Whether retained bytes and the physical count include this entry.</param>
        /// <param name="unstoredLease">Lease acquired but not yet stored on <paramref name="entry"/>.</param>
        private void RollbackPublication(
            RetainedEntry entry,
            string messageId,
            string articleIdHex,
            Guid requestId,
            bool messageIndexed,
            bool articleIndexed,
            bool requestAttached,
            bool accounted,
            CanonicalBufferLease? unstoredLease)
        {
            if (accounted)
            {
                SubtractBytesLocked(entry.PayloadBytes);
                _physicalCount = Math.Max(0, _physicalCount - 1);
            }

            entry.ReleaseBufferLease();
            unstoredLease?.Dispose();
            if (requestAttached)
            {
                _ = DetachRequestIdLocked(entry, requestId);
            }

            if (articleIndexed)
            {
                _messageIdByArticleId.Remove(articleIdHex);
            }

            if (messageIndexed)
            {
                _byMessageId.Remove(messageId);
            }

            if (entry.Node is not null)
            {
                _insertionOrder.Remove(entry.Node);
                entry.Node = null;
            }
        }

        /// <summary>Adds <paramref name="bytes"/> to <see cref="_retainedBytes"/>. Called under <see cref="_gate"/>.</summary>
        /// <param name="bytes">ArtData length of an entry just indexed. Not checked against <see cref="_maximumBytes"/> here.</param>
        private void AddBytesLocked(int bytes)
        {
            _retainedBytes += bytes;
        }

        /// <summary>
        /// Subtracts <paramref name="bytes"/> from <see cref="_retainedBytes"/>, floored at zero.
        /// Called under <see cref="_gate"/>.
        /// </summary>
        /// <param name="bytes"><see cref="RetainedEntry.PayloadBytes"/> of an entry just physically disposed.</param>
        private void SubtractBytesLocked(int bytes)
        {
            _retainedBytes = Math.Max(0, _retainedBytes - bytes);
        }

        /// <summary>
        /// Logs a locked-section rejection and returns a result with no listener endpoint.
        /// Does not change indexes or the incoming ArtData ownership.
        /// </summary>
        /// <param name="kind">Rejection classification.</param>
        /// <param name="identity">Identity computed for this attempt. Included even though admission failed.</param>
        /// <param name="payloadBytes">ArtData length that was refused.</param>
        /// <param name="released">Bytes physically released by reclaim or expiry earlier in this attempt.</param>
        /// <returns>
        /// A result whose endpoint fields are <see langword="null"/> and whose retained total is the
        /// current <see cref="_retainedBytes"/>.
        /// </returns>
        private ArticleRetentionResult RejectLocked(
            ArticleRetentionKind kind,
            ArticleIdentity identity,
            int payloadBytes,
            long released)
        {
            ArticleRetentionLogMessages.Rejected(_logger, kind, identity.ArticleIdHex, payloadBytes, _retainedBytes);
            return new ArticleRetentionResult(kind, identity, null, null, _retainedBytes, released);
        }

        /// <summary>
        /// Holds a delivery claim until <see cref="Transfer"/> or disposal.
        /// </summary>
        /// <remarks>
        /// <see cref="TryClaimForDelivery"/> owns this value with <c>using</c>. Disposal releases the claim.
        /// <see cref="Transfer"/> builds the caller's result first, then clears the holder, so a failure while
        /// building that result still disposes the claim and a successful return does not.
        /// </remarks>
        private ref struct UnpublishedDeliveryClaim
        {
            /// <summary>Claim reserved under <see cref="_gate"/>, or <see langword="null"/> after transfer or disposal.</summary>
            private ArticleDeliveryClaim? _claim;

            /// <summary>Reserved claim. Valid after <see cref="Adopt"/> and before <see cref="Transfer"/> or disposal.</summary>
            public ArticleDeliveryClaim Claim => _claim ?? throw new InvalidOperationException("Delivery claim is not reserved.");

            /// <summary>Takes ownership of <paramref name="claim"/> until transfer or disposal.</summary>
            /// <param name="claim">Claim already constructed for a reserved entry.</param>
            public void Adopt(ArticleDeliveryClaim claim) => _claim = claim;

            /// <summary>
            /// Returns a claimed result and drops this holder's ownership.
            /// </summary>
            /// <returns>The caller's claim. This holder will not dispose it.</returns>
            public ArticleDeliveryClaimResult Transfer()
            {
                var claim = Claim;
                var result = ArticleDeliveryClaimResult.Claimed(claim);
                _claim = null;
                return result;
            }

            /// <summary>Rolls back the reserved claim when <see cref="Transfer"/> has not taken it.</summary>
            public void Dispose()
            {
                var claim = _claim;
                _claim = null;
                claim?.Dispose();
            }
        }

        /// <summary>
        /// Exclusive NNTP delivery hold. Disposing before <see cref="Commit"/> rolls the claim back.
        /// </summary>
        /// <remarks>
        /// <see cref="ArticleBytes"/> is the authority's existing buffer, not a copy. The caller acquires one
        /// buffer lease before publishing the claim, and settlement disposes that lease. A rented array stays
        /// out of its pool until this claim settles even if TTL or <see cref="DisposeAsync"/> already dropped
        /// the authority's lease. The owner's last read of <see cref="ArticleBytes"/> happens before commit or
        /// dispose on that same thread; the reference count does not wait for a different thread that is still
        /// reading. Commit and dispose are idempotent and safe to call together: only the first settles the
        /// entry, and only the first disposes the lease.
        /// </remarks>
        internal sealed class ArticleDeliveryClaim : IDisposable
        {
            /// <summary>Authority that owns the indexes. Settlement calls back into it under its gate.</summary>
            private readonly ArticleRetentionAuthority _authority;

            /// <summary>Record whose buffer consumer this claim releases on settlement.</summary>
            private readonly ArticleRecord _record;

            /// <summary>0 until the first commit or rollback wins.</summary>
            private int _settled;

            /// <summary>0 until the first commit or dispose releases the buffer consumer.</summary>
            private int _bufferReleased;

            /// <summary>Lease acquired for this claim. Disposed once when the claim settles.</summary>
            private readonly CanonicalBufferLease _lease;

            /// <summary>Binds a claimed entry's bytes to the authority that settles the claim.</summary>
            /// <param name="authority">Authority that will commit or roll back this claim.</param>
            /// <param name="record">Retained record. Its bytes are snapshotted while <paramref name="lease"/> is held.</param>
            /// <param name="messageId">Stored Message-ID key. Not normalized.</param>
            /// <param name="lease">Consumer acquired before this claim is published. This claim disposes it.</param>
            internal ArticleDeliveryClaim(
                ArticleRetentionAuthority authority,
                ArticleRecord record,
                string messageId,
                CanonicalBufferLease lease)
            {
                _authority = authority;
                _record = record;
                _lease = lease;
                ArticleBytes = record.ArtData;
                MessageId = messageId;
            }

            /// <summary>
            /// Existing retained article bytes. Read before <see cref="Commit"/> or <see cref="Dispose"/>.
            /// A rented buffer may be returned to its pool when settlement drops the last consumer.
            /// </summary>
            internal ReadOnlyMemory<byte> ArticleBytes { get; }

            /// <summary>Exact stored Message-ID. Not rewritten from ArtData.</summary>
            internal string MessageId { get; }

            /// <summary>
            /// Removes the entry from every index and subtracts its bytes once when it is still indexed and unexpired.
            /// </summary>
            /// <remarks>
            /// A later <see cref="Dispose"/> does not roll the removal back. An entry already removed by TTL or
            /// <see cref="DisposeAsync"/> is not reindexed. An entry that expired before this call is removed
            /// by the expiry path instead of being consumed as a live article.
            /// Settlement then releases this claim's buffer consumer.
            /// </remarks>
            internal void Commit()
            {
                try
                {
                    _authority.CompleteDelivery(this, commit: true);
                }
                finally
                {
                    ReleaseBuffer();
                }
            }

            /// <summary>Rolls the claim back unless <see cref="Commit"/> already settled it.</summary>
            /// <remarks>
            /// A second call does not settle the entry again and does not release the buffer consumer again.
            /// An indexed, unexpired entry becomes claimable again.
            /// </remarks>
            public void Dispose()
            {
                try
                {
                    _authority.CompleteDelivery(this, commit: false);
                }
                finally
                {
                    ReleaseBuffer();
                }
            }

            /// <summary>Wins settlement once.</summary>
            /// <returns><see langword="false"/> when commit or rollback already ran.</returns>
            internal bool TryMarkSettled() => Interlocked.CompareExchange(ref _settled, 1, 0) == 0;

            /// <summary>
            /// Releases this claim's buffer lease once, after <see cref="CompleteDelivery"/> returns.
            /// The caller's last read of <see cref="ArticleBytes"/> must already have finished on this thread.
            /// Another thread must not read <see cref="ArticleBytes"/> concurrently with this release.
            /// </summary>
            private void ReleaseBuffer()
            {
                if (Interlocked.Exchange(ref _bufferReleased, 1) == 0)
                {
                    _lease.Dispose();
                }
            }
        }

        /// <summary>
        /// One retained Message-ID. ArtData stays reachable until physical disposal, which waits for
        /// <see cref="_readers"/> to reach zero. Callers mutate this type only while holding
        /// <see cref="_gate"/>; it does not take its own lock.
        /// </summary>
        private sealed class RetainedEntry
        {
            /// <summary>
            /// Canonical record whose ArtData is the authority-owned buffer. <see langword="null"/> after
            /// <see cref="TryDetachAuthorityPayload"/> succeeds. A delivery claim may still hold its own reference.
            /// </summary>
            private ArticleRecord? _record;

            /// <summary>
            /// Date-family header name captured at admission. Cleared with <see cref="_record"/> on physical disposal.
            /// </summary>
            private NntpArticleHeaderName? _selectedDateHeaderName;

            /// <summary>
            /// RequestIds that still pin the entry against FIFO reclaim.
            /// Membership is idempotent. Order is not significant.
            /// </summary>
            private readonly HashSet<Guid> _openableRequestIds = [];

            /// <summary>
            /// Delivery readers. FIFO physical release waits while this
            /// is positive. TTL expiry does not. Changed only under <see cref="_gate"/>.
            /// </summary>
            private int _readers;

            /// <summary>
            /// 1 while an NNTP <see cref="ArticleDeliveryClaim"/> holds this entry.
            /// Pins FIFO reclaim. TTL does not treat it as an extension of <see cref="ExpiresUtc"/>.
            /// </summary>
            private int _deliveryClaim;

            /// <summary>
            /// 0 until the first successful <see cref="TryMarkLogicallyRemoved"/>; 1 afterward.
            /// Logical removal drops indexes and does not by itself drop ArtData.
            /// </summary>
            private int _logicallyRemoved;

            /// <summary>
            /// 0 until <see cref="TryDetachAuthorityPayload"/> clears the record; 1 afterward.
            /// A second physical disposal fails.
            /// </summary>
            private int _physicallyDisposed;

            /// <summary>Stamps a new indexed entry. Does not attach a RequestId.</summary>
            /// <param name="identity">Message-ID and ArticleId hex used as index keys.</param>
            /// <param name="fqdn">Listener host copied from the authority at admission.</param>
            /// <param name="listenerPort">TLS listener port copied from the authority at admission.</param>
            /// <param name="record">CanonicalV1 record retained by reference.</param>
            /// <param name="selectedDateHeaderName">Date-family header name captured at admission.</param>
            /// <param name="expiresUtc">First instant at which the entry is expired.</param>
            internal RetainedEntry(
                ArticleIdentity identity,
                string fqdn,
                int listenerPort,
                ArticleRecord record,
                NntpArticleHeaderName selectedDateHeaderName,
                DateTimeOffset expiresUtc)
            {
                Identity = identity;
                Fqdn = fqdn;
                ListenerPort = listenerPort;
                _record = record;
                _selectedDateHeaderName = selectedDateHeaderName;
                PayloadBytes = record.ArtSize;
                ExpiresUtc = expiresUtc;
            }

            /// <summary>Message-ID and ArticleId hex. These are the index keys for the entry's lifetime.</summary>
            internal ArticleIdentity Identity { get; }

            /// <summary>Listener host fixed at admission. Not updated if the authority endpoint later changes.</summary>
            internal string Fqdn { get; }

            /// <summary>TLS listener port fixed at admission.</summary>
            internal int ListenerPort { get; }

            /// <summary>
            /// <see cref="ArticleRecord.ArtSize"/> captured at admission. Remains after
            /// <see cref="_record"/> is cleared so byte accounting can subtract the same length.
            /// </summary>
            internal int PayloadBytes { get; }

            /// <summary>
            /// First instant at which the entry is expired. <see cref="IsExpiredLocked"/> treats equality as expired.
            /// </summary>
            internal DateTimeOffset ExpiresUtc { get; }

            /// <summary>
            /// Link in <see cref="_insertionOrder"/>. <see langword="null"/> after <see cref="UnindexLocked"/> unlinks it.
            /// </summary>
            internal LinkedListNode<RetainedEntry>? Node { get; set; }

            /// <summary>Openable RequestIds still attached. Greater than zero pins the entry against FIFO reclaim.</summary>
            internal int OpenableRequestIdCount => _openableRequestIds.Count;

            /// <summary>Delivery readers. FIFO detach waits for zero. TTL does not.</summary>
            internal int ReaderCount => _readers;

            /// <summary><see langword="true"/> while an NNTP delivery claim is active.</summary>
            internal bool IsDeliveryClaimed => _deliveryClaim == 1;

            /// <summary>Marks this entry claimed. Fails when a claim is already active.</summary>
            /// <returns><see langword="false"/> when <see cref="_deliveryClaim"/> is already set. Does not change readers.</returns>
            internal bool TryBeginDeliveryClaim()
            {
                if (_deliveryClaim != 0)
                {
                    return false;
                }

                _deliveryClaim = 1;
                return true;
            }

            /// <summary>Clears the NNTP delivery claim. A second call is a no-op.</summary>
            internal void EndDeliveryClaim() => _deliveryClaim = 0;

            /// <summary>Consumer held until physical disposal. Null before publication and after detach.</summary>
            private CanonicalBufferLease? _bufferLease;

            /// <summary>Retained record, or <see langword="null"/> after physical disposal.</summary>
            internal ArticleRecord? Record => _record;

            /// <summary>Stores the retention consumer. Called once, under <see cref="_gate"/>, after indexes match accounting.</summary>
            /// <param name="lease">Lease acquired for this entry. Not disposed by the caller after this returns.</param>
            internal void HoldBuffer(CanonicalBufferLease lease) => _bufferLease = lease;

            /// <summary>Releases the retention consumer once. A second call does nothing.</summary>
            internal void ReleaseBufferLease()
            {
                var lease = _bufferLease;
                _bufferLease = null;
                lease?.Dispose();
            }

            /// <summary>Volatile read of logical removal. Indexes no longer contain the entry when this is <see langword="true"/>.</summary>
            internal bool IsLogicallyRemoved => Volatile.Read(ref _logicallyRemoved) == 1;

            /// <summary>
            /// Set by <see cref="RemoveExpiredLocked"/> to whether that call physically disposed the entry.
            /// Sweep byte credit uses this flag because <see cref="RemoveExpiredLocked"/> itself always returns <see langword="true"/>.
            /// </summary>
            internal bool WasPhysicallyDisposedThisRemoval { get; set; }

            /// <summary>
            /// Adds <paramref name="requestId"/> unless the set is already at <paramref name="maximum"/>.
            /// </summary>
            /// <param name="requestId">Capability to add. An id already in the set succeeds without growing it.</param>
            /// <param name="maximum">Inclusive cap. Compared with the count before an insert.</param>
            /// <returns>
            /// <see langword="true"/> when the id is present after the call, including when it was already present
            /// at the cap. <see langword="false"/> when the cap rejected a new id. Existing ids are not removed.
            /// </returns>
            internal bool TryAddOpenableRequestId(Guid requestId, int maximum)
            {
                if (_openableRequestIds.Contains(requestId))
                {
                    return true;
                }

                if (_openableRequestIds.Count >= maximum)
                {
                    return false;
                }

                return _openableRequestIds.Add(requestId);
            }

            /// <summary>Removes one openable RequestId from this entry only.</summary>
            /// <param name="requestId">Capability to remove.</param>
            /// <returns><see langword="false"/> when <paramref name="requestId"/> was not in the set.</returns>
            internal bool RemoveOpenableRequestId(Guid requestId) => _openableRequestIds.Remove(requestId);

            /// <summary>Copies every openable RequestId and clears the set.</summary>
            /// <returns>The ids that were openable. Empty when the set was empty. Order is undefined.</returns>
            internal Guid[] TakeAllOpenableRequestIds()
            {
                if (_openableRequestIds.Count == 0)
                {
                    return [];
                }

                var ids = new Guid[_openableRequestIds.Count];
                _openableRequestIds.CopyTo(ids);
                _openableRequestIds.Clear();
                return ids;
            }

            /// <summary>
            /// Takes one delivery reader when the record is still physically present and the entry is not logically removed.
            /// </summary>
            /// <returns>
            /// <see langword="true"/> after <see cref="_readers"/> is incremented.
            /// <see langword="false"/> when the entry is logically removed, physically disposed, or has no record.
            /// Does not consume a RequestId.
            /// </returns>
            internal bool TryAcquire()
            {
                if (_logicallyRemoved == 1 || _physicallyDisposed == 1 || _record is null)
                {
                    return false;
                }

                _readers++;
                return true;
            }

            /// <summary>Drops one delivery reader.</summary>
            /// <returns><see langword="false"/> when <see cref="_readers"/> is already zero. Does not dispose ArtData.</returns>
            internal bool TryRelease()
            {
                if (_readers <= 0)
                {
                    return false;
                }

                _readers--;
                return true;
            }

            /// <summary>Marks the entry logically removed once.</summary>
            /// <returns><see langword="false"/> when it was already logically removed. Does not drop ArtData or readers.</returns>
            internal bool TryMarkLogicallyRemoved() =>
                Interlocked.CompareExchange(ref _logicallyRemoved, 1, 0) == 0;

            /// <summary>
            /// Clears <see cref="_record"/> and <see cref="_selectedDateHeaderName"/> once.
            /// </summary>
            /// <returns>
            /// <see langword="false"/> when this entry was already detached.
            /// Does not consult <see cref="_readers"/> and does not subtract <see cref="PayloadBytes"/>.
            /// </returns>
            internal bool TryDetachAuthorityPayload()
            {
                if (Interlocked.CompareExchange(ref _physicallyDisposed, 1, 0) != 0)
                {
                    return false;
                }

                ReleaseBufferLease();
                _record = null;
                _selectedDateHeaderName = null;
                return true;
            }
        }
    }
}
