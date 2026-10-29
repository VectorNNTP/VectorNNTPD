using VectorNNTPD.ReaderService.Hosting.Systemd;

namespace VectorNNTPD.ReaderService.Tests.Hosting.Systemd;

public sealed class SystemdWatchdogIntervalTests
{
    [Fact]
    public void Calculate_UsesConfiguredFraction_AndLeavesMargin()
    {
        var interval = SystemdWatchdogInterval.Calculate(TimeSpan.FromSeconds(10), fraction: 0.5);

        Assert.Equal(TimeSpan.FromSeconds(5), interval);
        Assert.True(interval < TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void Calculate_RejectsInvalidFraction(double fraction)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SystemdWatchdogInterval.Calculate(TimeSpan.FromSeconds(10), fraction));
    }

    [Fact]
    public void Calculate_RejectsNonPositiveTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SystemdWatchdogInterval.Calculate(TimeSpan.Zero));
    }
}
