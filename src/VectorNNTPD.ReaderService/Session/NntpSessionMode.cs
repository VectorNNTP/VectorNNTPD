namespace VectorNNTPD.ReaderService.Session
{
    /// <summary>NNTP operating mode (distinct from authentication/authorization).</summary>
    public enum NntpSessionMode
    {
        /// <summary>No MODE command has successfully selected an operating mode yet.</summary>
        Unspecified = 0,

        /// <summary>Reader mode after a successful <c>MODE READER</c>.</summary>
        Reader = 1,

        /// <summary>
        /// Transit/streaming operating mode. <c>MODE STREAM</c> does not set this (RFC 4644 §2.3);
        /// AUTHINFO Transit authority is selected separately via
        /// <see cref="NntpAuthenticationAuthority"/>.
        /// </summary>
        Stream = 2,
    }
}
