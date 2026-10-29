using System.Collections.Concurrent;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.TransitService.NntpDb;

namespace VectorNNTPD.TransitService.SessionState.BytesAccounting
{
    /// <summary>
    /// Process-local account byte accumulator and crash-safe batch reconciler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hot path: <see cref="IAccountByteSink.ObserveCopied"/> is an <see cref="Interlocked.Add(ref long, long)"/>.
    /// Pending and in-flight totals live only in this process. A crash loses unreconciled bytes
    /// (at-most-once durable charge). That is the accepted approximation; durable quota must
    /// never be charged twice for the same batch.
    /// </para>
    /// <para>
    /// Reconciliation is MySQL first, then Redis APPLY with a per-batch id. After a
    /// successful MySQL consume in this process, the same batch id is reused for every
    /// Redis retry. Redis treats that id as exactly-once. The id is not durable.
    /// Restart never replays a batch.
    /// </para>
    /// </remarks>
    internal sealed class AccountByteTracker : IAccountByteAccountant
    {
        private readonly ConcurrentDictionary<string, AccountLedger> _ledgers = new(StringComparer.Ordinal);
        private readonly IAccountByteDurableStore _durable;
        private readonly IAccountByteStore _cluster;
        private readonly ILogger<AccountByteTracker> _logger;
        private readonly SemaphoreSlim _reconcileGate = new(1, 1);

        /// <summary>Initializes a new instance of the <see cref="AccountByteTracker"/> class.</summary>
        public AccountByteTracker(
            IAccountByteDurableStore durable,
            IAccountByteStore cluster,
            ILogger<AccountByteTracker> logger)
        {
            ArgumentNullException.ThrowIfNull(durable);
            ArgumentNullException.ThrowIfNull(cluster);
            ArgumentNullException.ThrowIfNull(logger);
            _durable = durable;
            _cluster = cluster;
            _logger = logger;
        }

        /// <inheritdoc />
        public IAccountByteSink CreateSink(string accountName)
        {
            if (string.IsNullOrWhiteSpace(accountName))
            {
                return NullAccountByteSink.Instance;
            }

            return new AccountLedgerSink(GetOrAdd(accountName));
        }

        /// <inheritdoc />
        public bool IsExhausted(string accountName)
        {
            if (string.IsNullOrWhiteSpace(accountName))
            {
                return false;
            }

            return _ledgers.TryGetValue(accountName, out var ledger)
                && Volatile.Read(ref ledger.Exhausted) == 1;
        }

        /// <inheritdoc />
        public void MarkExhausted(string accountName)
        {
            if (string.IsNullOrWhiteSpace(accountName))
            {
                return;
            }

            var ledger = GetOrAdd(accountName);
            if (Interlocked.Exchange(ref ledger.Exhausted, 1) == 0)
            {
                AccountByteLogMessages.QuotaExhausted(_logger, accountName, remaining: 0);
            }
        }

        /// <inheritdoc />
        public void ClearExhausted(string accountName)
        {
            if (string.IsNullOrWhiteSpace(accountName))
            {
                return;
            }

            if (_ledgers.TryGetValue(accountName, out var ledger))
            {
                Volatile.Write(ref ledger.Exhausted, 0);
            }
        }

        /// <inheritdoc />
        public async ValueTask<bool> DeleteAccountByteStateAsync(
            string accountName,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            long? deleted;
            try
            {
                deleted = await _cluster.DeleteAsync(accountName, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                AccountByteLogMessages.ObserveFailed(_logger, ex, accountName);
                return false;
            }

            if (deleted is null)
            {
                AccountByteLogMessages.RedisApplyUnavailable(_logger, accountName, 0, 0);
                return false;
            }

            ClearExhausted(accountName);
            AccountByteLogMessages.ClusterStateDeleted(_logger, accountName, deleted.Value);
            return true;
        }

        /// <inheritdoc />
        public async ValueTask<long?> ObserveRemainingAsync(
            string accountName,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            long? redis = null;
            try
            {
                redis = await _cluster.ObserveAsync(accountName, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                AccountByteLogMessages.ObserveFailed(_logger, ex, accountName);
            }

            AccountByteConsumeResult? durable = null;
            try
            {
                durable = await _durable.QueryRemainingAsync(accountName, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (NntpDbUnavailableException ex)
            {
                AccountByteLogMessages.DurableQueryFailed(_logger, ex, accountName);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                AccountByteLogMessages.DurableQueryFailed(_logger, ex, accountName);
            }

            if (durable is { Succeeded: false })
            {
                return null;
            }

            var mysqlRemaining = durable is { Succeeded: true } row
                ? AccountByteEngine.ClampNonNegative(row.Remaining)
                : (long?)null;

            if (redis is { } live && live != AccountByteKeys.Missing)
            {
                var clusterRemaining = AccountByteEngine.ClampNonNegative(live);
                if (mysqlRemaining is { } durableRemaining)
                {
                    var effective = clusterRemaining < durableRemaining ? clusterRemaining : durableRemaining;
                    if (clusterRemaining > durableRemaining)
                    {
                        await FloorRedisToMysqlAsync(accountName, durableRemaining, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return effective;
                }

                return clusterRemaining;
            }

            return mysqlRemaining;
        }

        /// <inheritdoc />
        public async ValueTask CommitDurableAsync(CancellationToken cancellationToken = default)
        {
            await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var pair in _ledgers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await CommitDurableAccountAsync(pair.Key, pair.Value, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _reconcileGate.Release();
            }
        }

        /// <inheritdoc />
        public async ValueTask ApplyCommittedAsync(CancellationToken cancellationToken = default)
        {
            await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var pair in _ledgers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ApplyCommittedAccountAsync(pair.Key, pair.Value, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _reconcileGate.Release();
            }
        }

        /// <inheritdoc />
        public async ValueTask ReconcileAsync(CancellationToken cancellationToken = default)
        {
            await CommitDurableAsync(cancellationToken).ConfigureAwait(false);
            await ApplyCommittedAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public bool TryGetCommittedBatch(string accountName, out AccountByteCommittedBatch batch)
        {
            batch = default;
            if (string.IsNullOrWhiteSpace(accountName)
                || !_ledgers.TryGetValue(accountName, out var ledger)
                || Volatile.Read(ref ledger.MysqlCommitted) != 1)
            {
                return false;
            }

            var batchId = ledger.BatchId;
            if (!AccountByteBatchId.IsValid(batchId))
            {
                return false;
            }

            batch = new AccountByteCommittedBatch(
                accountName,
                batchId,
                ledger.LastConsumed,
                ledger.LastMysqlRemaining);
            return true;
        }

        /// <inheritdoc />
        public void CompleteApply(string accountName, long remaining)
        {
            if (string.IsNullOrWhiteSpace(accountName)
                || !_ledgers.TryGetValue(accountName, out var ledger)
                || Volatile.Read(ref ledger.MysqlCommitted) != 1)
            {
                return;
            }

            var consumed = ledger.LastConsumed;
            var mysqlRemaining = ledger.LastMysqlRemaining;
            Interlocked.Exchange(ref ledger.InFlight, 0);
            ledger.BatchId = null;
            Volatile.Write(ref ledger.MysqlCommitted, 0);
            AccountByteLogMessages.BatchReconciled(_logger, accountName, consumed, remaining);
            if (remaining == 0 || mysqlRemaining == 0)
            {
                MarkExhausted(accountName);
            }
        }

        /// <summary>Returns the process-local pending total (tests).</summary>
        internal long PendingBytes(string accountName) =>
            _ledgers.TryGetValue(accountName, out var ledger)
                ? Volatile.Read(ref ledger.Pending)
                : 0;

        /// <summary>Returns the process-local in-flight total (tests).</summary>
        internal long InFlightBytes(string accountName) =>
            _ledgers.TryGetValue(accountName, out var ledger)
                ? Volatile.Read(ref ledger.InFlight)
                : 0;

        /// <summary>Returns whether MySQL already consumed the current in-flight batch (tests).</summary>
        internal bool MysqlCommitted(string accountName) =>
            _ledgers.TryGetValue(accountName, out var ledger)
            && Volatile.Read(ref ledger.MysqlCommitted) == 1;

        /// <summary>Returns the in-process Redis batch id awaiting APPLY acknowledgment (tests).</summary>
        internal string? CurrentBatchId(string accountName) =>
            _ledgers.TryGetValue(accountName, out var ledger) ? ledger.BatchId : null;

        private AccountLedger GetOrAdd(string accountName) =>
            _ledgers.GetOrAdd(accountName, static _ => new AccountLedger());

        private async ValueTask FloorRedisToMysqlAsync(
            string accountName,
            long mysqlRemaining,
            CancellationToken cancellationToken)
        {
            try
            {
                _ = await _cluster
                    .ApplyAsync(
                        accountName,
                        AccountByteBatchId.Create(),
                        consumed: 0,
                        mysqlRemaining,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                AccountByteLogMessages.ObserveFailed(_logger, ex, accountName);
            }
        }

        private async ValueTask CommitDurableAccountAsync(
            string accountName,
            AccountLedger ledger,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref ledger.MysqlCommitted) == 1)
            {
                return;
            }

            var snapshot = Interlocked.Exchange(ref ledger.Pending, 0);
            if (snapshot > 0)
            {
                Interlocked.Add(ref ledger.InFlight, snapshot);
            }

            var inFlight = Volatile.Read(ref ledger.InFlight);
            if (inFlight == 0)
            {
                return;
            }

            AccountByteConsumeResult result;
            try
            {
                result = await _durable.ConsumeAsync(accountName, inFlight, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (NntpDbUnavailableException ex)
            {
                AccountByteLogMessages.DurableConsumeFailed(_logger, ex, accountName, inFlight);
                return;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                AccountByteLogMessages.DurableConsumeFailed(_logger, ex, accountName, inFlight);
                return;
            }

            if (!result.Succeeded)
            {
                Interlocked.Exchange(ref ledger.InFlight, 0);
                ledger.BatchId = null;
                return;
            }

            ledger.LastConsumed = result.Consumed;
            ledger.LastMysqlRemaining = result.Remaining;
            ledger.BatchId = AccountByteBatchId.Create();
            Volatile.Write(ref ledger.MysqlCommitted, 1);
            if (result.Remaining == 0)
            {
                MarkExhausted(accountName);
            }
        }

        private async ValueTask ApplyCommittedAccountAsync(
            string accountName,
            AccountLedger ledger,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref ledger.MysqlCommitted) != 1)
            {
                return;
            }

            var consumed = ledger.LastConsumed;
            var mysqlRemaining = ledger.LastMysqlRemaining;
            var batchId = ledger.BatchId;
            if (!AccountByteBatchId.IsValid(batchId))
            {
                batchId = AccountByteBatchId.Create();
                ledger.BatchId = batchId;
            }

            var applied = await _cluster
                .ApplyAsync(accountName, batchId, consumed, mysqlRemaining, cancellationToken)
                .ConfigureAwait(false);
            if (applied is null)
            {
                AccountByteLogMessages.RedisApplyUnavailable(_logger, accountName, consumed, mysqlRemaining);
                return;
            }

            CompleteApply(accountName, applied.Value);
        }

        private sealed class AccountLedger
        {
            public long Pending;
            public long InFlight;
            public int Exhausted;
            public int MysqlCommitted;
            public long LastConsumed;
            public long LastMysqlRemaining;
            public string? BatchId;
        }

        private sealed class AccountLedgerSink(AccountLedger ledger) : IAccountByteSink
        {
            public void ObserveCopied(int bytes)
            {
                if (bytes <= 0)
                {
                    return;
                }

                Interlocked.Add(ref ledger.Pending, bytes);
            }
        }
    }
}
