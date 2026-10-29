namespace VectorNNTPD.TransitService.Newsgroups
{
    /// <summary>
    /// Process-wide newsgroup catalogue. Command handlers capture <see cref="Current"/> once.
    /// </summary>
    public interface INewsgroupCatalogue
    {
        /// <summary>
        /// Gets the last successfully published immutable snapshot.
        /// </summary>
        /// <remarks>
        /// After a successful start this is never null. Readers need no lock.
        /// Capture the reference once per command; a concurrent refresh may replace
        /// the published snapshot while that command continues on the captured instance.
        /// </remarks>
        NewsgroupSnapshot Current { get; }
    }
}
