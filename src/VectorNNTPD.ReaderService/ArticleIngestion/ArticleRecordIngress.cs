using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Session.Framing;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.ArticleIngestion
{
    /// <summary>
    /// Builds a Common <see cref="ArticleRecord"/> at the NNTP ingress boundary.
    /// </summary>
    /// <remarks>
    /// Destuff is this type's only extra step and only for stuffed wire. Parsing,
    /// classification, Date/Path materialization, ArtId, and ArtHash stay in
    /// <see cref="ArticleRecordFactory"/>. The destuffed source is discarded after
    /// materialize; the queued record references the factory ArtData buffer.
    /// </remarks>
    internal static class ArticleRecordIngress
    {
        /// <summary>
        /// Parses already-destuffed article bytes into a CanonicalV1 record.
        /// </summary>
        /// <param name="parser">Session-scoped Common parser (local identity for Path hops).</param>
        /// <param name="destuffedArticle">Unstuffed article bytes. Not retained after success.</param>
        /// <returns>Factory result. Accepted <see cref="ArticleRecord.ArtData"/> is the canonical buffer.</returns>
        public static ArticleRecordCreateResult TryCreateFromDestuffed(
            NntpArticleParser parser,
            ReadOnlyMemory<byte> destuffedArticle)
        {
            ArgumentNullException.ThrowIfNull(parser);
            return ArticleRecordFactory.TryCreate(parser, destuffedArticle, ArticlePathMode.Traverse);
        }

        /// <summary>
        /// Parses already-destuffed article bytes with one captured size limit and Path tracker.
        /// </summary>
        /// <param name="parser">Session parser. Its local identity remains the application hop.</param>
        /// <param name="destuffedArticle">Unstuffed article bytes.</param>
        /// <param name="maxArticleBytes">Captured <c>maxartsize</c>.</param>
        /// <param name="siteNameUtf8">Captured <c>sitename</c> bytes.</param>
        /// <returns>Factory result.</returns>
        public static ArticleRecordCreateResult TryCreateFromDestuffed(
            NntpArticleParser parser,
            ReadOnlyMemory<byte> destuffedArticle,
            int maxArticleBytes,
            ReadOnlySpan<byte> siteNameUtf8)
        {
            ArgumentNullException.ThrowIfNull(parser);
            return ArticleRecordFactory.TryCreate(
                parser,
                destuffedArticle,
                ArticlePathMode.Traverse,
                maxArticleBytes,
                siteNameUtf8);
        }

        /// <summary>
        /// Destuffs terminator-omitted wire once, then builds a CanonicalV1 record.
        /// </summary>
        /// <param name="parser">Session-scoped Common parser (local identity for Path hops).</param>
        /// <param name="stuffedWire">Stuffed article bytes without the NNTP terminator.</param>
        /// <param name="maxArticleBytes">Destuff and materialize ceiling.</param>
        /// <returns>
        /// Factory result, or parse-rejected <see cref="NntpArticleParseFailureCode.ArticleTooLarge"/>
        /// when destuff exceeds <paramref name="maxArticleBytes"/>.
        /// </returns>
        public static ArticleRecordCreateResult TryCreateFromStuffedWire(
            NntpArticleParser parser,
            ReadOnlyMemory<byte> stuffedWire,
            int maxArticleBytes)
        {
            ArgumentNullException.ThrowIfNull(parser);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxArticleBytes, 1);
            if (!NntpArticleDestuffer.TryDestuffStuffedWire(stuffedWire.Span, maxArticleBytes, out var destuffed))
            {
                return ArticleRecordCreateResult.RejectedParse(NntpArticleParseFailureCode.ArticleTooLarge);
            }

            return ArticleRecordFactory.TryCreate(
                parser,
                destuffed,
                ArticlePathMode.Traverse,
                maxArticleBytes,
                ArticlePathCanonicalizer.OrganizationalTrackerHost);
        }

        /// <summary>
        /// Destuffs stuffed wire and materializes it with one captured size limit and Path tracker.
        /// </summary>
        /// <param name="parser">Session parser.</param>
        /// <param name="stuffedWire">Stuffed article bytes.</param>
        /// <param name="maxArticleBytes">Captured <c>maxartsize</c>. Also the destuff ceiling.</param>
        /// <param name="siteNameUtf8">Captured <c>sitename</c> bytes.</param>
        /// <returns>Factory result, or <see cref="NntpArticleParseFailureCode.ArticleTooLarge"/> when destuff exceeds the limit.</returns>
        public static ArticleRecordCreateResult TryCreateFromStuffedWire(
            NntpArticleParser parser,
            ReadOnlyMemory<byte> stuffedWire,
            int maxArticleBytes,
            ReadOnlySpan<byte> siteNameUtf8)
        {
            ArgumentNullException.ThrowIfNull(parser);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxArticleBytes, 1);
            if (!NntpArticleDestuffer.TryDestuffStuffedWire(stuffedWire.Span, maxArticleBytes, out var destuffed))
            {
                return ArticleRecordCreateResult.RejectedParse(NntpArticleParseFailureCode.ArticleTooLarge);
            }

            return ArticleRecordFactory.TryCreate(
                parser,
                destuffed,
                ArticlePathMode.Traverse,
                maxArticleBytes,
                siteNameUtf8);
        }

        /// <summary>
        /// Creates a queue item whose <see cref="InboundArticle.Payload"/> aliases
        /// <paramref name="record"/>.ArtData (no article-sized copy).
        /// </summary>
        /// <param name="messageId">Command or POST Message-ID text.</param>
        /// <param name="record">Accepted CanonicalV1 record.</param>
        /// <param name="clientIdentity">Session client identity at admission.</param>
        /// <param name="receivedAtUtc">Queue-admission timestamp.</param>
        /// <param name="producer">TAKETHIS, POST, IHAVE, or BackFiller.</param>
        /// <param name="feed">Inbound Transit identifier bytes captured from the session.</param>
        /// <returns>Queue item carrying <paramref name="record"/>.</returns>
        /// <remarks>
        /// Throws only when <paramref name="messageId"/> is empty or
        /// <paramref name="record"/> is not CanonicalV1. POST calls this only after
        /// <c>TryCreateFromDestuffed</c> accepted CanonicalV1 with a non-empty
        /// Message-ID; those throws are unreachable on that path.
        /// </remarks>
        public static InboundArticle CreateQueued(
            string messageId,
            in ArticleRecord record,
            ConnectionClientIdentity clientIdentity,
            DateTimeOffset receivedAtUtc,
            InboundArticleProducer producer,
            ReadOnlyMemory<byte> feed = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            if (record.ParseStatus != ArticleParseStatus.CanonicalV1)
            {
                throw new ArgumentException("Queued ArticleRecord must be CanonicalV1.", nameof(record));
            }

            return new InboundArticle(
                messageId,
                clientIdentity,
                receivedAtUtc,
                producer,
                record,
                feed);
        }
    }
}
