namespace VectorNNTPD.ReaderService.History
{
    /// <summary>
    /// Shared HistoryDB used by CHECK (peek), IHAVE, and TAKETHIS (peek + remember).
    /// </summary>
    public interface IHistoryDb
    {
        /// <summary>
        /// Looks up <paramref name="messageId"/> wire octets. Memory hits do not touch Redis.
        /// A miss records the identifier locally and queues a Redis write.
        /// CHECK uses <see cref="PeekAsync"/> instead so a query does not make the identifier known.
        /// </summary>
        ValueTask<HistoryLookupResult> LookupAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Looks up <paramref name="messageId"/> without recording a miss.
        /// Used by CHECK, IHAVE admission, and TAKETHIS so a query or failed transfer
        /// does not make the identifier known. TAKETHIS starts this lookup as soon as
        /// the Message-ID is parsed so it overlaps article receive.
        /// </summary>
        ValueTask<HistoryLookupResult> PeekAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Records <paramref name="messageId"/> locally and queues a Redis write after a
        /// successful IHAVE or TAKETHIS accept. Does not perform a Redis EXISTS.
        /// </summary>
        void Remember(ReadOnlyMemory<byte> messageId);

        /// <summary>Returns whether the digest is currently present in the local store (tests).</summary>
        bool ContainsLocal(in HistoryDigest digest);
    }
}
