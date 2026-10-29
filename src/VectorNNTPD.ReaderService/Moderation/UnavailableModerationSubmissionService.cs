namespace VectorNNTPD.ReaderService.Moderation
{
    /// <summary>
    /// Production default when no mail or durable moderator-delivery backend is registered.
    /// </summary>
    /// <remarks>
    /// RFC 5537 §3.5 step 7 requires the injecting agent to reject the proto-article when
    /// forwarding is not possible. POST maps <see cref="ModerationSubmissionStatus.Unavailable"/>
    /// to <c>441</c>.
    /// </remarks>
    public sealed class UnavailableModerationSubmissionService : IModerationSubmissionService
    {
        /// <summary>Shared instance.</summary>
        public static UnavailableModerationSubmissionService Instance { get; } = new();

        /// <inheritdoc />
        public ValueTask<ModerationSubmissionResult> SubmitAsync(
            ModerationSubmission submission,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(submission);
            _ = cancellationToken;
            return ValueTask.FromResult(
                new ModerationSubmissionResult(
                    ModerationSubmissionStatus.Unavailable,
                    "moderation delivery is not configured"));
        }
    }
}
