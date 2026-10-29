using System.IO.Pipelines;
using VectorNNTPD.TransitService.Networking.Transport;

namespace VectorNNTPD.TransitService.Tests.Networking.Transport;

/// <summary>
/// Serializes the static TX-pipe pause experiment so tests do not leak into production defaults.
/// </summary>
[Collection(nameof(TxPipePauseExperimentTests))]
public sealed class TxPipePauseExperimentTests
{
    public TxPipePauseExperimentTests() => TxPipePauseExperiment.Clear();

    [Fact]
    public void Create_RemainsProduction64KiB_WhenExperimentIsSet()
    {
        try
        {
            TxPipePauseExperiment.Set(4 * 1024 * 1024);
            var input = NntpPipeOptions.Create();
            Assert.Equal(NntpPipeOptions.PauseWriterThreshold, input.PauseWriterThreshold);
            Assert.Equal(NntpPipeOptions.ResumeWriterThreshold, input.ResumeWriterThreshold);
            Assert.Equal(64 * 1024, input.PauseWriterThreshold);
            Assert.Equal(32 * 1024, input.ResumeWriterThreshold);
            Assert.Same(PipeScheduler.ThreadPool, input.ReaderScheduler);
            Assert.Same(PipeScheduler.ThreadPool, input.WriterScheduler);
        }
        finally
        {
            TxPipePauseExperiment.Clear();
        }
    }

    [Fact]
    public void CreateOutput_Default_IsProduction64KiB()
    {
        TxPipePauseExperiment.Clear();
        Assert.False(TxPipePauseExperiment.IsConfigured);
        var output = NntpPipeOptions.CreateOutput();
        Assert.Equal(64 * 1024, output.PauseWriterThreshold);
        Assert.Equal(32 * 1024, output.ResumeWriterThreshold);
        Assert.Same(PipeScheduler.Inline, output.ReaderScheduler);
        Assert.Same(PipeScheduler.Inline, output.WriterScheduler);
    }

    [Fact]
    public void CreateOutput_AppliesAllowlistedPause_AndHalfResume()
    {
        try
        {
            TxPipePauseExperiment.Set(4 * 1024 * 1024);
            Assert.True(TxPipePauseExperiment.TryGetEffective(out var pause, out var resume));
            Assert.Equal(4 * 1024 * 1024, pause);
            Assert.Equal(2 * 1024 * 1024, resume);

            var output = NntpPipeOptions.CreateOutput();
            Assert.Equal(4 * 1024 * 1024, output.PauseWriterThreshold);
            Assert.Equal(2 * 1024 * 1024, output.ResumeWriterThreshold);
            Assert.Equal(NntpPipeOptions.MinimumSegmentSize, output.MinimumSegmentSize);
            Assert.Same(PipeScheduler.Inline, output.ReaderScheduler);
            Assert.Same(PipeScheduler.Inline, output.WriterScheduler);
        }
        finally
        {
            TxPipePauseExperiment.Clear();
        }
    }

    [Fact]
    public void Set_64KiB_MatchesProductionThresholds()
    {
        try
        {
            TxPipePauseExperiment.Set(64 * 1024);
            Assert.True(TxPipePauseExperiment.IsConfigured);
            var output = NntpPipeOptions.CreateOutput();
            Assert.Equal(NntpPipeOptions.PauseWriterThreshold, output.PauseWriterThreshold);
            Assert.Equal(NntpPipeOptions.ResumeWriterThreshold, output.ResumeWriterThreshold);
            Assert.Same(PipeScheduler.Inline, output.ReaderScheduler);
            Assert.Same(PipeScheduler.Inline, output.WriterScheduler);
        }
        finally
        {
            TxPipePauseExperiment.Clear();
        }
    }

    [Fact]
    public void Set_RejectsValuesOutsideAllowlist()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TxPipePauseExperiment.Set(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TxPipePauseExperiment.Set(16 * 1024 * 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => TxPipePauseExperiment.Set(96 * 1024));
        Assert.False(TxPipePauseExperiment.IsConfigured);
        Assert.Equal(64 * 1024, NntpPipeOptions.CreateOutput().PauseWriterThreshold);
    }

    [Theory]
    [InlineData("64KiB", 64 * 1024)]
    [InlineData("128k", 128 * 1024)]
    [InlineData("256KiB", 256 * 1024)]
    [InlineData("512KiB", 512 * 1024)]
    [InlineData("1MiB", 1024 * 1024)]
    [InlineData("2m", 2 * 1024 * 1024)]
    [InlineData("4MiB", 4 * 1024 * 1024)]
    [InlineData("8MiB", 8 * 1024 * 1024)]
    [InlineData("4194304", 4 * 1024 * 1024)]
    public void TryParseAllowed_AcceptsSweepTokens(string token, long expected)
    {
        Assert.True(TxPipePauseExperiment.TryParseAllowed(token, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("16MiB")]
    [InlineData("96KiB")]
    [InlineData("")]
    public void TryParseAllowed_RejectsAccidentalValues(string token)
    {
        Assert.False(TxPipePauseExperiment.TryParseAllowed(token, out _));
    }

    [Fact]
    public async Task CreateOutput_4MiB_DoesNotBackpressureAfterOne64KiBChunk()
    {
        try
        {
            TxPipePauseExperiment.Set(4 * 1024 * 1024);
            var pipe = new Pipe(NntpPipeOptions.CreateOutput());
            WriteZeros(pipe.Writer, 64 * 1024);
            var flush = pipe.Writer.FlushAsync();
            Assert.True(flush.IsCompletedSuccessfully);
            pipe.Reader.AdvanceTo((await pipe.Reader.ReadAsync()).Buffer.End);
            await pipe.Writer.CompleteAsync();
            await pipe.Reader.CompleteAsync();
        }
        finally
        {
            TxPipePauseExperiment.Clear();
        }
    }

    [Fact]
    public async Task Create_Production64KiB_BackpressuresAfterOne64KiBChunk()
    {
        TxPipePauseExperiment.Clear();
        var pipe = new Pipe(NntpPipeOptions.Create());
        WriteZeros(pipe.Writer, 64 * 1024);
        var flush = pipe.Writer.FlushAsync();
        Assert.False(flush.IsCompleted);

        var read = await pipe.Reader.ReadAsync();
        pipe.Reader.AdvanceTo(read.Buffer.End);
        var result = await flush;
        Assert.False(result.IsCompleted);

        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    private static void WriteZeros(PipeWriter writer, int bytes)
    {
        var span = writer.GetSpan(bytes);
        span[..bytes].Clear();
        writer.Advance(bytes);
    }
}
