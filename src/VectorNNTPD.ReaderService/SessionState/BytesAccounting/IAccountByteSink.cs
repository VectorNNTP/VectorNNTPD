namespace VectorNNTPD.ReaderService.SessionState.BytesAccounting
{
    /// <summary>
    /// Hot-path observer for NNTP application bytes copied into the outbound <c>PipeWriter</c>.
    /// </summary>
    /// <remarks>
    /// Implementations must be allocation-free and must not perform Redis or MySQL I/O.
    /// </remarks>
    public interface IAccountByteSink
    {
        /// <summary>Records <paramref name="bytes"/> copied into the output pipe for this account.</summary>
        void ObserveCopied(int bytes);
    }
}
