using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.TransitService.ArticleIngestion
{
    /// <summary>
    /// Owned destuffed article produced by downstream IHAVE interpretation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Headers"/> and <see cref="Body"/> are owned destuffed copies built by
    /// <see cref="IhaveArticleInterpreter.DestuffToArticle"/> for benches and classifier
    /// tests. Production IHAVE queues CanonicalV1 <c>ArticleRecord</c> instead.
    /// </para>
    /// <para>
    /// <see cref="Size"/> is the destuffed complete-article length (headers, the header/body
    /// blank line, and body). The NNTP terminating <c>.</c> line is not included. This matches
    /// <c>ArticleIngestionOptions.MaxArticleBytes</c> (“after dot-unstuffing”) and is the
    /// value later Transit routing should use.
    /// </para>
    /// <para>
    /// Headers are raw destuffed header field lines (CRLF-terminated), not a parsed object
    /// model. The body is the destuffed received representation (wire yEnc/BASE64/uuencode
    /// text when those encodings were used — not decoded).
    /// </para>
    /// </remarks>
    public readonly struct Article
    {
        /// <summary>Initializes a new owned article from destuffed header and body bytes.</summary>
        public Article(ReadOnlyMemory<byte> headers, ReadOnlyMemory<byte> body, ArticleType type)
        {
            Headers = headers;
            Body = body;
            Type = type == ArticleType.None ? ArticleType.Default : type;
            var separator = headers.Length > 0 || body.Length > 0 ? 2 : 0;
            Size = checked(headers.Length + separator + body.Length);
            Payload = Combine(headers, body, separator);
        }

        /// <summary>Gets destuffed header field lines, each CRLF-terminated, without the blank separator.</summary>
        public ReadOnlyMemory<byte> Headers { get; }

        /// <summary>Gets destuffed body bytes (received representation; not decoded).</summary>
        public ReadOnlyMemory<byte> Body { get; }

        /// <summary>Gets the destuffed complete article (headers + blank line + body).</summary>
        public ReadOnlyMemory<byte> Payload { get; }

        /// <summary>
        /// Gets destuffed complete-article bytes (headers + blank line + body).
        /// Does not include the NNTP multiline terminator.
        /// </summary>
        public int Size { get; }

        /// <summary>Gets the deterministic Diablo-mapped classification.</summary>
        public ArticleType Type { get; }

        private static ReadOnlyMemory<byte> Combine(
            ReadOnlyMemory<byte> headers,
            ReadOnlyMemory<byte> body,
            int separator)
        {
            if (headers.IsEmpty && body.IsEmpty)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            var buffer = new byte[headers.Length + separator + body.Length];
            headers.Span.CopyTo(buffer);
            if (separator > 0)
            {
                buffer[headers.Length] = (byte)'\r';
                buffer[headers.Length + 1] = (byte)'\n';
            }

            if (!body.IsEmpty)
            {
                body.Span.CopyTo(buffer.AsSpan(headers.Length + separator));
            }

            return buffer;
        }
    }
}
