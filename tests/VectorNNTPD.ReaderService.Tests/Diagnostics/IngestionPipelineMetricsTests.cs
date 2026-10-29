using System.Diagnostics;
using VectorNNTPD.ReaderService.Diagnostics;

namespace VectorNNTPD.ReaderService.Tests.Diagnostics;

/// <summary>Bucketed ingestion-pipeline histogram contracts used by one-minute telemetry.</summary>
public sealed class IngestionPipelineMetricsTests
{
    [Fact]
    public void CaptureInterval_ReportsCountAverageAndP95()
    {
        var metrics = new IngestionPipelineMetrics();
        for (var i = 0; i < 94; i++)
        {
            metrics.RecordNews(TimeSpan.FromMilliseconds(1));
        }

        for (var i = 0; i < 6; i++)
        {
            metrics.RecordNews(TimeSpan.FromMilliseconds(100));
        }

        var snapshot = metrics.CaptureInterval();
        Assert.Equal(100, snapshot.News.Count);
        Assert.Equal(100, snapshot.News.P95Ms);
        Assert.InRange(snapshot.News.AvgMs, 4, 12);
        Assert.True(snapshot.News.MaxMs >= 90);

        var empty = metrics.CaptureInterval();
        Assert.Equal(0, empty.News.Count);
        Assert.Equal(0, empty.News.P95Ms);
    }

    [Fact]
    public void ConfirmBusyPercent_IsConfirmSumOverBusyTime()
    {
        var metrics = new IngestionPipelineMetrics();
        var confirmTicks = (long)(0.040 * Stopwatch.Frequency);
        RecordAgo(metrics.RecordPublishAndConfirm, TimeSpan.FromMilliseconds(40));
        metrics.AddBusyTicks((long)(0.100 * Stopwatch.Frequency));
        metrics.AddIdleTicks((long)(0.100 * Stopwatch.Frequency));
        metrics.RecordWorkerItem(Stopwatch.GetTimestamp() - confirmTicks);

        var snapshot = metrics.CaptureInterval();
        Assert.Equal(1, snapshot.WorkerItems);
        Assert.InRange(snapshot.BusyPercent, 40, 60);
        Assert.InRange(snapshot.ConfirmBusyPercent, 30, 50);
        Assert.Equal(1.0, snapshot.ArticlesPerSecond(TimeSpan.FromSeconds(1)), precision: 6);
    }

    private static void RecordAgo(Action<long> record, TimeSpan ago)
    {
        var start = Stopwatch.GetTimestamp() - (long)(ago.TotalSeconds * Stopwatch.Frequency);
        record(start);
    }
}
