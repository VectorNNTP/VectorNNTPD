using VectorNNTPD.ReaderService.Networking.Transport;

namespace VectorNNTPD.ReaderService.Tests.Networking.Transport;

/// <summary>
/// Serializes process-wide write-granularity experiment state. These tests do not create
/// <see cref="NntpConnection"/> instances while <see cref="TxWriteGranularityExperiment.Set"/>
/// is active.
/// </summary>
[Collection(nameof(TxWriteGranularityExperimentTests))]
public sealed class TxWriteGranularityExperimentTests
{
    public TxWriteGranularityExperimentTests() => TxWriteGranularityExperiment.Clear();

    [Fact]
    public void Default_IsInactive()
    {
        TxWriteGranularityExperiment.Clear();
        Assert.False(TxWriteGranularityExperiment.IsConfigured);
        Assert.Null(TxWriteGranularityExperiment.ConfiguredTargetBytes);
        Assert.False(TxWriteGranularityExperiment.TryGetTarget(out var target));
        Assert.Equal(0, target);
        Assert.False(TxWriteGranularityExperiment.TryResolveTarget(transport: null, out _));
    }

    [Theory]
    [InlineData(64 * 1024)]
    [InlineData(128 * 1024)]
    [InlineData(256 * 1024)]
    [InlineData(512 * 1024)]
    [InlineData(1024 * 1024)]
    [InlineData(4 * 1024 * 1024)]
    public void Set_AllowlistedTargets_AreResolved(int targetBytes)
    {
        try
        {
            TxWriteGranularityExperiment.Set(targetBytes);
            Assert.True(TxWriteGranularityExperiment.IsConfigured);
            Assert.True(TxWriteGranularityExperiment.TryGetTarget(out var resolved));
            Assert.Equal(targetBytes, resolved);
            Assert.Equal(targetBytes, TxWriteGranularityExperiment.ConfiguredTargetBytes);
        }
        finally
        {
            TxWriteGranularityExperiment.Clear();
        }
    }

    [Fact]
    public void Set_RejectsValuesOutsideAllowlist()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TxWriteGranularityExperiment.Set(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TxWriteGranularityExperiment.Set(2 * 1024 * 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => TxWriteGranularityExperiment.Set(8 * 1024 * 1024));
        Assert.False(TxWriteGranularityExperiment.IsConfigured);
    }

    [Theory]
    [InlineData("64KiB", 64 * 1024)]
    [InlineData("128k", 128 * 1024)]
    [InlineData("256KiB", 256 * 1024)]
    [InlineData("512KiB", 512 * 1024)]
    [InlineData("1MiB", 1024 * 1024)]
    [InlineData("4MiB", 4 * 1024 * 1024)]
    [InlineData("4194304", 4 * 1024 * 1024)]
    public void TryParseAllowed_AcceptsSweepTokens(string token, int expected)
    {
        Assert.True(TxWriteGranularityExperiment.TryParseAllowed(token, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("2MiB")]
    [InlineData("8MiB")]
    [InlineData("96KiB")]
    [InlineData("")]
    public void TryParseAllowed_RejectsAccidentalValues(string token)
    {
        Assert.False(TxWriteGranularityExperiment.TryParseAllowed(token, out _));
    }
}
