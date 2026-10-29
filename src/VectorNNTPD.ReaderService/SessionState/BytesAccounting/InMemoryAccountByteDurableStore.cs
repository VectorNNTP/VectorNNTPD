using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.ReaderService.NntpDb;

namespace VectorNNTPD.ReaderService.SessionState.BytesAccounting
{
    /// <summary>In-memory durable remaining quota for tests. Shared across trackers as one MySQL.</summary>
    internal sealed class InMemoryAccountByteDurableStore : IAccountByteDurableStore
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, AccountRow> _accounts = new(StringComparer.Ordinal);

        /// <summary>When set, consume/query fail as if MySQL were down.</summary>
        public bool Unavailable { get; set; }

        /// <summary>Gets how many consume operations mutated or inspected a row.</summary>
        public int ConsumeCalls { get; private set; }

        /// <summary>Gets how many remaining-quota reads ran.</summary>
        public int QueryCalls { get; private set; }

        /// <summary>Adds or replaces remaining quota for an account.</summary>
        public void SeedByteAccount(string accountName, long remaining)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            ArgumentOutOfRangeException.ThrowIfNegative(remaining);
            lock (_gate)
            {
                _accounts[accountName] = new AccountRow(remaining);
            }
        }

        /// <summary>Returns stored remaining, or <see langword="null"/> when missing.</summary>
        public long? Remaining(string accountName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            lock (_gate)
            {
                return _accounts.TryGetValue(accountName, out var row) ? row.Remaining : null;
            }
        }

        /// <inheritdoc />
        public ValueTask<AccountByteConsumeResult> ConsumeAsync(
            string accountName,
            long bytes,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            cancellationToken.ThrowIfCancellationRequested();
            if (Unavailable)
            {
                throw new NntpDbUnavailableException("MySQL byte-quota consume unavailable.");
            }

            lock (_gate)
            {
                ConsumeCalls++;
                if (!_accounts.TryGetValue(accountName, out var row))
                {
                    return ValueTask.FromResult(
                        new AccountByteConsumeResult(AccountByteConsumeStatus.AccountNotFound, 0, 0));
                }

                var consumed = bytes > row.Remaining ? row.Remaining : bytes;
                row.Remaining -= consumed;
                return ValueTask.FromResult(
                    new AccountByteConsumeResult(AccountByteConsumeStatus.Consumed, row.Remaining, consumed));
            }
        }

        /// <inheritdoc />
        public ValueTask<AccountByteConsumeResult> QueryRemainingAsync(
            string accountName,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            cancellationToken.ThrowIfCancellationRequested();
            if (Unavailable)
            {
                throw new NntpDbUnavailableException("MySQL byte-quota query unavailable.");
            }

            lock (_gate)
            {
                QueryCalls++;
                if (!_accounts.TryGetValue(accountName, out var row))
                {
                    return ValueTask.FromResult(
                        new AccountByteConsumeResult(AccountByteConsumeStatus.AccountNotFound, 0, 0));
                }

                return ValueTask.FromResult(
                    new AccountByteConsumeResult(AccountByteConsumeStatus.Consumed, row.Remaining, 0));
            }
        }

        private sealed class AccountRow(long remaining)
        {
            public long Remaining { get; set; } = remaining;
        }
    }
}
