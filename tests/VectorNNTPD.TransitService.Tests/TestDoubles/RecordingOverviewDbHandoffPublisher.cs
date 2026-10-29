using VectorNNTPD.TransitService.ArticleIngestion.OverviewDb;

namespace VectorNNTPD.TransitService.Tests.TestDoubles;

/// <summary>Captures OverviewDB handoff payloads and can nack a budgeted number of publishes.</summary>
internal sealed class RecordingOverviewDbHandoffPublisher : IOverviewDbHandoffPublisher
{
    private int _attempts;
    private int _outstanding;
    private readonly object _gate = new();
    private readonly Queue<OverviewDbWorkItem> _failures = new();
    private readonly TaskCompletionSource<int> _firstAttempt =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _confirmed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets captured successfully confirmed payloads in publish order.</summary>
    public List<byte[]> Payloads { get; } = [];

    /// <summary>Gets the number of publish attempts, including nacks.</summary>
    public int AttemptCount => Volatile.Read(ref _attempts);

    /// <summary>Completes when the first publish attempt begins.</summary>
    public Task<int> FirstAttempt => _firstAttempt.Task;

    /// <summary>Completes when the first payload is recorded as confirmed.</summary>
    public Task Confirmed => _confirmed.Task;

    private int _confirmWaitCount;
    private TaskCompletionSource? _confirmWait;

    /// <summary>
    /// Completes when at least <paramref name="count"/> payloads have been confirmed.
    /// </summary>
    public Task WaitUntilConfirmedAsync(int count, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Payloads.Count >= count)
            {
                return Task.CompletedTask;
            }

            _confirmWaitCount = count;
            _confirmWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _confirmWait.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Remaining attempts that enqueue a failure instead of confirming.</summary>
    public int RemainingFailures { get; set; }

    /// <summary>
    /// Exception thrown for each <see cref="RemainingFailures"/> budgeted attempt.
    /// </summary>
    public Exception? TransientPublishException { get; set; }

    /// <summary>When set, every attempt throws this exception after the remaining-failure budget.</summary>
    public Exception? PublishException { get; set; }

    /// <summary>
    /// When set, a failing attempt waits on this source after recording the attempt
    /// and before enqueueing the failure.
    /// </summary>
    public TaskCompletionSource? BlockOnFailure { get; set; }

    /// <summary>
    /// Artificial confirmation delay applied after <see cref="PublishAsync"/> returns.
    /// Simulates quorum confirm latency without blocking the publish call.
    /// </summary>
    public TimeSpan ConfirmDelay { get; set; }

    /// <summary>Maximum outstanding unconfirmed publishes (window). Zero means unbounded.</summary>
    public int OutstandingWindow { get; set; }

    /// <inheritdoc />
    public int OutstandingCount => Volatile.Read(ref _outstanding);

    /// <inheritdoc />
    public async Task PublishAsync(OverviewDbWorkItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        if (OutstandingWindow > 0)
        {
            while (Volatile.Read(ref _outstanding) >= OutstandingWindow)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }

        var attempt = Interlocked.Increment(ref _attempts);
        _firstAttempt.TrySetResult(attempt);
        Interlocked.Increment(ref _outstanding);

        if (RemainingFailures > 0)
        {
            RemainingFailures--;
            if (BlockOnFailure is not null)
            {
                await BlockOnFailure.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Decrement(ref _outstanding);
            if (TransientPublishException is not null || PublishException is not null)
            {
                throw TransientPublishException
                    ?? PublishException
                    ?? new InvalidOperationException("RabbitMQ negatively acknowledged the OverviewDB handoff.");
            }

            EnqueueFailure(item);
            return;
        }

        if (PublishException is not null)
        {
            if (BlockOnFailure is not null)
            {
                await BlockOnFailure.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Decrement(ref _outstanding);
            throw PublishException;
        }

        // Fire-and-forget confirm so PublishAsync is not serialized behind ConfirmDelay.
        _ = ConfirmLaterAsync(item, cancellationToken);
    }

    /// <inheritdoc />
    public bool TryDequeuePublishFailure(out OverviewDbWorkItem item)
    {
        lock (_gate)
        {
            if (_failures.Count == 0)
            {
                item = null!;
                return false;
            }

            item = _failures.Dequeue();
            return true;
        }
    }

    /// <inheritdoc />
    public void AbandonOutstanding()
    {
        Volatile.Write(ref _outstanding, 0);
        lock (_gate)
        {
            _failures.Clear();
        }
    }

    private async Task ConfirmLaterAsync(OverviewDbWorkItem item, CancellationToken cancellationToken)
    {
        try
        {
            if (ConfirmDelay > TimeSpan.Zero)
            {
                await Task.Delay(ConfirmDelay, CancellationToken.None).ConfigureAwait(false);
            }

            lock (_gate)
            {
                Payloads.Add(item.Payload.ToArray());
                _confirmed.TrySetResult();
                if (_confirmWait is not null && Payloads.Count >= _confirmWaitCount)
                {
                    _confirmWait.TrySetResult();
                    _confirmWait = null;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private void EnqueueFailure(OverviewDbWorkItem item)
    {
        lock (_gate)
        {
            _failures.Enqueue(item);
        }
    }
}
