namespace VectorNNTPD.Common.Acme.Protocol
{
    /// <summary>
    /// ACME POST retry semantics. Separates safe reads and idempotent account creation from
    /// state-changing operations that must not be replayed after an ambiguous transmission outcome.
    /// </summary>
    internal enum AcmeRequestSemantics
    {
        /// <summary>
        /// POST-as-GET and other reads. Transient transport and HTTP 5xx failures may retry with a fresh JWS.
        /// </summary>
        SafeRead,

        /// <summary>
        /// <c>newAccount</c> for the same account key is idempotent (RFC 8555 §7.3). Transient failures may retry with a fresh JWS.
        /// </summary>
        IdempotentWrite,

        /// <summary>
        /// <c>newOrder</c>, finalize, and challenge POST. After an ambiguous outcome the JWS is not replayed;
        /// callers recover through known resource URLs or the account orders list.
        /// </summary>
        StateChanging,
    }
}
