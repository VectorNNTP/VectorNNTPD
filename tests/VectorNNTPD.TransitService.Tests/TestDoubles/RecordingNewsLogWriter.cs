using VectorNNTPD.TransitService.ArticleIngestion;

namespace VectorNNTPD.TransitService.Tests.TestDoubles;

/// <summary>Captures news events for post-queue ingress tests.</summary>
internal sealed class RecordingNewsLogWriter : INewsLogWriter
{
    private readonly List<NewsLogEvent> _events = [];
    private readonly TaskCompletionSource? _gate;
    private int _writeCalls;
    private int _flushCalls;

    public RecordingNewsLogWriter(Task? gate = null)
    {
        _gate = gate is null
            ? null
            : TaskCompletionSourceFrom(gate);
    }

    public IReadOnlyList<NewsLogEvent> Events
    {
        get
        {
            lock (_events)
            {
                return _events.ToArray();
            }
        }
    }

    public int WriteCalls => Volatile.Read(ref _writeCalls);

    public int FlushCalls => Volatile.Read(ref _flushCalls);

    public void Write(in NewsLogEvent evt)
    {
        if (_gate is not null)
        {
            _gate.Task.GetAwaiter().GetResult();
        }

        Interlocked.Increment(ref _writeCalls);
        lock (_events)
        {
            _events.Add(
                new NewsLogEvent(
                    evt.Disposition,
                    evt.MessageId.ToArray(),
                    evt.Feed.ToArray(),
                    evt.Sites.ToArray(),
                    evt.Timestamp,
                    evt.ResponseCode,
                    evt.Reason.ToArray(),
                    evt.Size));
        }
    }

    public void Flush() => Interlocked.Increment(ref _flushCalls);

    private static TaskCompletionSource TaskCompletionSourceFrom(Task gate)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.ContinueWith(
            static (completed, state) =>
            {
                var tcs = (TaskCompletionSource)state!;
                if (completed.IsFaulted)
                {
                    tcs.TrySetException(completed.Exception!.InnerExceptions);
                }
                else if (completed.IsCanceled)
                {
                    tcs.TrySetCanceled();
                }
                else
                {
                    tcs.TrySetResult();
                }
            },
            source,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return source;
    }
}

/// <summary>News writer that throws to prove already-accepted articles are not reprocessed.</summary>
internal sealed class ThrowingNewsLogWriter : INewsLogWriter
{
    public int WriteCalls { get; private set; }

    public void Write(in NewsLogEvent evt)
    {
        WriteCalls++;
        throw new IOException("news writer test failure");
    }

    public void Flush()
    {
    }
}
