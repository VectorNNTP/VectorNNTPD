namespace VectorNNTPD.ReaderService.SessionState.BytesAccounting
{
    /// <summary>
    /// Process-local download-quota bookkeeping. Redis and MySQL are used by
    /// AUTHINFO observe and the periodic reconciler, never on the NNTP write path.
    /// </summary>
    public interface IAccountByteAccountant
    {
        /// <summary>
        /// Returns a sink that <see cref="IAccountByteSink.ObserveCopied"/>s into this
        /// account's pending total. No-op when <paramref name="accountName"/> is empty.
        /// </summary>
        IAccountByteSink CreateSink(string accountName);

        /// <summary>Gets whether this process has observed remaining <c>== 0</c> for the account.</summary>
        bool IsExhausted(string accountName);

        /// <summary>
        /// Reads cluster remaining (Redis if present, otherwise durable MySQL).
        /// Does not create or raise a Redis key. Returns <see langword="null"/> when
        /// remaining cannot be determined.
        /// </summary>
        /// <remarks>
        /// Live remaining is <c>min(Redis, MySQL)</c> when Redis exists. A stale-high Redis
        /// key is floored down to MySQL; observe never raises Redis and never uses the
        /// AUTHINFO user-record cache.
        /// </remarks>
        ValueTask<long?> ObserveRemainingAsync(string accountName, CancellationToken cancellationToken = default);

        /// <summary>Marks the account exhausted for all local sessions of this process.</summary>
        void MarkExhausted(string accountName);

        /// <summary>
        /// Clears the process-local exhausted flag. Used after an explicit top-up
        /// invalidation or when live remaining is observed greater than zero.
        /// </summary>
        void ClearExhausted(string accountName);

        /// <summary>
        /// Deletes cluster Redis remaining-quota state after an operator MySQL top-up.
        /// The next observe uses current durable remaining. Does not increase Redis.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> when the key was removed or already absent;
        /// <see langword="false"/> when Redis was unavailable.
        /// </returns>
        ValueTask<bool> DeleteAccountByteStateAsync(string accountName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Snapshots pending bytes and persists one account-level MySQL consume per
        /// account with work. Does not APPLY Redis. Used by
        /// <see cref="VectorNNTPD.ReaderService.SessionState.SessionStateService"/> before the combined Redis cycle.
        /// </summary>
        ValueTask CommitDurableAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// APPLY any MySQL-committed batches that are still awaiting Redis. Combined
        /// RENEW+APPLY that already called <see cref="CompleteApply"/> is skipped.
        /// </summary>
        ValueTask ApplyCommittedAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Snapshots pending, consumes MySQL, and APPLYs leftover batches. Used by
        /// tests and <see cref="VectorNNTPD.ReaderService.SessionState.SessionStateService"/> shutdown.
        /// </summary>
        ValueTask ReconcileAsync(CancellationToken cancellationToken = default);

        /// <summary>Returns the MySQL-committed batch awaiting Redis APPLY, if any.</summary>
        bool TryGetCommittedBatch(string accountName, out AccountByteCommittedBatch batch);

        /// <summary>
        /// Clears the in-process committed batch after a successful Redis APPLY
        /// (combined EVAL or APPLY-only).
        /// </summary>
        void CompleteApply(string accountName, long remaining);
    }

    /// <summary>One account-wide MySQL-committed consume awaiting Redis APPLY.</summary>
    public readonly struct AccountByteCommittedBatch
    {
        /// <summary>Initializes a committed batch snapshot.</summary>
        public AccountByteCommittedBatch(string accountName, string batchId, long consumed, long mysqlRemaining)
        {
            AccountName = accountName;
            BatchId = batchId;
            Consumed = consumed;
            MysqlRemaining = mysqlRemaining;
        }

        /// <summary>Gets the AUTHINFO account name.</summary>
        public string AccountName { get; }

        /// <summary>Gets the in-process batch identity.</summary>
        public string BatchId { get; }

        /// <summary>Gets bytes MySQL already subtracted for this batch.</summary>
        public long Consumed { get; }

        /// <summary>Gets durable remaining after this consume.</summary>
        public long MysqlRemaining { get; }
    }

    /// <summary>No-op accountant for tests and unauthenticated identities.</summary>
    public sealed class NullAccountByteAccountant : IAccountByteAccountant
    {
        /// <summary>Shared no-op instance.</summary>
        public static NullAccountByteAccountant Instance { get; } = new();

        private NullAccountByteAccountant()
        {
        }

        /// <inheritdoc />
        public IAccountByteSink CreateSink(string accountName) => NullAccountByteSink.Instance;

        /// <inheritdoc />
        public bool IsExhausted(string accountName) => false;

        /// <inheritdoc />
        public ValueTask<long?> ObserveRemainingAsync(string accountName, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<long?>(null);

        /// <inheritdoc />
        public void MarkExhausted(string accountName)
        {
        }

        /// <inheritdoc />
        public void ClearExhausted(string accountName)
        {
        }

        /// <inheritdoc />
        public ValueTask<bool> DeleteAccountByteStateAsync(
            string accountName,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        /// <inheritdoc />
        public ValueTask CommitDurableAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        /// <inheritdoc />
        public ValueTask ApplyCommittedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        /// <inheritdoc />
        public ValueTask ReconcileAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        /// <inheritdoc />
        public bool TryGetCommittedBatch(string accountName, out AccountByteCommittedBatch batch)
        {
            batch = default;
            return false;
        }

        /// <inheritdoc />
        public void CompleteApply(string accountName, long remaining)
        {
        }
    }

    /// <summary>Sink that ignores copied bytes.</summary>
    internal sealed class NullAccountByteSink : IAccountByteSink
    {
        public static NullAccountByteSink Instance { get; } = new();

        public void ObserveCopied(int bytes)
        {
        }
    }
}
