namespace VectorNNTPD.ReaderService.Session
{
    /// <summary>
    /// Selects which AUTHINFO credential authority is active.
    /// Independent of <see cref="NntpSessionMode"/> receive/capability state.
    /// </summary>
    /// <remarks>
    /// Source IP never selects this value. <c>MODE READER</c> and the unspecified default
    /// use <see cref="Reader"/>. A successful <c>MODE STREAM</c> (or an explicit
    /// <see cref="NntpSessionMode.Stream"/>) uses <see cref="Transit"/>.
    /// There is no Transit ↔ Reader fallback.
    /// </remarks>
    public enum NntpAuthenticationAuthority
    {
        /// <summary>Newsmaster then MySQL <c>nntpusers</c>. Never Transit peer credentials.</summary>
        Reader = 0,

        /// <summary>Configured Transit peer credentials only. Never MySQL or newsmaster.</summary>
        Transit = 1,
    }
}
