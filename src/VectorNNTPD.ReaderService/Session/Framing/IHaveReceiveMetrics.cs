namespace VectorNNTPD.ReaderService.Session.Framing
{
    /// <summary>IHAVE receive instrumentation (frame + own; no destuff).</summary>
    public readonly record struct IHaveReceiveMetrics(
        int PipeReads,
        int ArticleSize,
        TimeSpan ReceiveElapsed);
}
