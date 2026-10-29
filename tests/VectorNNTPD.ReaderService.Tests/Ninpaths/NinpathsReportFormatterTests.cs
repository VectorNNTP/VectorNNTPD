using System.Text;
using VectorNNTPD.ReaderService.Ninpaths;

namespace VectorNNTPD.ReaderService.Tests.Ninpaths;

public sealed class NinpathsReportFormatterTests
{
    [Fact]
    public void Format_EmptyTraffic_ReturnsEmptyBuffer()
    {
        var stats = Read("");
        Assert.Empty(NinpathsReportFormatter.Format(stats, 1, 2, 2));
    }

    [Fact]
    public void Format_WritesInn311CompactDump()
    {
        var stats = Read("Path: alpha!beta!omega\nPath: alpha!beta!omega\n");
        var dump = Encoding.Latin1.GetString(NinpathsReportFormatter.Format(stats, 100, 200, 200));
        Assert.StartsWith("!!NINP 3.1.1 100 200 2 2 200\n", dump, StringComparison.Ordinal);
        Assert.Contains("!!NLREC\n", dump, StringComparison.Ordinal);
        Assert.EndsWith("!!NLEND 1\n", dump, StringComparison.Ordinal);
        Assert.Contains("alpha 2", dump, StringComparison.Ordinal);
        Assert.Contains("beta 2", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("omega", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("nntpd", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("ZCZC", dump, StringComparison.Ordinal);

        Assert.True(stats.TryGetSentTo("alpha", out _));
        var alpha = stats.Find("alpha"u8)!;
        var beta = stats.Find("beta"u8)!;
        Assert.Contains(
            $":{alpha.Number}!{beta.Number},2",
            dump,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Format_OmitsCountWhenTallyIsOne()
    {
        var stats = Read("Path: left!right!end\n");
        var dump = Encoding.Latin1.GetString(NinpathsReportFormatter.Format(stats, 1, 2, 2));
        var left = stats.Find("left"u8)!;
        var right = stats.Find("right"u8)!;
        Assert.Contains($":{left.Number}!{right.Number}", dump, StringComparison.Ordinal);
        Assert.DoesNotContain($":{left.Number}!{right.Number},", dump, StringComparison.Ordinal);
        Assert.EndsWith("!!NLEND 1\n", dump, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_NumbersSitesInInnHashWalkOrder()
    {
        var stats = Read("Path: a!b!z\nPath: c!d!z\n");
        _ = NinpathsReportFormatter.Format(stats, 1, 2, 2);
        long expected = 0;
        for (var i = 0; i < stats.Buckets.Length; i++)
        {
            for (var site = stats.Buckets[i]; site is not null; site = site.BucketNext)
            {
                Assert.Equal(expected, site.Number);
                expected++;
            }
        }

        Assert.Equal(stats.SiteCount, expected);
    }

    [Fact]
    public void Format_RelationOrderIsInnPrependOrder()
    {
        var stats = Read("Path: hub!first!end\nPath: hub!second!end\n");
        var dump = Encoding.Latin1.GetString(NinpathsReportFormatter.Format(stats, 1, 2, 2));
        var hub = stats.Find("hub"u8)!;
        var first = stats.Find("first"u8)!;
        var second = stats.Find("second"u8)!;
        var expected = $":{hub.Number}!{second.Number}!{first.Number}";
        Assert.Contains(expected, dump, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_DoesNotRereadTheSource()
    {
        var stats = Read("Path: a!b!c\n");
        var first = NinpathsReportFormatter.Format(stats, 5, 6, 6);
        var second = NinpathsReportFormatter.Format(stats, 5, 6, 6);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ReportPeriod_UsesLocalMidnightFromInpathsFileName()
    {
        var time = new UtcTimeProvider(new DateTimeOffset(2026, 9, 28, 1, 0, 0, TimeSpan.Zero));
        var (start, end, average) = NinpathsReportPeriod.Resolve(
            Path.Join("logs", "inpaths-20260927.log"),
            time);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            start);
        Assert.Equal(time.GetUtcNow().ToUnixTimeSeconds(), end);
        Assert.Equal(end, average);
    }

    [Fact]
    public void ReportPeriod_UnknownFileName_UsesDumpTimeForAllFields()
    {
        var time = new UtcTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var unix = time.GetUtcNow().ToUnixTimeSeconds();
        var (start, end, average) = NinpathsReportPeriod.Resolve("not-a-survey.txt", time);
        Assert.Equal(unix, start);
        Assert.Equal(unix, end);
        Assert.Equal(unix, average);
    }

    [Fact]
    public void HeaderFields_AreSpaceSeparatedInInnOrder()
    {
        var stats = Read("Path: a!b!c\n");
        var dump = Encoding.Latin1.GetString(NinpathsReportFormatter.Format(stats, 10, 20, 15));
        var header = dump.Split('\n')[0].Split(' ');
        Assert.Equal(["!!NINP", "3.1.1", "10", "20", "2", "1", "15"], header);
    }

    private static NinpathsStatistics Read(string text)
    {
        using var stream = new MemoryStream(Encoding.Latin1.GetBytes(text), writable: false);
        return NinpathsLogReader.Read(stream);
    }

    private sealed class UtcTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utc;

        public UtcTimeProvider(DateTimeOffset utc) => _utc = utc;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => _utc;
    }
}
