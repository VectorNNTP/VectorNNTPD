namespace VectorNNTPD.ReaderService.ArticleIngestion.OverviewDb
{
    /// <summary>
    /// One in-process OverviewDB handoff unit: an already-encoded
    /// <c>OverviewArticleV1</c> payload awaiting RabbitMQ publication.
    /// </summary>
    /// <remarks>
    /// Ownership of <see cref="Payload"/> transfers with the item. The publisher
    /// stage publishes these bytes and does not re-encode. Successful enqueue into
    /// the OverviewDB work queue is <b>not</b> equivalent to a RabbitMQ publisher
    /// confirmation; broker durability still occurs only after the OverviewDB publisher
    /// stage receives a broker confirmation for the outstanding publish.
    /// </remarks>
    public sealed class OverviewDbWorkItem
    {
        /// <summary>Initializes a work item that owns <paramref name="payload"/>.</summary>
        public OverviewDbWorkItem(byte[] payload, string messageId)
        {
            ArgumentNullException.ThrowIfNull(payload);
            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            Payload = payload;
            MessageId = messageId;
        }

        /// <summary>Gets the owned encoded OverviewArticleV1 bytes.</summary>
        public byte[] Payload { get; }

        /// <summary>Gets the article Message-ID for diagnostics.</summary>
        public string MessageId { get; }

        /// <summary>Gets the reserved byte-budget length (payload length).</summary>
        public int ByteLength => Payload.Length;
    }
}
