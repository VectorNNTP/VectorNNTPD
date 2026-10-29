using System.Buffers;
using System.IO.Pipelines;
using VectorNNTPD.ReaderService.Networking.Transport;

namespace VectorNNTPD.ReaderService.Tests.Networking.Transport;

/// <summary>Serializes static <see cref="TransportIoProbe"/> enable/disable so tests do not race.</summary>
[Collection(nameof(TransportIoSessionTests))]
public sealed class TransportIoSessionTests
{
    [Fact]
    public void CreateSession_WhenDisabled_ReturnsNull()
    {
        TransportIoProbe.Disable();
        Assert.False(TransportIoProbe.IsEnabled);
        Assert.Null(TransportIoProbe.CreateSession());
    }

    [Fact]
    public void RecordReceive_AggregatesBytesAndSyncCounts()
    {
        var session = new TransportIoSession();
        var ts = System.Diagnostics.Stopwatch.GetTimestamp();
        session.RecordReceive(4096, 4096, ts, ts, sync: true);
        session.RecordReceive(4096, 2048, ts, ts, sync: false);

        var snap = session.Snapshot();
        Assert.Equal(2, snap.Rx.Ops);
        Assert.Equal(6144, snap.Rx.Bytes);
        Assert.Equal(8192, snap.Rx.Requested);
        Assert.Equal(2048, snap.Rx.MinBytes);
        Assert.Equal(4096, snap.Rx.MaxBytes);
        Assert.Equal(1, snap.Rx.Sync);
        Assert.Equal(1, snap.Rx.Async);
        Assert.Equal(2, snap.Rx.OpUs.Length);
    }

    [Fact]
    public void RecordSend_AggregatesAndReportsPercentiles()
    {
        var session = new TransportIoSession();
        var ts = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < 10; i++)
        {
            session.RecordSend(65536, 65536, ts, ts, sync: true);
        }

        var snap = session.Snapshot();
        Assert.Equal(10, snap.Tx.Ops);
        Assert.Equal(655360, snap.Tx.Bytes);
        Assert.Equal(65536, snap.Tx.MinBytes);
        Assert.Equal(65536, snap.Tx.MaxBytes);
        Assert.Equal(10, snap.Tx.Sync);
        Assert.Equal(0, snap.Tx.Async);
        var stats = TransportIoReport.Stats(snap.Tx.OpUs);
        Assert.True(stats.P50 >= 0);
        Assert.True(stats.P99 >= stats.P50);
        Assert.True(stats.Max >= stats.P99);
    }

    [Fact]
    public void Percentile_KnownSeries()
    {
        var samples = new[] { 10, 20, 30, 40, 50 };
        Assert.Equal(10, TransportIoReport.Percentile(samples, 0));
        Assert.Equal(30, TransportIoReport.Percentile(samples, 0.5));
        Assert.Equal(50, TransportIoReport.Percentile(samples, 1));
        Assert.Equal(0, TransportIoReport.Percentile([], 0.5));
    }

    [Fact]
    public void ExcessOperations_UpdateCounters_ButCapSamples()
    {
        var session = new TransportIoSession();
        var ts = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < TransportIoSession.SampleCapacity + 25; i++)
        {
            session.RecordReceive(100, 100, ts, ts, sync: true);
        }

        var snap = session.Snapshot();
        Assert.Equal(TransportIoSession.SampleCapacity + 25, snap.Rx.Ops);
        Assert.Equal(TransportIoSession.SampleCapacity, snap.Rx.OpUs.Length);
        Assert.Equal((TransportIoSession.SampleCapacity + 25) * 100L, snap.Rx.Bytes);
    }

    [Fact]
    public void RecordTxReadShape_SingleSegment()
    {
        var session = new TransportIoSession();
        session.RecordTxReadShape(new ReadOnlySequence<byte>(new byte[65536]));
        session.RecordTxReadShape(ReadOnlySequence<byte>.Empty);

        var snap = session.Snapshot();
        Assert.Equal(1, snap.TxRead.Reads);
        Assert.Equal(1, snap.TxRead.EmptyReads);
        Assert.Equal(65536, snap.TxRead.Bytes);
        Assert.Equal(1, snap.TxRead.Segments);
        Assert.Equal(65536, snap.TxRead.MinBytes);
        Assert.Equal(65536, snap.TxRead.MaxBytes);
        Assert.Equal(1, snap.TxRead.MinSegments);
        Assert.Equal(1, snap.TxRead.MaxSegments);
        Assert.Equal(65536, snap.TxRead.MinSegmentBytes);
        Assert.Equal(65536, snap.TxRead.MaxSegmentBytes);
    }

    [Fact]
    public async Task RecordTxReadShape_MultiSegmentFromPipe()
    {
        var options = new PipeOptions(
            pauseWriterThreshold: 4 * 1024 * 1024,
            resumeWriterThreshold: 2 * 1024 * 1024,
            minimumSegmentSize: 4 * 1024,
            useSynchronizationContext: false);
        var pipe = new Pipe(options);
        var chunk = new byte[65536];
        for (var i = 0; i < 4; i++)
        {
            var memory = pipe.Writer.GetMemory(chunk.Length);
            chunk.CopyTo(memory);
            pipe.Writer.Advance(chunk.Length);
            _ = await pipe.Writer.FlushAsync();
        }

        var result = await pipe.Reader.ReadAsync();
        var session = new TransportIoSession();
        session.RecordTxReadShape(result.Buffer);
        pipe.Reader.AdvanceTo(result.Buffer.End);
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();

        var snap = session.Snapshot();
        Assert.Equal(1, snap.TxRead.Reads);
        Assert.Equal(4 * 65536, snap.TxRead.Bytes);
        Assert.True(snap.TxRead.Segments >= 4, "expected at least one segment per 64 KiB flush");
        Assert.Equal(snap.TxRead.Segments, snap.TxRead.MaxSegments);
        Assert.True(snap.TxRead.MaxSegmentBytes >= 65536 || snap.TxRead.Segments > 4);
    }

    [Fact]
    public void Write_ProducesReportFile()
    {
        var session = new TransportIoSession();
        var ts = System.Diagnostics.Stopwatch.GetTimestamp();
        session.RecordSend(1024, 1024, ts, ts, sync: true);
        var dir = Path.Join(Path.GetTempPath(), "vectornntp-transport-io-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = session.Write(dir, "198.18.0.66:1234");
            Assert.True(File.Exists(path));
            var text = File.ReadAllText(path);
            Assert.Contains("TX (stream WriteAsync)", text, StringComparison.Ordinal);
            Assert.Contains("operations:", text, StringComparison.Ordinal);
            Assert.Contains("TX SendAsync loop stages", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void EnableDisable_TogglesSessionCreation()
    {
        var dir = Path.Join(Path.GetTempPath(), "vectornntp-transport-io-" + Guid.NewGuid().ToString("N"));
        try
        {
            TransportIoProbe.Enable(dir);
            Assert.True(TransportIoProbe.IsEnabled);
            Assert.NotNull(TransportIoProbe.CreateSession());
        }
        finally
        {
            TransportIoProbe.Disable();
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        Assert.False(TransportIoProbe.IsEnabled);
        Assert.Null(TransportIoProbe.CreateSession());
    }

    [Fact]
    public void RecordSendLoopStages_AccumulateWithoutPerOpObjects()
    {
        var session = new TransportIoSession();
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        session.RecordSendLoopStart();
        session.RecordReadToFirstWrite(t0, t0 + 1000);
        session.RecordBetweenWrites(t0, t0 + 500);
        session.RecordTransportWrite(t0, t0 + 2000);
        session.RecordLastWriteToAdvance(t0, t0 + 300);
        session.RecordAdvanceTo(t0, t0 + 100);
        session.RecordAdvanceToNextRead(t0, t0 + 400);
        session.RecordSendLoopEnd();

        var snap = session.Snapshot();
        Assert.Equal(1, snap.Loop.AdvanceCount);
        Assert.Equal(1, snap.Loop.BetweenWriteCount);
        Assert.Equal(1, snap.Loop.TransportWriteCount);
        Assert.True(snap.Loop.SendAsyncElapsed >= TimeSpan.Zero);
        Assert.True(snap.Loop.AdvanceTo >= TimeSpan.Zero);
        Assert.True(snap.Loop.TransportWrite >= TimeSpan.Zero);
        _ = Assert.Single(snap.Loop.AdvanceUs);
        var text = TransportIoReport.Format(snap, "loop-test");
        Assert.Contains("AdvanceTo calls:", text, StringComparison.Ordinal);
        Assert.Contains("ReadAsync waiting:", text, StringComparison.Ordinal);
        Assert.Contains("TX write coalesce:", text, StringComparison.Ordinal);
        Assert.Contains("TX write granularity", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordCopy_AggregatesBytesAndDuration()
    {
        var session = new TransportIoSession { WriteGranularityTargetBytes = 256 * 1024 };
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        session.RecordCopy(65536, t0, t0 + 1000);
        session.RecordCopy(65536, t0, t0 + 2000);

        var snap = session.Snapshot();
        Assert.Equal(256 * 1024, snap.Coalesce.TargetBytes);
        Assert.Equal(2, snap.Coalesce.CopyCount);
        Assert.Equal(131072, snap.Coalesce.CopyBytes);
        Assert.True(snap.Coalesce.CopyTime >= TimeSpan.Zero);
        Assert.Equal(2, snap.Coalesce.CopyUs.Length);
        var text = TransportIoReport.Format(snap, "copy-test");
        Assert.Contains("copy bytes:", text, StringComparison.Ordinal);
        Assert.Contains("target:", text, StringComparison.Ordinal);
    }
}
