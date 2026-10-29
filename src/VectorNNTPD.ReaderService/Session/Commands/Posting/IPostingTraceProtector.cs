namespace VectorNNTPD.ReaderService.Session.Commands.Posting
{
    /// <summary>
    /// Authenticated-encryption boundary for server-owned POST <c>X-Trace</c> values.
    /// </summary>
    /// <remarks>
    /// Implementations must provide confidentiality, integrity, and tamper detection.
    /// Do not log keys or decrypted payloads.
    /// </remarks>
    public interface IPostingTraceProtector
    {
        /// <summary>
        /// Protects <paramref name="payload"/> as an opaque header-safe token.
        /// </summary>
        string Protect(PostingTracePayload payload);

        /// <summary>
        /// Attempts to recover a payload from a previously protected token.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> when authentication succeeds and the payload is well-formed;
        /// otherwise <see langword="false"/> (tamper, unknown version, or wrong key).
        /// </returns>
        bool TryUnprotect(ReadOnlySpan<char> token, out PostingTracePayload payload);
    }
}
