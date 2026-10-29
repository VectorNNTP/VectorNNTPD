namespace VectorNNTPD.TransitService.Moderation
{
    /// <summary>
    /// Process-wide moderator catalogue. Command handlers capture <see cref="Current"/> once.
    /// </summary>
    public interface IModeratorCatalogue
    {
        /// <summary>
        /// Gets the last successfully published immutable snapshot.
        /// </summary>
        /// <remarks>
        /// After a successful start this is never null. Readers need no lock.
        /// Capture the reference once per POST; a concurrent refresh may replace
        /// the published snapshot while that command continues on the captured instance.
        /// </remarks>
        ModeratorSnapshot Current { get; }
    }
}
