using System.IO.Pipelines;

namespace VectorNNTPD.TransitService.Session.Framing
{
    /// <summary>Result of reading one NNTP multiline data block from a <see cref="PipeReader"/>.</summary>
    public enum NntpMultilineReadStatus
    {
        /// <summary>
        /// Complete article consumed (terminator seen). Payload meaning depends on the reader:
        /// STREAM framing copies wire bytes; <see cref="NntpMultilineDataReader"/> destuffs.
        /// </summary>
        Completed = 0,

        /// <summary>Peer disconnected before the terminating dot line; nothing should be enqueued.</summary>
        Incomplete = 1,

        /// <summary>
        /// Article exceeded the configured size limit; remaining bytes through the terminator were discarded.
        /// </summary>
        TooLarge = 2,
    }

    /// <summary>Outcome of <see cref="NntpMultilineDataReader.ReadArticleAsync"/>.</summary>
    /// <param name="Status">Read status.</param>
    /// <param name="Payload">
    /// Article bytes when <see cref="NntpMultilineReadStatus.Completed"/> (STREAM: framed wire
    /// without terminator; multiline reader: destuffed); otherwise empty.
    /// </param>
    public readonly record struct NntpMultilineReadResult(
        NntpMultilineReadStatus Status,
        ReadOnlyMemory<byte> Payload);

    /// <summary>
    /// Reads an NNTP multiline data block (RFC 3977 §3.1.1) with dot-unstuffing.
    /// </summary>
    /// <remarks>
    /// Terminator is a line containing only <c>.</c> (i.e. <c>.CRLF</c>).
    /// A leading doubled dot on a content line is reduced to a single leading dot.
    /// The terminator line is not included in the payload.
    /// Line boundaries are located with <see cref="NntpDelimiterSearch"/> (runtime-vectorized
    /// <c>IndexOf</c> on contiguous spans, sequence scan across segments).
    /// </remarks>
    public static class NntpMultilineDataReader
    {
        /// <summary>
        /// Reads one multiline article from <paramref name="reader"/> up to <paramref name="maxArticleBytes"/>.
        /// </summary>
        public static async ValueTask<NntpMultilineReadResult> ReadArticleAsync(
            PipeReader reader,
            int maxArticleBytes,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(reader);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxArticleBytes, 1);

            var parser = new NntpContinuousRxParser();
            return await NntpContinuousRxReader
                .ReadArticleAsync(reader, parser, maxArticleBytes, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
