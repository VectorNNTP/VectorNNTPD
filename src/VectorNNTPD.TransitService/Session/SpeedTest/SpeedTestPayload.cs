namespace VectorNNTPD.TransitService.Session.SpeedTest
{
    /// <summary>
    /// Deterministic synthetic SPEEDTEST payload. One immortal chunk is reused for every write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The payload is not an article, is not read from disk, and is not customer or feed data.
    /// It is a fixed ASCII pattern chosen so NNTP dot-stuffing never applies: no line begins
    /// with <c>.</c>. Content characteristics are therefore irrelevant to compression or
    /// article-pipeline behaviour.
    /// </para>
    /// <para>
    /// Each line is <see cref="LineBytes"/> octets: one <c>#</c>, then repeating digits
    /// <c>0–9</c>, then CRLF. <see cref="Chunk"/> is <see cref="LinesPerChunk"/> concatenated
    /// lines (<see cref="ChunkBytes"/>). Callers write a CRLF-aligned slice of that buffer
    /// and must not retain a reference after the awaited flush when they later reuse the
    /// same immortal memory for the next write (writes are sequential and flush-awaited).
    /// </para>
    /// </remarks>
    internal static class SpeedTestPayload
    {
        /// <summary>One payload line including CRLF.</summary>
        public const int LineBytes = 1024;

        /// <summary>Lines packed into the reusable chunk.</summary>
        public const int LinesPerChunk = 64;

        /// <summary>Reusable chunk size (64 KiB).</summary>
        public const int ChunkBytes = LineBytes * LinesPerChunk;

        /// <summary>Immortal reusable chunk. The TX pump only reads it.</summary>
        public static readonly ReadOnlyMemory<byte> Chunk = BuildChunk();

        /// <summary>
        /// Returns a CRLF-aligned prefix of <see cref="Chunk"/> no larger than
        /// <paramref name="remainingBytes"/>. Empty when fewer than <see cref="LineBytes"/> remain.
        /// </summary>
        public static ReadOnlyMemory<byte> Take(long remainingBytes)
        {
            if (remainingBytes < LineBytes)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            var take = remainingBytes >= ChunkBytes
                ? ChunkBytes
                : (int)(remainingBytes - (remainingBytes % LineBytes));
            return Chunk[..take];
        }

        private static byte[] BuildChunk()
        {
            var chunk = new byte[ChunkBytes];
            for (var line = 0; line < LinesPerChunk; line++)
            {
                var offset = line * LineBytes;
                chunk[offset] = (byte)'#';
                for (var i = 1; i < LineBytes - 2; i++)
                {
                    chunk[offset + i] = (byte)('0' + (i % 10));
                }

                chunk[offset + LineBytes - 2] = (byte)'\r';
                chunk[offset + LineBytes - 1] = (byte)'\n';
            }

            return chunk;
        }
    }
}
