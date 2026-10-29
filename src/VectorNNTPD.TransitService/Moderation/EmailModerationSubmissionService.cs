using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Email;

namespace VectorNNTPD.TransitService.Moderation
{
    /// <summary>
    /// Forwards a proto-article to the moderator by enqueueing outbound email.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type knows email messages, not SMTP. <see cref="IEmailService.SendAsync"/>
    /// acceptance means the complete message was durably written to the local
    /// filesystem spool. It is not proof that a remote SMTP server or moderator
    /// mailbox received the message. SMTP delivery is asynchronous.
    /// </para>
    /// <para>
    /// POST maps <see cref="ModerationSubmissionStatus.Accepted"/> to <c>240</c> and
    /// other statuses to <c>441</c>.
    /// </para>
    /// </remarks>
    public sealed class EmailModerationSubmissionService : IModerationSubmissionService
    {
        private readonly IEmailService _email;
        private readonly ModerationEmailComposer _composer;
        private readonly IOptions<EmailOptions> _emailOptions;

        /// <summary>Initializes a new email-backed submission service.</summary>
        public EmailModerationSubmissionService(
            IEmailService email,
            ModerationEmailComposer composer,
            IOptions<EmailOptions> emailOptions)
        {
            ArgumentNullException.ThrowIfNull(email);
            ArgumentNullException.ThrowIfNull(composer);
            ArgumentNullException.ThrowIfNull(emailOptions);
            _email = email;
            _composer = composer;
            _emailOptions = emailOptions;
        }

        /// <inheritdoc />
        public async ValueTask<ModerationSubmissionResult> SubmitAsync(
            ModerationSubmission submission,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(submission);
            if (!_emailOptions.Value.Enabled)
            {
                return new ModerationSubmissionResult(
                    ModerationSubmissionStatus.Unavailable,
                    "email subsystem is disabled");
            }

            EmailMessage message;
            try
            {
                message = _composer.Compose(submission);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return new ModerationSubmissionResult(
                    ModerationSubmissionStatus.Failed,
                    "moderation email could not be constructed");
            }

            var enqueue = await _email.SendAsync(message, cancellationToken).ConfigureAwait(false);
            return enqueue.Status switch
            {
                EmailEnqueueStatus.Accepted => new ModerationSubmissionResult(
                    ModerationSubmissionStatus.Accepted,
                    "durably accepted into the local email spool"),
                EmailEnqueueStatus.Disabled or EmailEnqueueStatus.Unavailable => new ModerationSubmissionResult(
                    ModerationSubmissionStatus.Unavailable,
                    enqueue.Detail),
                EmailEnqueueStatus.Rejected => new ModerationSubmissionResult(
                    ModerationSubmissionStatus.Failed,
                    enqueue.Detail),
                _ => new ModerationSubmissionResult(
                    ModerationSubmissionStatus.Failed,
                    enqueue.Detail),
            };
        }
    }
}
