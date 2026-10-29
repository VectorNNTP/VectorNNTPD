using System.Diagnostics;
using VectorNNTPD.TransitService.Session;

namespace VectorNNTPD.TransitService.Tests.Session;

public sealed class TakeThisStageProbeTests
{
    [Fact]
    public void Default_IsDisabled()
    {
        Assert.False(TakeThisStageProbe.IsEnabled);
        Assert.Null(TakeThisStageProbe.CreateSessionLog());
    }

    [Fact]
    public void Percentile_IsMonotonic()
    {
        var values = new double[] { 10, 20, 30, 40, 50 };
        Assert.Equal(10, TakeThisStageReport.Percentile(values, 0));
        Assert.Equal(30, TakeThisStageReport.Percentile(values, 0.5));
        Assert.Equal(50, TakeThisStageReport.Percentile(values, 1));
        Assert.True(TakeThisStageReport.Percentile(values, 0.95) >= TakeThisStageReport.Percentile(values, 0.50));
    }

    [Fact]
    public void Intervals_ClassifyReceiveAsDominant_WhenReceiveIsLargest()
    {
        var origin = Stopwatch.GetTimestamp();
        var freq = Stopwatch.Frequency;
        long AtMs(double ms) => origin + (long)(ms / 1000.0 * freq);

        var sample = new TakeThisStageSample(
            CommandParsedTs: origin,
            PeekStartTs: origin,
            PeekCompleteTs: AtMs(0.2),
            ReceiveStartTs: origin,
            ReceiveCompleteTs: AtMs(1.0),
            EmitStartTs: AtMs(1.05),
            EnqueueStartTs: AtMs(1.06),
            EnqueueAcceptedTs: AtMs(1.08),
            RememberStartTs: AtMs(1.08),
            RememberCompleteTs: AtMs(1.09),
            ReplyEnqueueStartTs: AtMs(1.09),
            ReplyEnqueueCompleteTs: AtMs(1.12),
            OccupiedAtEmit: 3,
            PeakOccupied: 8,
            ArticleBytes: 1000,
            Outcome: "unseen");

        var intervals = TakeThisStageReport.Intervals(sample);
        Assert.Equal("article receive", intervals.Dominant);
        Assert.True(intervals.ReceiveUs > intervals.PeekUs);
        Assert.True(intervals.OverlapUs >= intervals.ReceiveUs - 1);
    }

    [Fact]
    public void Report_IncludesRequestedIntervals()
    {
        var origin = Stopwatch.GetTimestamp();
        var sample = new TakeThisStageSample(
            origin,
            origin,
            origin + Stopwatch.Frequency / 1000,
            origin,
            origin + Stopwatch.Frequency / 500,
            origin + Stopwatch.Frequency / 400,
            origin + Stopwatch.Frequency / 400,
            origin + Stopwatch.Frequency / 350,
            origin + Stopwatch.Frequency / 350,
            origin + Stopwatch.Frequency / 340,
            origin + Stopwatch.Frequency / 340,
            origin + Stopwatch.Frequency / 300,
            OccupiedAtEmit: 2,
            PeakOccupied: 4,
            ArticleBytes: 64,
            Outcome: "unseen");

        var text = TakeThisStageReport.Format([sample], peakOccupied: 4, "test", csvPath: null);
        Assert.Contains("HistoryDB Peek", text, StringComparison.Ordinal);
        Assert.Contains("article receive", text, StringComparison.Ordinal);
        Assert.Contains("queue EnqueueAsync", text, StringComparison.Ordinal);
        Assert.Contains("HistoryDB Remember", text, StringComparison.Ordinal);
        Assert.Contains("239 Channel enqueue", text, StringComparison.Ordinal);
        Assert.Contains("ready → emit (order)", text, StringComparison.Ordinal);
        Assert.Contains("Peak occupied", text, StringComparison.Ordinal);
    }
}
