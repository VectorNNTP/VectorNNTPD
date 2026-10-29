using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Logging;

namespace VectorNNTPD.ReaderService.Tests.TestDoubles;

/// <summary>Captures Path-survey observations without pretending to persist them.</summary>
internal sealed class RecordingPathSurveyWriter : IPathSurveyWriter
{
    private readonly List<byte[]> _paths = [];
    private int _writeCalls;
    private int _flushCalls;

    public IReadOnlyList<byte[]> Paths
    {
        get
        {
            lock (_paths)
            {
                return _paths.ToArray();
            }
        }
    }

    public int WriteCalls => Volatile.Read(ref _writeCalls);

    public int FlushCalls => Volatile.Read(ref _flushCalls);

    public void Write(ReadOnlySpan<byte> canonicalPath)
    {
        Interlocked.Increment(ref _writeCalls);
        lock (_paths)
        {
            _paths.Add(canonicalPath.ToArray());
        }
    }

    public void Flush() => Interlocked.Increment(ref _flushCalls);
}

/// <summary>Counts Path-survey writes without retaining the observation stream.</summary>
internal sealed class CountingPathSurveyWriter : IPathSurveyWriter
{
    private int _writeCalls;
    private long _bytes;

    public int WriteCalls => Volatile.Read(ref _writeCalls);

    public long Bytes => Interlocked.Read(ref _bytes);

    public void Write(ReadOnlySpan<byte> canonicalPath)
    {
        Interlocked.Increment(ref _writeCalls);
        Interlocked.Add(ref _bytes, canonicalPath.Length);
    }

    public void Flush()
    {
    }
}

/// <summary>Path-survey writer that throws to prove already-accepted articles are not reprocessed.</summary>
internal sealed class ThrowingPathSurveyWriter : IPathSurveyWriter
{
    public int WriteCalls { get; private set; }

    public void Write(ReadOnlySpan<byte> canonicalPath)
    {
        WriteCalls++;
        throw new IOException("path survey writer test failure");
    }

    public void Flush()
    {
    }
}

/// <summary>Records completed Path-survey files handed off before gzip.</summary>
internal sealed class RecordingCompletedPathSurveyFileHandler : ICompletedPathSurveyFileHandler
{
    private readonly List<string> _completed = [];
    private readonly List<bool> _gzipExisted = [];

    public IReadOnlyList<string> Completed
    {
        get
        {
            lock (_completed)
            {
                return _completed.ToArray();
            }
        }
    }

    public IReadOnlyList<bool> GzipExistedAtHandoff
    {
        get
        {
            lock (_gzipExisted)
            {
                return _gzipExisted.ToArray();
            }
        }
    }

    public void OnCompletedFile(string completedFilePath)
    {
        var gzip = completedFilePath + ".gz";
        lock (_completed)
        {
            _gzipExisted.Add(File.Exists(gzip));
            _completed.Add(completedFilePath);
        }
    }
}

/// <summary>Completed-file handler that throws after recording the path.</summary>
internal sealed class ThrowingCompletedPathSurveyFileHandler : ICompletedPathSurveyFileHandler
{
    public string? Path { get; private set; }

    public void OnCompletedFile(string completedFilePath)
    {
        Path = completedFilePath;
        throw new InvalidOperationException("completed-file handler test failure");
    }
}
