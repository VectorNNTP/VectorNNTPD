using System.Net;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.ReaderService.PostFilter
{
    /// <summary>
    /// One PostFilter rejection queued for asynchronous NntpDB persistence.
    /// The payload is the exact unstuffed article available at the decision, or
    /// <see langword="null"/> when those bytes were not available.
    /// </summary>
    public sealed class PostFilterRejectionEvidence
    {
        /// <summary>Creates an evidence snapshot. Does not copy <paramref name="articlePayload"/>.</summary>
        public PostFilterRejectionEvidence(
            DateTimeOffset rejectedUtc,
            long policyRevision,
            string? accountName,
            IPAddress sourceAddress,
            ArticleType artType,
            string? messageId,
            int articleSize,
            PostFilterStage stage,
            string reason,
            PostFilterSpamAssassinStatus? spamAssassinStatus,
            decimal? spamAssassinScore,
            decimal? spamAssassinThreshold,
            ReadOnlyMemory<byte>? articlePayload)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            ArgumentNullException.ThrowIfNull(sourceAddress);
            ArgumentOutOfRangeException.ThrowIfNegative(articleSize);
            RejectedUtc = rejectedUtc;
            PolicyRevision = policyRevision;
            AccountName = PostFilterAccountIdentity.TryFromRequest(accountName);
            SourceAddress = sourceAddress;
            ArtType = artType;
            MessageId = string.IsNullOrWhiteSpace(messageId) ? null : messageId.Trim();
            ArticleSize = articleSize;
            Stage = stage;
            Reason = reason;
            SpamAssassinStatus = spamAssassinStatus;
            SpamAssassinScore = spamAssassinScore;
            SpamAssassinThreshold = spamAssassinThreshold;
            ArticlePayload = articlePayload;
        }

        /// <summary>Gets the rejection timestamp.</summary>
        public DateTimeOffset RejectedUtc { get; }

        /// <summary>Gets the published policy revision that produced the decision.</summary>
        public long PolicyRevision { get; }

        /// <summary>Gets the MD5 account identifier, or <see langword="null"/> when unauthenticated.</summary>
        public string? AccountName { get; }

        /// <summary>Gets the client source address.</summary>
        public IPAddress SourceAddress { get; }

        /// <summary>Gets the classifier flags from the article, not a policy ENUM.</summary>
        public ArticleType ArtType { get; }

        /// <summary>Gets the Message-ID when available.</summary>
        public string? MessageId { get; }

        /// <summary>Gets the article size in bytes (0 when unknown).</summary>
        public int ArticleSize { get; }

        /// <summary>Gets the PostFilter stage.</summary>
        public PostFilterStage Stage { get; }

        /// <summary>Gets the internal PostFilter reason.</summary>
        public string Reason { get; }

        /// <summary>Gets the SA status for SA-stage rejections.</summary>
        public PostFilterSpamAssassinStatus? SpamAssassinStatus { get; }

        /// <summary>Gets the SA score when present.</summary>
        public decimal? SpamAssassinScore { get; }

        /// <summary>Gets the SA threshold when present.</summary>
        public decimal? SpamAssassinThreshold { get; }

        /// <summary>
        /// Gets the complete unstuffed article, or <see langword="null"/> when the
        /// complete article was not available at the rejection point.
        /// </summary>
        public ReadOnlyMemory<byte>? ArticlePayload { get; }

        /// <summary>Builds evidence from a POST evaluation that already has an <see cref="ArticleRecord"/>.</summary>
        public static PostFilterRejectionEvidence FromEvaluation(
            in PostFilterRequest request,
            in PostFilterResult result,
            DateTimeOffset rejectedUtc)
        {
            var article = request.Article;
            ReadOnlyMemory<byte>? payload = article.ArtSize > 0 ? article.ArtData : null;
            return new(
                rejectedUtc,
                result.PolicyRevision,
                request.AccountName,
                request.ClientIdentity.ClientAddress,
                article.ArtType,
                EncodingMessageId(article),
                article.ArtSize,
                result.Stage,
                result.Reason,
                result.SpamAssassinStatus,
                result.SpamAssassinScore,
                result.SpamAssassinThreshold,
                payload);
        }

        /// <summary>Builds evidence for a post-accept admission failure while the article is still held.</summary>
        public static PostFilterRejectionEvidence FromAdmissionFailure(
            in PostFilterRequest request,
            in PostFilterResult accepted,
            string reason,
            DateTimeOffset rejectedUtc)
        {
            var article = request.Article;
            ReadOnlyMemory<byte>? payload = article.ArtSize > 0 ? article.ArtData : null;
            return new(
                rejectedUtc,
                accepted.PolicyRevision,
                request.AccountName,
                request.ClientIdentity.ClientAddress,
                article.ArtType,
                EncodingMessageId(article),
                article.ArtSize,
                PostFilterStage.Admission,
                reason,
                spamAssassinStatus: null,
                spamAssassinScore: null,
                spamAssassinThreshold: null,
                payload);
        }

        private static string? EncodingMessageId(in ArticleRecord article)
        {
            var messageId = article.MessageId;
            return messageId.IsEmpty ? null : System.Text.Encoding.ASCII.GetString(messageId);
        }
    }
}
