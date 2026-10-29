using System.IO.Pipelines;

namespace VectorNNTPD.ReaderService.Session.Framing
{
    /// <summary>
    /// Long-lived <see cref="PipeReader"/> loop over <see cref="NntpContinuousRxParser"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Consumed/examined positions follow parser progress: complete units are consumed; incomplete
    /// tails are examined but not consumed. <c>AdvanceTo(buffer.End)</c> is never used as a generic
    /// progress mechanism.
    /// </para>
    /// <para>
    /// Article payloads are owned <c>byte[]</c> copies taken before <c>AdvanceTo</c>.
    /// STREAM units copy framed wire bytes (no destuff). <see cref="ReadArticleAsync"/> destuffs
    /// for <see cref="NntpMultilineDataReader"/>. Pipe sequences are not enqueued.
    /// </para>
    /// </remarks>
    public static class NntpContinuousRxReader
    {
        /// <summary>
        /// Reads one protocol unit. Returns <see cref="NntpContinuousRxKind.NeedMore"/> only when
        /// the pipe completes mid-command (treated as end of stream by the caller) or mid-article.
        /// </summary>
        public static async ValueTask<NntpContinuousRxUnit> ReadUnitAsync(
            PipeReader reader,
            NntpContinuousRxParser parser,
            bool consumeTakeThisArticle,
            int maxArticleBytes,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(reader);
            ArgumentNullException.ThrowIfNull(parser);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxArticleBytes, 1);

            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                var examinedOrigin = buffer.End;

                var unit = parser.TryConsume(ref buffer, consumeTakeThisArticle, maxArticleBytes);
                if (unit.Kind != NntpContinuousRxKind.NeedMore)
                {
                    // Consume the unit; remaining bytes stay in the Pipe for the next call.
                    reader.AdvanceTo(buffer.Start, buffer.Start);
                    return unit;
                }

                // Incomplete command or article: consume copied/complete prefix (buffer.Start),
                // examine everything so the next read waits for new bytes.
                reader.AdvanceTo(buffer.Start, examinedOrigin);
                if (result.IsCompleted)
                {
                    if (parser.Mode == NntpContinuousRxMode.Article)
                    {
                        parser.Reset();
                        return new NntpContinuousRxUnit(
                            NntpContinuousRxKind.TakeThis,
                            article: new NntpMultilineReadResult(
                                NntpMultilineReadStatus.Incomplete,
                                ReadOnlyMemory<byte>.Empty));
                    }

                    parser.Reset();
                    return new NntpContinuousRxUnit(NntpContinuousRxKind.NeedMore);
                }
            }
        }

        /// <summary>
        /// Reads one destuffed article from the current Pipe position (MODE READER / multiline body).
        /// </summary>
        public static async ValueTask<NntpMultilineReadResult> ReadArticleAsync(
            PipeReader reader,
            NntpContinuousRxParser parser,
            int maxArticleBytes,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(reader);
            ArgumentNullException.ThrowIfNull(parser);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxArticleBytes, 1);

            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                var examinedOrigin = buffer.End;
                var unit = parser.TryConsumeArticleOnly(ref buffer, maxArticleBytes);
                if (unit.Kind == NntpContinuousRxKind.TakeThis)
                {
                    reader.AdvanceTo(buffer.Start, buffer.Start);
                    return unit.Article;
                }

                reader.AdvanceTo(buffer.Start, examinedOrigin);
                if (result.IsCompleted)
                {
                    parser.Reset();
                    return new NntpMultilineReadResult(NntpMultilineReadStatus.Incomplete, ReadOnlyMemory<byte>.Empty);
                }
            }
        }
    }
}
