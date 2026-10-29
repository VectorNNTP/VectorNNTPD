using VectorNNTPD.Common.Articles;
using VectorNNTPD.TransitService.Networking.Proxy;

namespace VectorNNTPD.TransitService.ArticleIngestion
{
    /// <summary>
    /// An article accepted into the ingestion queue as a CanonicalV1 <see cref="ArticleRecord"/>.
    /// </summary>
    /// <remarks>
    /// TAKETHIS, POST, and IHAVE all construct the record before admission.
    /// <see cref="Payload"/> aliases <see cref="ArticleRecord.ArtData"/>. The queue
    /// rejects items that are not CanonicalV1. Workers must not destuff or parse.
    /// </remarks>
    public sealed class InboundArticle
    {
        /// <summary>Initializes a CanonicalV1 queue item.</summary>
        public InboundArticle(
            string messageId,
            ConnectionClientIdentity clientIdentity,
            DateTimeOffset receivedAtUtc,
            InboundArticleProducer producer,
            ArticleRecord record,
            ReadOnlyMemory<byte> feed = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            ArgumentNullException.ThrowIfNull(clientIdentity);
            if (record.ParseStatus != ArticleParseStatus.CanonicalV1)
            {
                throw new ArgumentException("Queued ArticleRecord must be CanonicalV1.", nameof(record));
            }

            MessageId = messageId;
            ClientIdentity = clientIdentity;
            ReceivedAtUtc = receivedAtUtc;
            Producer = producer;
            Record = record;
            Payload = record.ArtData;
            Feed = feed;
        }

        /// <summary>Gets the message-id supplied with the transfer command or POST.</summary>
        public string MessageId { get; }

        /// <summary>Gets canonical ArtData (alias of <see cref="Record"/>).</summary>
        public ReadOnlyMemory<byte> Payload { get; }

        /// <summary>Gets the effective client identity at acceptance time.</summary>
        public ConnectionClientIdentity ClientIdentity { get; }

        /// <summary>Gets the UTC timestamp when the article was accepted into the queue.</summary>
        public DateTimeOffset ReceivedAtUtc { get; }

        /// <summary>Gets which command produced this item.</summary>
        public InboundArticleProducer Producer { get; }

        /// <summary>
        /// Gets the inbound Transit identifier captured at queue admission, or empty
        /// when the session was not a named peer.
        /// </summary>
        public ReadOnlyMemory<byte> Feed { get; }

        /// <summary>Gets the CanonicalV1 ingress record.</summary>
        public ArticleRecord Record { get; }
    }
}
