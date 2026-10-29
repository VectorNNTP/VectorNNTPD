using VectorNNTPD.TransitService.Email;

namespace VectorNNTPD.TransitService.Tests.TestDoubles;

/// <summary>Captures <see cref="IEmailService.SendAsync"/> without SMTP or spool I/O.</summary>
internal sealed class RecordingEmailService : IEmailService
{
    private readonly List<EmailMessage> _sent = [];

    public EmailEnqueueStatus Status { get; set; } = EmailEnqueueStatus.Accepted;

    public Exception? Throw { get; set; }

    public TaskCompletionSource? Hold { get; set; }

    public TaskCompletionSource SentOnce { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int SendThreadId { get; private set; }

    public int SendCount { get; private set; }

    public IReadOnlyList<EmailMessage> Sent
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    public async ValueTask<EmailEnqueueResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Hold is not null)
        {
            await Hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Throw is not null)
        {
            throw Throw;
        }

        SendThreadId = Environment.CurrentManagedThreadId;
        lock (_sent)
        {
            _sent.Add(message);
            SendCount++;
        }

        SentOnce.TrySetResult();
        return new EmailEnqueueResult(Status, "test");
    }
}
