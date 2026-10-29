namespace VectorNNTPD.TransitService.NntpDb
{
    /// <summary>Outcome of a durable <c>account_byte_limit</c> consume or remaining-quota read.</summary>
    public enum AccountByteConsumeStatus
    {
        /// <summary>The account row exists and the remaining value is valid.</summary>
        Consumed,

        /// <summary>No <c>nntpusers</c> row matched the plaintext account name.</summary>
        AccountNotFound,
    }

    /// <summary>Durable byte-quota result. <see cref="Remaining"/> is never negative.</summary>
    public readonly struct AccountByteConsumeResult
    {
        /// <summary>Initializes a clamped durable quota result.</summary>
        public AccountByteConsumeResult(AccountByteConsumeStatus status, long remaining, long consumed)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(remaining);
            ArgumentOutOfRangeException.ThrowIfNegative(consumed);
            Status = status;
            Remaining = remaining;
            Consumed = consumed;
        }

        /// <summary>Gets whether the row was found or missing.</summary>
        public AccountByteConsumeStatus Status { get; }

        /// <summary>Gets remaining bytes after the operation. Always <c>&gt;= 0</c>.</summary>
        public long Remaining { get; }

        /// <summary>Gets how many bytes were actually subtracted. Always <c>&gt;= 0</c>.</summary>
        public long Consumed { get; }

        /// <summary>Gets whether the row exists and remaining quota is known.</summary>
        public bool Succeeded => Status == AccountByteConsumeStatus.Consumed;
    }
}
