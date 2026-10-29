namespace VectorNNTPD.TransitService.Email
{
    /// <summary>Source-generated email subsystem log messages. Never includes credentials.</summary>
    internal static partial class EmailLogMessages
    {
        [LoggerMessage(
            EventId = 2600,
            Level = LogLevel.Information,
            Message = "Email delivery service starting (enabled {Enabled}, security {SecurityMode})")]
        public static partial void DeliveryStarting(ILogger logger, bool Enabled, string SecurityMode);

        [LoggerMessage(
            EventId = 2601,
            Level = LogLevel.Information,
            Message = "Email delivery service stopped")]
        public static partial void DeliveryStopped(ILogger logger);

        [LoggerMessage(
            EventId = 2602,
            Level = LogLevel.Debug,
            Message = "Outbound email accepted by local spool (recipients {RecipientCount}, bytes {EncodedBytes}, correlation {CorrelationId})")]
        public static partial void Enqueued(
            ILogger logger,
            int RecipientCount,
            int EncodedBytes,
            string? CorrelationId);

        [LoggerMessage(
            EventId = 2604,
            Level = LogLevel.Information,
            Message = "SMTP session starting ({Host}:{Port}, {SecurityMode})")]
        public static partial void SmtpSessionStarting(ILogger logger, string Host, int Port, string SecurityMode);

        [LoggerMessage(
            EventId = 2605,
            Level = LogLevel.Information,
            Message = "SMTP session completed ({Host}:{Port}, accepted recipients {AcceptedRecipients})")]
        public static partial void SmtpSessionCompleted(ILogger logger, string Host, int Port, int AcceptedRecipients);

        [LoggerMessage(
            EventId = 2606,
            Level = LogLevel.Warning,
            Message = "SMTP delivery will retry (attempt {Attempt}/{MaxAttempts}, kind {FailureKind}, status {StatusCode})")]
        public static partial void DeliveryRetry(
            ILogger logger,
            int Attempt,
            int MaxAttempts,
            string FailureKind,
            int StatusCode);

        [LoggerMessage(
            EventId = 2607,
            Level = LogLevel.Error,
            Message = "Email delivery failed (kind {FailureKind}, status {StatusCode}, recipients {RecipientCount}, correlation {CorrelationId})")]
        public static partial void DeliveryFailed(
            ILogger logger,
            string FailureKind,
            int StatusCode,
            int RecipientCount,
            string? CorrelationId);

        [LoggerMessage(
            EventId = 2609,
            Level = LogLevel.Debug,
            Message = "Email message rejected before enqueue ({Reason})")]
        public static partial void MessageRejected(ILogger logger, string Reason);

        [LoggerMessage(
            EventId = 2610,
            Level = LogLevel.Error,
            Message = "Unexpected email delivery failure (recipients {RecipientCount}, correlation {CorrelationId})")]
        public static partial void DeliveryUnexpected(ILogger logger, Exception exception, int RecipientCount, string? CorrelationId);

        [LoggerMessage(
            EventId = 2611,
            Level = LogLevel.Error,
            Message = "Email spool write failed")]
        public static partial void SpoolWriteFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2612,
            Level = LogLevel.Warning,
            Message = "Email spool claim recovery failed for {FileName}")]
        public static partial void SpoolRecoverFailed(ILogger logger, Exception exception, string FileName);

        [LoggerMessage(
            EventId = 2613,
            Level = LogLevel.Error,
            Message = "Email spool file {SpoolId} is malformed and was moved to failed")]
        public static partial void SpoolMalformed(ILogger logger, string SpoolId);

        [LoggerMessage(
            EventId = 2614,
            Level = LogLevel.Error,
            Message = "Email spool quarantine failed for {SpoolId}")]
        public static partial void SpoolQuarantineFailed(ILogger logger, Exception exception, string SpoolId);

        [LoggerMessage(
            EventId = 2615,
            Level = LogLevel.Critical,
            Message = "SMTP accepted the message but the spool file {SpoolId} could not be deleted; it may be resent after restart")]
        public static partial void SpoolDeleteAfterAcceptFailed(ILogger logger, Exception exception, string SpoolId);
    }
}
