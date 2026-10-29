using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Logging;

namespace VectorNNTPD.ReaderService.Tests.ArticleIngestion;

public sealed class SerilogPathSurveyWriterTests
{
    [Fact]
    public void ReadSettings_HonoursConfiguredOperationalProperties()
    {
        using var dir = new TempLogDir();
        var configuration = PathSurveyTestConfiguration.Create(
            dir.Path,
            rollingInterval: "Hour",
            retainedFileCountLimit: 1,
            buffered: false,
            fileSizeLimitBytes: 1_048_576,
            rollOnFileSizeLimit: true,
            bufferSize: 250,
            blockWhenFull: false);
        var settings = NntpdPathSurveyLogging.ReadSettings(configuration);
        Assert.Equal(Path.Join(dir.Path, "inpaths-.log"), settings.Path);
        Assert.Equal(RollingInterval.Hour, settings.RollingInterval);
        Assert.Equal(1, settings.RetainedFileCountLimit);
        Assert.False(settings.Buffered);
        Assert.True(settings.RollOnFileSizeLimit);
        Assert.Equal(1_048_576, settings.FileSizeLimitBytes);
        Assert.Equal(250, settings.BufferSize);
        Assert.False(settings.BlockWhenFull);
        Assert.Null(settings.FlushToDiskInterval);
    }

    [Fact]
    public void BindResolvedPathSurveyPath_UsesLogDir_AndDoesNotExposeOutputTemplate()
    {
        using var dir = new TempLogDir();
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nntpd:LogDir"] = dir.Path,
            ["Serilog:Inpaths:path"] = "logs/inpaths-.log",
            ["Serilog:Inpaths:outputTemplate"] = "HACK {Message}",
        });
        NntpdPathSurveyLogging.BindResolvedPathSurveyPath(configuration, dir.Path);
        Assert.Equal(NntpdPathSurveyLogging.PathSurveyRollingFilePath(dir.Path), configuration["Serilog:Inpaths:path"]);
        var settings = NntpdPathSurveyLogging.ReadSettings(configuration);
        Assert.Equal(NntpdPathSurveyLogging.PathSurveyRollingFilePath(dir.Path), settings.Path);
        Assert.Equal("HACK {Message}", configuration["Serilog:Inpaths:outputTemplate"]);
    }

    [Fact]
    public void Write_UsesConfiguredPath_AndPathFormatter()
    {
        using var dir = new TempLogDir();
        var configuration = PathSurveyTestConfiguration.Create(dir.Path, rollingInterval: "Infinite");
        using (var writer = new SerilogPathSurveyWriter(configuration))
        {
            writer.Write("canonical.example!peer"u8);
            writer.Flush();
        }

        var text = PathSurveyTestConfiguration.ReadInpathsFile(dir.Path);
        Assert.Equal("Path: canonical.example!peer\n", text);
        Assert.DoesNotContain("HACK", text, StringComparison.Ordinal);
        Assert.DoesNotContain("giganews", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_PreservesExactCanonicalPathBytes()
    {
        using var dir = new TempLogDir();
        var configuration = PathSurveyTestConfiguration.Create(dir.Path, rollingInterval: "Infinite");
        var path = "news.usenet.ninja!nntpd01.usenet.ninja!BlueWorldHosting!not-for-mail"u8;
        using (var writer = new SerilogPathSurveyWriter(configuration))
        {
            writer.Write(path);
            writer.Dispose();
        }

        var text = PathSurveyTestConfiguration.ReadInpathsFile(dir.Path);
        Assert.Equal("Path: " + Encoding.Latin1.GetString(path) + "\n", text);
    }

    [Fact]
    public void Write_EmptyPath_EmitsPathPrefixAndNewline()
    {
        using var dir = new TempLogDir();
        var configuration = PathSurveyTestConfiguration.Create(dir.Path, rollingInterval: "Infinite");
        using (var writer = new SerilogPathSurveyWriter(configuration))
        {
            writer.Write([]);
            writer.Dispose();
        }

        Assert.Equal("Path: \n", PathSurveyTestConfiguration.ReadInpathsFile(dir.Path));
    }

    [Fact]
    public void Write_EmitsCompleteLines_AndDisposeFlushesThem()
    {
        using var dir = new TempLogDir();
        var configuration = PathSurveyTestConfiguration.Create(dir.Path, rollingInterval: "Infinite", buffered: true);
        using (var writer = new SerilogPathSurveyWriter(configuration))
        {
            writer.Write("one.example"u8);
            writer.Write("two.example"u8);
        }

        var text = PathSurveyTestConfiguration.ReadInpathsFile(dir.Path);
        Assert.Equal("Path: one.example\nPath: two.example\n", text);
    }

    [Fact]
    public void SizeBasedRoll_HandsCompletedFileToHandler_BeforeGzip()
    {
        using var dir = new TempLogDir();
        var handler = new RecordingCompletedPathSurveyFileHandler();
        var configuration = PathSurveyTestConfiguration.Create(
            dir.Path,
            rollingInterval: "Infinite",
            retainedFileCountLimit: 1,
            buffered: false,
            fileSizeLimitBytes: 80,
            rollOnFileSizeLimit: true);
        using (var writer = new SerilogPathSurveyWriter(
                   configuration,
                   handler,
                   NullLogger<SerilogPathSurveyWriter>.Instance))
        {
            for (var i = 0; i < 40; i++)
            {
                writer.Write("aaaaaaaaaaaaaaaaaaaaaaaa"u8);
            }

            writer.Dispose();
        }

        Assert.NotEmpty(handler.Completed);
        Assert.All(handler.GzipExistedAtHandoff, static existed => Assert.False(existed));
        Assert.All(handler.Completed, static path => Assert.False(File.Exists(path)));
        Assert.Empty(Directory.GetFiles(dir.Path, "inpaths*.gz"));
        Assert.Single(Directory.GetFiles(dir.Path, "inpaths*.log"));
    }

    [Fact]
    public void DailyRolling_WritesDatedFileName()
    {
        using var dir = new TempLogDir();
        var configuration = PathSurveyTestConfiguration.Create(dir.Path, rollingInterval: "Day");
        using (var writer = new SerilogPathSurveyWriter(configuration))
        {
            writer.Write("day.example"u8);
            writer.Dispose();
        }

        var dated = Directory.GetFiles(dir.Path, "inpaths-????????.log");
        Assert.Single(dated);
        Assert.Equal("Path: day.example\n", PathSurveyTestConfiguration.ReadInpathsFile(dir.Path));
    }

    [Fact]
    public void ApplicationLogger_DoesNotReceivePathLines()
    {
        using var dir = new TempLogDir();
        var appSink = new CollectingSink();
        using var app = new LoggerConfiguration().WriteTo.Sink(appSink).CreateLogger();
        var configuration = PathSurveyTestConfiguration.Create(dir.Path, rollingInterval: "Infinite");
        using (var writer = new SerilogPathSurveyWriter(configuration))
        {
            writer.Write("isolated.example"u8);
            writer.Dispose();
        }

        app.Information("application diagnostic");
        Assert.DoesNotContain(appSink.Lines, static line => line.Contains("isolated.example", StringComparison.Ordinal));
        Assert.DoesNotContain(appSink.Lines, static line => line.StartsWith("Path: ", StringComparison.Ordinal));
        Assert.Equal("Path: isolated.example\n", PathSurveyTestConfiguration.ReadInpathsFile(dir.Path));
    }

    [Fact]
    public void NewsLogger_DoesNotReceivePathSurveyLines()
    {
        using var dir = new TempLogDir();
        var pathConfig = PathSurveyTestConfiguration.Create(dir.Path, rollingInterval: "Infinite");
        var newsConfig = NewsTestConfiguration.Create(dir.Path, rollingInterval: "Infinite");
        using (var paths = new SerilogPathSurveyWriter(pathConfig))
        using (var news = new SerilogNewsLogWriter(newsConfig))
        {
            paths.Write("survey-only.example"u8);
            news.Write(new NewsLogEvent(NewsLogDisposition.Accepted, Encoding.ASCII.GetBytes("<news-only@example.com>")));
            paths.Dispose();
            news.Dispose();
        }

        var pathText = PathSurveyTestConfiguration.ReadInpathsFile(dir.Path);
        var newsText = NewsTestConfiguration.ReadNewsFile(dir.Path);
        Assert.Equal("Path: survey-only.example\n", pathText);
        Assert.DoesNotContain("Path:", newsText, StringComparison.Ordinal);
        Assert.Contains("<news-only@example.com>", newsText, StringComparison.Ordinal);
        Assert.DoesNotContain("<news-only@example.com>", pathText, StringComparison.Ordinal);
        Assert.DoesNotContain("survey-only.example", newsText, StringComparison.Ordinal);
    }

    [Fact]
    public void Writer_DoesNotRetainPathObservationsInACollection()
    {
        var fields = typeof(SerilogPathSurveyWriter).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(
            fields,
            static field =>
                field.FieldType != typeof(Serilog.Core.Logger)
                && typeof(System.Collections.IEnumerable).IsAssignableFrom(field.FieldType)
                && field.FieldType != typeof(string));
        Assert.Single(fields);
    }

    [Fact]
    public void CreateLogger_UsesDistinctSourceContext()
    {
        Assert.Equal("VectorNNTPD.ReaderService.Inpaths", NntpdPathSurveyLogging.SourceContext);
        Assert.NotEqual(NntpdNewsLogging.SourceContext, NntpdPathSurveyLogging.SourceContext);
    }

    [Fact]
    public void ProductionAppsettings_DeclaresInpathsBesideNews()
    {
        var json = File.ReadAllText(FindProductionAppsettings());
        Assert.Contains("\"Inpaths\"", json, StringComparison.Ordinal);
        Assert.Contains("logs/inpaths-.log", json, StringComparison.Ordinal);
        Assert.Contains("logs/news-.log", json, StringComparison.Ordinal);
    }

    private static string FindProductionAppsettings()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Join(dir, "src", "VectorNNTPD.ReaderService", "VectorNNTPD.ReaderService.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Production VectorNNTPD.ReaderService.json was not found.");
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<string> Lines { get; } = [];

        public void Emit(LogEvent logEvent) => Lines.Add(logEvent.RenderMessage());
    }

    private sealed class TempLogDir : IDisposable
    {
        public TempLogDir()
        {
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "vectornntp-inpaths-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            // codeql[cs/catch-of-all-exceptions] Best-effort test cleanup must tolerate any filesystem or disposal failure.
            catch (Exception ex)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
                // best-effort
            }
        }
    }
}
