namespace VectorNNTPD.TransitService.Session.Framing
{
    /// <summary>Outcome of <see cref="IHaveArticleReader.ReadAsync"/>.</summary>
    /// <remarks>
    /// <see cref="Payload"/> is owned NNTP wire bytes (dot-stuffing preserved, terminator omitted)
    /// or empty when <see cref="Status"/> is not <see cref="NntpMultilineReadStatus.Completed"/>.
    /// </remarks>
    public readonly record struct IHaveArticleReadResult(
        NntpMultilineReadStatus Status,
        ReadOnlyMemory<byte> Payload,
        IHaveReceiveMetrics Metrics);
}
