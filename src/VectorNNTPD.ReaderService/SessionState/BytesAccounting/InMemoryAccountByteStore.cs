namespace VectorNNTPD.ReaderService.SessionState.BytesAccounting
{
    /// <summary>Process-local cluster fake. Two trackers sharing one instance behave as two nodes.</summary>
    internal sealed class InMemoryAccountByteStore : IAccountByteStore
    {
        private readonly AccountByteEngine _engine = new();

        /// <summary>When set, apply/observe fail as if Redis were down.</summary>
        public bool Unavailable { get; set; }

        /// <summary>Gets how many apply operations reached the engine.</summary>
        public int ApplyCalls { get; private set; }

        /// <summary>Gets how many observe operations reached the engine.</summary>
        public int ObserveCalls { get; private set; }

        /// <summary>
        /// When greater than zero, the next apply runs the engine then returns
        /// <see langword="null"/> (Redis executed, response lost).
        /// </summary>
        public int HideNextApplyResults { get; set; }

        /// <summary>Gets the shared engine (tests).</summary>
        internal AccountByteEngine Engine => _engine;

        /// <inheritdoc />
        public ValueTask<long?> ApplyAsync(
            string accountName,
            string batchId,
            long consumed,
            long mysqlRemainingAfter,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
            cancellationToken.ThrowIfCancellationRequested();
            if (Unavailable)
            {
                return ValueTask.FromResult<long?>(null);
            }

            ApplyCalls++;
            var remaining = _engine.Apply(EncodingKey(accountName), batchId, consumed, mysqlRemainingAfter);
            if (HideNextApplyResults > 0)
            {
                HideNextApplyResults--;
                return ValueTask.FromResult<long?>(null);
            }

            return ValueTask.FromResult<long?>(remaining);
        }

        /// <inheritdoc />
        public ValueTask<long?> ObserveAsync(string accountName, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            cancellationToken.ThrowIfCancellationRequested();
            if (Unavailable)
            {
                return ValueTask.FromResult<long?>(null);
            }

            ObserveCalls++;
            return ValueTask.FromResult<long?>(_engine.Observe(EncodingKey(accountName)));
        }

        /// <inheritdoc />
        public ValueTask<long?> DeleteAsync(string accountName, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            cancellationToken.ThrowIfCancellationRequested();
            if (Unavailable)
            {
                return ValueTask.FromResult<long?>(null);
            }

            return ValueTask.FromResult<long?>(_engine.Delete(EncodingKey(accountName)));
        }

        internal static string EncodingKey(string accountName) =>
            System.Text.Encoding.UTF8.GetString(AccountByteKeys.Create(accountName));
    }
}
