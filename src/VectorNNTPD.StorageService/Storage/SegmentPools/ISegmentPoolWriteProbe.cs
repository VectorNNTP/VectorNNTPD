namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Writes, reads back, and deletes a temporary file inside a pool directory.
    /// </summary>
    internal interface ISegmentPoolWriteProbe
    {
        /// <summary>
        /// Probes <paramref name="canonicalDirectory"/>.
        /// </summary>
        /// <param name="canonicalDirectory">Canonical pool directory that already passed mount checks.</param>
        /// <param name="cancellationToken">Cancels the probe.</param>
        /// <returns><see langword="null"/> on success. Otherwise an actionable failure, including cleanup failure.</returns>
        string? Probe(string canonicalDirectory, CancellationToken cancellationToken);
    }
}
