using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Transport.ArticleTransfer;

namespace VectorNNTPD.ReaderService.Transport.Vatp
{
    /// <summary>Result of <see cref="IVatpArticleClient.FetchArticleAsync"/>.</summary>
    public readonly record struct VatpFetchResult
    {
        private VatpFetchResult(
            VatpFetchKind kind,
            ArticleRecord record,
            string? error,
            VatpErrorCode? errorCode,
            Guid? requestId,
            ArticleId? articleId,
            int acceptedDataBytes)
        {
            if (acceptedDataBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(acceptedDataBytes));
            }

            Kind = kind;
            Record = record;
            Error = error;
            ErrorCode = errorCode;
            RequestId = requestId;
            ArticleId = articleId;
            AcceptedDataBytes = acceptedDataBytes;
        }

        /// <summary>Gets the outcome kind.</summary>
        public VatpFetchKind Kind { get; }

        /// <summary>Gets the article when <see cref="Kind"/> is <see cref="VatpFetchKind.Success"/>.</summary>
        public ArticleRecord Record { get; }

        /// <summary>Gets optional diagnostic text (never article payload).</summary>
        public string? Error { get; }

        /// <summary>Gets the VATP error code when supplied by the peer or state machine.</summary>
        public VatpErrorCode? ErrorCode { get; }

        /// <summary>Gets the OPEN RequestId when known.</summary>
        public Guid? RequestId { get; }

        /// <summary>Gets the OPEN ArticleId when known.</summary>
        public ArticleId? ArticleId { get; }

        /// <summary>
        /// Gets the number of DATA payload bytes copied into the receive stream before this result was built.
        /// </summary>
        /// <remarks>
        /// Zero when no receive stream existed or no DATA payload had been copied.
        /// When a stream exists, this is that stream's copied DATA count at completion.
        /// </remarks>
        public int AcceptedDataBytes { get; }

        /// <summary>Creates a success result.</summary>
        public static VatpFetchResult FromSuccess(
            ArticleRecord record,
            Guid requestId,
            ArticleId articleId,
            int acceptedDataBytes = 0) =>
            new(VatpFetchKind.Success, record, null, null, requestId, articleId, acceptedDataBytes);

        /// <summary>Creates a remote transfer failure.</summary>
        public static VatpFetchResult RemoteFailure(
            string? error,
            VatpErrorCode? errorCode,
            Guid requestId,
            ArticleId articleId,
            int acceptedDataBytes = 0) =>
            new(VatpFetchKind.RemoteTransferFailure, default, error, errorCode, requestId, articleId, acceptedDataBytes);

        /// <summary>Creates a protocol failure.</summary>
        public static VatpFetchResult ProtocolFailure(
            string? error,
            Guid? requestId = null,
            ArticleId? articleId = null,
            int acceptedDataBytes = 0) =>
            new(VatpFetchKind.ProtocolFailure, default, error, null, requestId, articleId, acceptedDataBytes);

        /// <summary>Creates a connection failure.</summary>
        public static VatpFetchResult ConnectionFailure(
            string? error,
            Guid? requestId = null,
            ArticleId? articleId = null,
            int acceptedDataBytes = 0) =>
            new(VatpFetchKind.ConnectionFailure, default, error, null, requestId, articleId, acceptedDataBytes);

        /// <summary>Creates a cancelled result.</summary>
        public static VatpFetchResult Cancelled(Guid requestId, ArticleId articleId, int acceptedDataBytes = 0) =>
            new(VatpFetchKind.Cancelled, default, null, VatpErrorCode.Cancelled, requestId, articleId, acceptedDataBytes);

        /// <summary>Creates an invalid ArticleId result.</summary>
        public static VatpFetchResult InvalidArticleId(string? error) =>
            new(VatpFetchKind.InvalidArticleId, default, error, null, null, null, 0);

        /// <summary>Creates an incomplete or malformed article result.</summary>
        public static VatpFetchResult IncompleteOrMalformed(
            string? error,
            VatpErrorCode? errorCode,
            Guid requestId,
            ArticleId articleId,
            int acceptedDataBytes = 0) =>
            new(
                VatpFetchKind.IncompleteOrMalformedArticle,
                default,
                error,
                errorCode,
                requestId,
                articleId,
                acceptedDataBytes);
    }
}
