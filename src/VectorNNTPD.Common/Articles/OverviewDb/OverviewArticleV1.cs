namespace VectorNNTPD.Common.Articles.OverviewDb
{
    /// <summary>
    /// Decoded OverviewDB ingest handoff. Production encoding writes this shape as
    /// protobuf; this type is the decoded view used by OverviewDB consumers and tests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the wire DTO. RabbitMQ publication, confirms, topology, and NNTP
    /// ingress remain application-owned.
    /// </para>
    /// <para>
    /// Textual overview fields are raw header value octets (<see cref="byte"/>[]), matching
    /// <c>ArticleRecord</c> ranges and the article parser's permitted header bytes (including
    /// <c>0x80</c>–<c>0xFF</c>). They are not UTF-8-decoded strings.
    /// </para>
    /// </remarks>
    internal sealed class OverviewArticleV1
    {
        /// <summary>Current contract version encoded in every payload.</summary>
        public const uint CurrentSchemaVersion = 1;

        /// <summary>Gets or sets the schema version. Must be <see cref="CurrentSchemaVersion"/>.</summary>
        internal uint SchemaVersion { get; set; }

        /// <summary>Gets or sets the 32-byte VectorNNTP <c>ArticleId</c> / ArtId digest.</summary>
        internal byte[] ArticleId { get; set; } = [];

        /// <summary>Gets or sets the Message-ID header value octets, including angle brackets.</summary>
        internal byte[] MessageId { get; set; } = [];

        /// <summary>Gets the newsgroup membership tokens in article header order.</summary>
        internal List<byte[]> Newsgroups { get; } = [];

        /// <summary>Gets or sets the Subject header value octets.</summary>
        internal byte[] Subject { get; set; } = [];

        /// <summary>Gets or sets the From header value octets.</summary>
        internal byte[] From { get; set; } = [];

        /// <summary>Gets or sets the canonical Date header value octets from <c>ArticleRecord</c>.</summary>
        internal byte[] Date { get; set; } = [];

        /// <summary>Gets or sets the References header value octets.</summary>
        internal byte[] References { get; set; } = [];

        /// <summary>Gets or sets canonical article byte size (<c>ArticleRecord.ArtSize</c>).</summary>
        internal uint Bytes { get; set; }

        /// <summary>Gets or sets the overview line count (<c>ArticleRecord.ArtLines</c>).</summary>
        internal uint Lines { get; set; }
    }
}
