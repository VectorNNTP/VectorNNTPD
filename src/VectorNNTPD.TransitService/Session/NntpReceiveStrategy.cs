namespace VectorNNTPD.TransitService.Session
{
    /// <summary>
    /// Per-connection RX strategy selected from existing <see cref="NntpSession.Mode"/>.
    /// </summary>
    /// <remarks>
    /// RFC 4644 §2.3: <c>MODE STREAM</c> MUST NOT change server state, so this is not set by that
    /// command. <see cref="StreamDataPlane"/> is the default (unspecified) path used by transit/feed
    /// peers. <see cref="ReaderCommand"/> is selected only after a successful <c>MODE READER</c>.
    /// </remarks>
    internal enum NntpReceiveStrategy
    {
        /// <summary>Command-oriented RX: <c>ReadLineAsync</c> → parser → dispatcher.</summary>
        ReaderCommand = 0,

        /// <summary>Continuous CHECK/TAKETHIS data-plane RX.</summary>
        StreamDataPlane = 1,
    }
}
