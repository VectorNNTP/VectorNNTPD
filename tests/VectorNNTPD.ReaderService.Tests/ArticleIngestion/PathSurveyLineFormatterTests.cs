using System.Text;
using VectorNNTPD.ReaderService.ArticleIngestion;

namespace VectorNNTPD.ReaderService.Tests.ArticleIngestion;

/// <summary>Exact INN-style Path-survey line from canonical ArticleRecord.Path bytes.</summary>
public sealed class PathSurveyLineFormatterTests
{
    [Fact]
    public void Write_CopiesCanonicalPathBytes_WithPrefixAndLf()
    {
        var path = "news.usenet.ninja!nntpd01.usenet.ninja!peer.example"u8;
        var buffer = new byte[PathSurveyLineFormatter.RequiredLength(path)];
        var written = PathSurveyLineFormatter.Write(buffer, path);
        Assert.Equal(buffer.Length, written);
        Assert.Equal("Path: news.usenet.ninja!nntpd01.usenet.ninja!peer.example\n", Encoding.Latin1.GetString(buffer));
    }

    [Fact]
    public void Write_PreservesExactPathBytes_WithoutInventingHops()
    {
        var path = "foo!bar!not-for-mail"u8;
        var buffer = new byte[PathSurveyLineFormatter.RequiredLength(path)];
        PathSurveyLineFormatter.Write(buffer, path);
        var line = Encoding.Latin1.GetString(buffer);
        Assert.Equal("Path: foo!bar!not-for-mail\n", line);
        Assert.DoesNotContain("giganews", line, StringComparison.Ordinal);
        Assert.DoesNotContain("nntpd01", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_EmptyPath_FollowsArticleRecordEmptySpanSemantics()
    {
        var buffer = new byte[PathSurveyLineFormatter.RequiredLength([])];
        var written = PathSurveyLineFormatter.Write(buffer, []);
        Assert.Equal(7, written);
        Assert.Equal("Path: \n", Encoding.Latin1.GetString(buffer));
    }

    [Fact]
    public void Write_MultipleCalls_AreIndependentSequentialRecords()
    {
        using var stream = new MemoryStream();
        WriteOne(stream, "alpha!one"u8);
        WriteOne(stream, "beta!two"u8);
        Assert.Equal("Path: alpha!one\nPath: beta!two\n", Encoding.Latin1.GetString(stream.ToArray()));
    }

    [Fact]
    public void Write_LargeSequentialInput_DoesNotMaterializeTheCompleteStream()
    {
        var path = "hop.example!not-for-mail"u8;
        var required = PathSurveyLineFormatter.RequiredLength(path);
        var buffer = new byte[required];
        const int count = 20_000;
        using var stream = new FileStream(
            Path.Join(Path.GetTempPath(), "vectornntp-inpaths-seq-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.DeleteOnClose);
        for (var i = 0; i < count; i++)
        {
            var written = PathSurveyLineFormatter.Write(buffer, path);
            stream.Write(buffer.AsSpan(0, written));
        }

        Assert.Equal((long)required * count, stream.Length);
    }

    private static void WriteOne(Stream stream, ReadOnlySpan<byte> path)
    {
        var buffer = new byte[PathSurveyLineFormatter.RequiredLength(path)];
        var written = PathSurveyLineFormatter.Write(buffer, path);
        stream.Write(buffer.AsSpan(0, written));
    }
}
