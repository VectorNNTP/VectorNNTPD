namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    /// <summary>
    /// Converts logical on-disk article bytes into an NNTP multiline ARTICLE body (dot-stuffed + terminator).
    /// Corpus files are not assumed to be wire-framed.
    /// </summary>
    internal static class NntpWireFramer
    {
        /// <summary>
        /// Adds one stuffing dot to lines that begin with <c>.</c> and appends the terminating <c>.CRLF</c>.
        /// Does not alter logical article content beyond transport stuffing.
        /// </summary>
        internal static byte[] FrameLogicalArticle(ReadOnlySpan<byte> article)
        {
            using var buffer = new MemoryStream(article.Length + 64);
            var offset = 0;
            while (offset < article.Length)
            {
                var lineEnd = article[offset..].IndexOf((byte)'\n');
                var next = lineEnd < 0 ? article.Length : offset + lineEnd + 1;
                var contentEnd = lineEnd < 0
                    ? article.Length
                    : (lineEnd > 0 && article[offset + lineEnd - 1] == (byte)'\r'
                        ? offset + lineEnd - 1
                        : offset + lineEnd);
                if (contentEnd > offset && article[offset] == (byte)'.')
                {
                    buffer.WriteByte((byte)'.');
                }

                buffer.Write(article[offset..next]);
                if (lineEnd < 0)
                {
                    buffer.WriteByte((byte)'\r');
                    buffer.WriteByte((byte)'\n');
                }

                offset = next;
            }

            buffer.WriteByte((byte)'.');
            buffer.WriteByte((byte)'\r');
            buffer.WriteByte((byte)'\n');
            return buffer.ToArray();
        }

        /// <summary>
        /// Streams a framed ARTICLE body from a logical article file without loading the whole file when possible.
        /// For simplicity and correctness with mixed EOL, this implementation reads the file then frames
        /// (bounded by <paramref name="maxArticleBytes"/>).
        /// </summary>
        internal static async Task<(byte[] Logical, byte[] Framed)> ReadAndFrameAsync(
            string path,
            int maxArticleBytes,
            CancellationToken cancellationToken)
        {
            var logical = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (logical.Length > maxArticleBytes)
            {
                throw new InvalidOperationException(
                    $"Article file exceeds maxArticleBytes ({logical.Length} > {maxArticleBytes}): {path}");
            }

            return (logical, FrameLogicalArticle(logical));
        }
    }
}
