namespace VectorNNTPD.TransitService.Email
{
    /// <summary>
    /// Transit host stand-in: ninpaths may reference <see cref="IEmailService"/>, but
    /// the full SMTP spool stack is Reader-owned. Enqueues are reported disabled.
    /// </summary>
    internal sealed class NullEmailService : IEmailService
    {
        /// <summary>Shared instance.</summary>
        public static NullEmailService Instance { get; } = new();

        /// <inheritdoc />
        public ValueTask<EmailEnqueueResult> SendAsync(
            EmailMessage message,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new EmailEnqueueResult(EmailEnqueueStatus.Disabled, "email disabled on transit host"));
        }
    }
}
