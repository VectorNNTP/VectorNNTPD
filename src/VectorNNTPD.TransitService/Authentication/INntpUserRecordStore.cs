namespace VectorNNTPD.TransitService.Authentication
{
    /// <summary>Looks up an NNTP account row by plaintext wire username.</summary>
    public interface INntpUserRecordStore
    {
        /// <summary>
        /// Returns the account snapshot, or <see langword="null"/> when no row exists.
        /// Disabled accounts are still returned; enablement is enforced by the validator.
        /// </summary>
        /// <exception cref="VectorNNTPD.Common.NntpDb.NntpDbUnavailableException">Backend failure. Must not be mapped to invalid credentials.</exception>
        ValueTask<NntpUserRecord?> TryGetUserAsync(string accountName, CancellationToken cancellationToken = default);
    }
}
