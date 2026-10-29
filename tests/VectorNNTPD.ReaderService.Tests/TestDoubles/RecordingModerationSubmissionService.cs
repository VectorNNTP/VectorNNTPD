using VectorNNTPD.ReaderService.Moderation;

namespace VectorNNTPD.ReaderService.Tests.TestDoubles;

/// <summary>Records moderation submissions and returns a configured result.</summary>
public sealed class RecordingModerationSubmissionService : IModerationSubmissionService
{
    /// <summary>Initializes a new instance of the <see cref="RecordingModerationSubmissionService"/> class.</summary>
    public RecordingModerationSubmissionService(
        ModerationSubmissionStatus status = ModerationSubmissionStatus.Accepted,
        string detail = "recorded")
    {
        Status = status;
        Detail = detail;
    }

    /// <summary>Gets the status returned to callers.</summary>
    public ModerationSubmissionStatus Status { get; set; }

    /// <summary>Gets the detail returned to callers.</summary>
    public string Detail { get; set; }

    /// <summary>Gets recorded submissions in call order.</summary>
    public List<ModerationSubmission> Submissions { get; } = [];

    /// <inheritdoc />
    public ValueTask<ModerationSubmissionResult> SubmitAsync(
        ModerationSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        cancellationToken.ThrowIfCancellationRequested();
        Submissions.Add(submission);
        return ValueTask.FromResult(new ModerationSubmissionResult(Status, Detail));
    }
}
