namespace VectorNNTPD.ReaderService.Moderation
{
    /// <summary>Loads enabled moderator rows from the NntpDB catalogue source.</summary>
    public interface INntpModeratorRepository
    {
        /// <summary>
        /// Returns enabled rows in <c>moderator_id</c> order. Callers must not query this
        /// from the POST hot path.
        /// </summary>
        ValueTask<IReadOnlyList<NntpModeratorRow>> GetEnabledAsync(CancellationToken cancellationToken = default);
    }
}
