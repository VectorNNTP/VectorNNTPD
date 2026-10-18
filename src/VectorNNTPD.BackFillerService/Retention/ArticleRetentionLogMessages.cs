namespace VectorNNTPD.BackFillerService.Retention
{
    /// <summary>Source-generated retention logs. Never includes article bodies or secrets.</summary>
    internal static partial class ArticleRetentionLogMessages
    {
        /// <summary>
        /// Writes event 5500 after a new Message-ID entry is indexed and its ArtData length is added.
        /// </summary>
        /// <param name="logger">Logger that receives the information event.</param>
        /// <param name="ArticleIdHex">Lowercase hexadecimal ArticleId of the retained entry.</param>
        /// <param name="PayloadBytes">ArtData length just added.</param>
        /// <param name="RetainedPayloadBytes">Owned payload bytes after that add.</param>
        [LoggerMessage(
            EventId = 5500,
            Level = LogLevel.Debug,
            Message = "Article retained articleId={ArticleIdHex} bytes={PayloadBytes} retainedBytes={RetainedPayloadBytes}")]
        internal static partial void Retained(ILogger logger, string ArticleIdHex, int PayloadBytes, long RetainedPayloadBytes);

        /// <summary>Writes event 5501 when admission refuses a payload. The refusal does not change the owned total.</summary>
        /// <param name="logger">Logger that receives the warning.</param>
        /// <param name="Kind">Rejection classification.</param>
        /// <param name="ArticleIdHex">
        /// ArticleId hex when identity was computed. <see langword="null"/> for
        /// <see cref="ArticleRetentionKind.InvalidPayload"/> and
        /// <see cref="ArticleRetentionKind.PayloadExceedsCapacity"/>, which are logged before an identity exists.
        /// </param>
        /// <param name="PayloadBytes">
        /// <see cref="VectorNNTPD.Common.Articles.ArticleRecord.ArtSize"/> when the record is invalid;
        /// otherwise the ArtData length that was refused.
        /// </param>
        /// <param name="RetainedPayloadBytes">Owned payload bytes observed for the log. This event does not change that total.</param>
        [LoggerMessage(
            EventId = 5501,
            Level = LogLevel.Warning,
            Message = "Article retention rejected kind={Kind} articleId={ArticleIdHex} bytes={PayloadBytes} retainedBytes={RetainedPayloadBytes}")]
        internal static partial void Rejected(ILogger logger, ArticleRetentionKind Kind, string? ArticleIdHex, int PayloadBytes, long RetainedPayloadBytes);

        /// <summary>
        /// Writes event 5502 after <see cref="ArticleRetentionAuthority.SweepExpired"/> physically releases bytes.
        /// Not written when that pass releases nothing, and not written for FIFO reclaim during admission.
        /// </summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="ReleasedBytes">Payload bytes physically disposed by that sweep.</param>
        /// <param name="RetainedPayloadBytes">Owned payload bytes after the sweep.</param>
        [LoggerMessage(
            EventId = 5502,
            Level = LogLevel.Debug,
            Message = "Article retention sweep releasedBytes={ReleasedBytes} retainedBytes={RetainedPayloadBytes}")]
        internal static partial void Swept(ILogger logger, long ReleasedBytes, long RetainedPayloadBytes);

    }
}
