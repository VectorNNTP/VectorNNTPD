using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Logging;

namespace VectorNNTPD.ReaderService.Tests.Logging;

public sealed class PathSurveyCompletedFileTests
{
    [Fact]
    public void CompletedFile_IsExposedToHandler_BeforeGzip_AndOriginalBytesAreInTheArchive()
    {
        var dir = CreateTempLogDir();
        try
        {
            var handler = new RecordingCompletedPathSurveyFileHandler();
            using (var writer = new SerilogPathSurveyWriter(
                       Configuration(dir, retention: 5, sizeLimit: 80),
                       handler,
                       NullLogger<SerilogPathSurveyWriter>.Instance))
            {
                for (var i = 0; i < 8; i++)
                {
                    writer.Write("aaaaaaaaaaaaaaaaaaaaaaaa"u8);
                }
            }

            Assert.NotEmpty(handler.Completed);
            Assert.All(handler.GzipExistedAtHandoff, static existed => Assert.False(existed));
            Assert.All(handler.Completed, static path => Assert.False(File.Exists(path)));
            var archives = Directory.GetFiles(dir, "inpaths*.log.gz");
            Assert.NotEmpty(archives);
            Assert.NotEmpty(Directory.GetFiles(dir, "inpaths*.log"));
            using var gz = new GZipStream(File.OpenRead(archives[0]), CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            Assert.Contains("Path: ", reader.ReadToEnd(), StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void HandlerFailure_DoesNotSkipGzip_AndDoesNotDropTheCompletedBytes()
    {
        var dir = CreateTempLogDir();
        try
        {
            var handler = new ThrowingCompletedPathSurveyFileHandler();
            using (var writer = new SerilogPathSurveyWriter(
                       Configuration(dir, retention: 5, sizeLimit: 80),
                       handler,
                       NullLogger<SerilogPathSurveyWriter>.Instance))
            {
                for (var i = 0; i < 8; i++)
                {
                    writer.Write("bbbbbbbbbbbbbbbbbbbbbbbb"u8);
                }
            }

            Assert.False(string.IsNullOrEmpty(handler.Path));
            Assert.False(File.Exists(handler.Path));
            var archive = handler.Path + ".gz";
            Assert.True(File.Exists(archive));
            using var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8);
            Assert.Contains("Path: ", reader.ReadToEnd(), StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void NewsRoll_CompressesTheCompletedFile_WithoutAPathSurveyHandler()
    {
        var dir = CreateTempLogDir();
        try
        {
            using (var writer = new SerilogNewsLogWriter(NewsConfiguration(dir)))
            {
                writer.Write(new NewsLogEvent(NewsLogDisposition.Accepted, "<a@example>"u8.ToArray()));
                writer.Write(new NewsLogEvent(NewsLogDisposition.Accepted, "<b@example>"u8.ToArray()));
            }

            var archives = Directory.GetFiles(dir, "news*.log.gz");
            Assert.NotEmpty(archives);
            Assert.All(archives, static archive => Assert.False(File.Exists(archive[..^3])));
            Assert.Contains(Directory.GetFiles(dir), static path => path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase));
            using var gz = new GZipStream(File.OpenRead(archives[0]), CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            var text = reader.ReadToEnd();
            Assert.Contains("<a@example>", text, StringComparison.Ordinal);
            Assert.DoesNotContain("<b@example>", text, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private static IConfiguration Configuration(string dir, int retention, long sizeLimit) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Serilog:Inpaths:path"] = Path.Join(dir, "inpaths-.log"),
            ["Serilog:Inpaths:rollingInterval"] = "Infinite",
            ["Serilog:Inpaths:retainedFileCountLimit"] = retention.ToString(),
            ["Serilog:Inpaths:buffered"] = "false",
            ["Serilog:Inpaths:rollOnFileSizeLimit"] = "true",
            ["Serilog:Inpaths:fileSizeLimitBytes"] = sizeLimit.ToString(),
        }).Build();

    private static IConfiguration NewsConfiguration(string dir) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Serilog:News:path"] = Path.Join(dir, "news-.log"),
            ["Serilog:News:rollingInterval"] = "Infinite",
            ["Serilog:News:retainedFileCountLimit"] = "5",
            ["Serilog:News:buffered"] = "false",
            ["Serilog:News:rollOnFileSizeLimit"] = "true",
            ["Serilog:News:fileSizeLimitBytes"] = "1",
        }).Build();

    private static string CreateTempLogDir()
    {
        var dir = Path.Join(Path.GetTempPath(), "vectornntp-inpaths-completed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        // codeql[cs/catch-of-all-exceptions] Best-effort test cleanup must tolerate any filesystem or disposal failure.
        catch (Exception ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }
    }
}
