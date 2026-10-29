using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;

namespace VectorNNTPD.ReaderService.Email
{
    /// <summary>
    /// Public email API: validate, encode, and durably spool. Does not perform SMTP I/O.
    /// </summary>
    public sealed class EmailService : IEmailService
    {
        private readonly IEmailSpool _spool;
        private readonly IEmailMessageEncoder _encoder;
        private readonly IOptions<EmailOptions> _options;
        private readonly ILogger<EmailService> _logger;

        /// <summary>Initializes a new email service.</summary>
        public EmailService(
            IEmailSpool spool,
            IEmailMessageEncoder encoder,
            IOptions<EmailOptions> options,
            ILogger<EmailService> logger)
        {
            ArgumentNullException.ThrowIfNull(spool);
            ArgumentNullException.ThrowIfNull(encoder);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            _spool = spool;
            _encoder = encoder;
            _options = options;
            _logger = logger;
        }

        /// <inheritdoc />
        public async ValueTask<EmailEnqueueResult> SendAsync(
            EmailMessage message,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);

            var options = _options.Value;
            if (!options.Enabled)
            {
                return new EmailEnqueueResult(EmailEnqueueStatus.Disabled, "email subsystem is disabled");
            }

            if (!_spool.IsAccepting)
            {
                return new EmailEnqueueResult(EmailEnqueueStatus.Unavailable, "email spool is not accepting work");
            }

            EmailWorkItem item;
            try
            {
                Rfc5322MessageEncoder.Validate(message);
                var encoded = _encoder.Encode(message);
                item = new EmailWorkItem
                {
                    Message = message,
                    EncodedMessage = encoded,
                    EnvelopeSender = ResolveEnvelopeSender(message, options),
                    Recipients = message.EnvelopeRecipients(),
                    CorrelationId = message.CorrelationId,
                };
            }
            catch (ArgumentException ex)
            {
                EmailLogMessages.MessageRejected(_logger, ex.Message);
                return new EmailEnqueueResult(EmailEnqueueStatus.Rejected, ex.Message);
            }

            try
            {
                _ = await _spool.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new EmailEnqueueResult(EmailEnqueueStatus.Failed, "spool write was canceled");
            }
            catch (InvalidOperationException)
            {
                return new EmailEnqueueResult(EmailEnqueueStatus.Unavailable, "email spool is not accepting work");
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                EmailLogMessages.SpoolWriteFailed(_logger, ex);
                return new EmailEnqueueResult(EmailEnqueueStatus.Failed, "spool write failed");
            }

            EmailLogMessages.Enqueued(
                _logger,
                item.Recipients.Count,
                item.EncodedMessage.Length,
                item.CorrelationId);
            return new EmailEnqueueResult(
                EmailEnqueueStatus.Accepted,
                "durably accepted into the local email spool");
        }

        internal static EmailAddress ResolveEnvelopeSender(EmailMessage message, EmailOptions options)
        {
            if (message.EnvelopeSender is not null)
            {
                return message.EnvelopeSender;
            }

            if (EmailOptionsValidator.TryValidateMailbox(options.EnvelopeSender, out var envelope))
            {
                return new EmailAddress(envelope);
            }

            if (EmailOptionsValidator.TryValidateMailbox(options.DefaultFrom, out var from))
            {
                return new EmailAddress(from);
            }

            return message.From;
        }
    }
}
