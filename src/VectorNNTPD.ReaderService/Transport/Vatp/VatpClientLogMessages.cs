using VectorNNTPD.Common.Transport.ArticleTransfer;

namespace VectorNNTPD.ReaderService.Transport.Vatp
{
    /// <summary>Source-generated VATP client log messages.</summary>
    internal static partial class VatpClientLogMessages
    {
        [LoggerMessage(
            EventId = 2800,
            Level = LogLevel.Debug,
            Message = "VATP connecting to {Host}:{Port}.")]
        public static partial void Connecting(ILogger logger, string Host, int Port);

        [LoggerMessage(
            EventId = 2801,
            Level = LogLevel.Debug,
            Message = "VATP TLS established for {Host}:{Port}.")]
        public static partial void TlsEstablished(ILogger logger, string Host, int Port);

        [LoggerMessage(
            EventId = 2802,
            Level = LogLevel.Debug,
            Message = "VATP HELLO complete on {Host}:{Port} maxFramePayload={MaxFramePayload}.")]
        public static partial void HelloComplete(ILogger logger, string Host, int Port, uint MaxFramePayload);

        [LoggerMessage(
            EventId = 2803,
            Level = LogLevel.Debug,
            Message = "VATP OPEN streamId={StreamId} requestId={RequestId} articleId={ArticleIdHex}.")]
        public static partial void OpenSent(
            ILogger logger,
            uint StreamId,
            Guid RequestId,
            string ArticleIdHex);

        [LoggerMessage(
            EventId = 2804,
            Level = LogLevel.Debug,
            Message = "VATP META streamId={StreamId} requestId={RequestId} articleId={ArticleIdHex}.")]
        public static partial void MetaReceived(
            ILogger logger,
            uint StreamId,
            Guid RequestId,
            string ArticleIdHex);

        [LoggerMessage(
            EventId = 2805,
            Level = LogLevel.Debug,
            Message = "VATP transfer complete streamId={StreamId} requestId={RequestId} articleId={ArticleIdHex}.")]
        public static partial void TransferComplete(
            ILogger logger,
            uint StreamId,
            Guid RequestId,
            string ArticleIdHex);

        [LoggerMessage(
            EventId = 2806,
            Level = LogLevel.Debug,
            Message = "VATP transfer failed streamId={StreamId} requestId={RequestId} articleId={ArticleIdHex} errorCode={ErrorCode}.")]
        public static partial void TransferFailed(
            ILogger logger,
            uint StreamId,
            Guid RequestId,
            string ArticleIdHex,
            VatpErrorCode ErrorCode);

        [LoggerMessage(
            EventId = 2807,
            Level = LogLevel.Debug,
            Message = "VATP CANCEL streamId={StreamId} requestId={RequestId} articleId={ArticleIdHex}.")]
        public static partial void CancelSent(
            ILogger logger,
            uint StreamId,
            Guid RequestId,
            string ArticleIdHex);

        [LoggerMessage(
            EventId = 2808,
            Level = LogLevel.Debug,
            Message = "VATP connection closed {Host}:{Port}.")]
        public static partial void ConnectionClosed(ILogger logger, string Host, int Port);

        [LoggerMessage(
            EventId = 2809,
            Level = LogLevel.Debug,
            Message = "VATP protocol violation on {Host}:{Port}: {Detail}.")]
        public static partial void ProtocolViolation(ILogger logger, string Host, int Port, string Detail);
    }
}
