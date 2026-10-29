namespace VectorNNTPD.TransitService.Moderation
{
    /// <summary>
    /// Durable boundary that accepts a proto-article for moderator forwarding.
    /// </summary>
    /// <remarks>
    /// POST policy calls this abstraction; it does not send email or speak SMTP.
    /// The production implementation composes a moderator email and calls
    /// <c>IEmailService.SendAsync</c>. Queue acceptance is not remote SMTP delivery.
    /// </remarks>
    public interface IModerationSubmissionService
    {
        /// <summary>
        /// Offers <paramref name="submission"/> for moderator delivery.
        /// </summary>
        /// <param name="submission">Proto-article and resolved moderator route.</param>
        /// <param name="cancellationToken">Cancels the attempt. Must not be ignored by implementations that wait.</param>
        /// <returns>Accepted, unavailable, or failed. Does not inject the article.</returns>
        ValueTask<ModerationSubmissionResult> SubmitAsync(
            ModerationSubmission submission,
            CancellationToken cancellationToken = default);
    }
}
