using VectorNNTPD.Common.Core;

namespace VectorNNTPD.ReaderService.Tests.TestDoubles;

internal sealed class FakeApplicationService : IApplicationService
{
    private readonly Func<CancellationToken, Task>? _onStart;
    private readonly Func<CancellationToken, Task>? _onStop;
    private readonly TaskCompletionSource? _executionTcs;
    private int _startCount;
    private int _stopCount;
    private int _stopAttemptCount;

    public FakeApplicationService(
        string name,
        Func<CancellationToken, Task>? onStart = null,
        Func<CancellationToken, Task>? onStop = null,
        bool withExecution = false)
    {
        Name = name;
        _onStart = onStart;
        _onStop = onStop;
        if (withExecution)
        {
            _executionTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public string Name { get; }

    public Task? Execution => _executionTcs?.Task;

    public int StartCount => Volatile.Read(ref _startCount);

    public int StopCount => Volatile.Read(ref _stopCount);

    /// <summary>Number of times <see cref="StopAsync"/> was entered, including canceled/aborted attempts.</summary>
    public int StopAttemptCount => Volatile.Read(ref _stopAttemptCount);

    public List<string> SharedStartOrder { get; set; } = [];

    public List<string> SharedStopOrder { get; set; } = [];

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource StopGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool HoldStopUntilReleased { get; set; }

    /// <summary>
    /// When set with <see cref="HoldStopUntilReleased"/>, stop waits on <see cref="StopGate"/> without
    /// observing the cancellation token (non-cooperative stop).
    /// </summary>
    public bool IgnoreStopCancellation { get; set; }

    public void CompleteExecution(Exception? exception = null)
    {
        if (_executionTcs is null)
        {
            throw new InvalidOperationException("Execution was not enabled for this fake.");
        }

        if (exception is null)
        {
            _executionTcs.TrySetResult();
        }
        else
        {
            _executionTcs.TrySetException(exception);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_onStart is not null)
        {
            await _onStart(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _startCount);
        SharedStartOrder.Add(Name);
        Started.TrySetResult();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stopAttemptCount);
        StopEntered.TrySetResult();

        if (HoldStopUntilReleased)
        {
            if (IgnoreStopCancellation)
            {
                await StopGate.Task.ConfigureAwait(false);
            }
            else
            {
                await StopGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (_onStop is not null)
        {
            await _onStop(cancellationToken).ConfigureAwait(false);
        }

        if (!IgnoreStopCancellation)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        Interlocked.Increment(ref _stopCount);
        SharedStopOrder.Add(Name);
    }
}
