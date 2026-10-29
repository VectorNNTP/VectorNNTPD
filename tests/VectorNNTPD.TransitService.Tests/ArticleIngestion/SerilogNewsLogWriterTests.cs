using System.Text;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Logging;

namespace VectorNNTPD.TransitService.Tests.ArticleIngestion;

public sealed class SerilogNewsLogWriterTests
{
    [Fact]
    public void ReadSettings_HonoursConfiguredOperationalProperties()
    {
        using var dir = new TempLogDir();
        var configuration = NewsTestConfiguration.Create(
            dir.Path,
            rollingInterval: "Hour",
            retainedFileCountLimit: 3,
            buffered: false,
            fileSizeLimitBytes: 1_048_576,
            rollOnFileSizeLimit: true,
            bufferSize: 250,
            blockWhenFull: false);
        var settings = NntpdNewsLogging.ReadSettings(configuration);
        Assert.Equal(Path.Join(dir.Path, "news-.log"), settings.Path);
        Assert.Equal(RollingInterval.Hour, settings.RollingInterval);
        Assert.Equal(3, settings.RetainedFileCountLimit);
        Assert.False(settings.Buffered);
        Assert.True(settings.RollOnFileSizeLimit);
        Assert.Equal(1_048_576, settings.FileSizeLimitBytes);
        Assert.Equal(250, settings.BufferSize);
        Assert.False(settings.BlockWhenFull);
        Assert.Null(settings.FlushToDiskInterval);
    }

    [Fact]
    public void BindResolvedNewsPath_UsesLogDir_AndDoesNotExposeOutputTemplate()
    {
        using var dir = new TempLogDir();
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nntpd:LogDir"] = dir.Path,
            ["Serilog:News:path"] = "logs/news-.log",
            ["Serilog:News:outputTemplate"] = "HACK {Message}",
        });
        NntpdNewsLogging.BindResolvedNewsPath(configuration, dir.Path);
        Assert.Equal(NntpdNewsLogging.NewsRollingFilePath(dir.Path), configuration["Serilog:News:path"]);
        var settings = NntpdNewsLogging.ReadSettings(configuration);
        Assert.Equal(NntpdNewsLogging.NewsRollingFilePath(dir.Path), settings.Path);
        Assert.Equal("HACK {Message}", configuration["Serilog:News:outputTemplate"]);
    }

    [Fact]
    public void Write_UsesConfiguredPath_AndInnFormatter()
    {
        using var dir = new TempLogDir();
        var configuration = NewsTestConfiguration.Create(dir.Path, rollingInterval: "Infinite");
        using (var writer = new SerilogNewsLogWriter(configuration))
        {
            writer.Write(Accepted("<dir@example.com>"));
            writer.Flush();
        }

        var text = NewsTestConfiguration.ReadNewsFile(dir.Path);
        Assert.Contains("<dir@example.com>", text, StringComparison.Ordinal);
        Assert.Contains(" + ? <dir@example.com> 0 ?", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HACK", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_NamedPeer_EmitsInboundFeedSizeAndOutboundPlaceholder()
    {
        using var dir = new TempLogDir();
        var configuration = NewsTestConfiguration.Create(dir.Path, rollingInterval: "Infinite");
        using (var writer = new SerilogNewsLogWriter(configuration))
        {
            writer.Write(new NewsLogEvent(
                NewsLogDisposition.Accepted,
                Encoding.ASCII.GetBytes("<bdp-backfill-53301945c0f74ac3afc1edda885d2294@livingmemorybear.invalid>"),
                Encoding.ASCII.GetBytes("BlueWorldHosting"),
                size: 1584));
            writer.Flush();
        }

        var text = NewsTestConfiguration.ReadNewsFile(dir.Path);
        Assert.Contains(
            " + BlueWorldHosting <bdp-backfill-53301945c0f74ac3afc1edda885d2294@livingmemorybear.invalid> 1584 ?",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(" + ? ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_EmitsCompleteLines_AndDisposeFlushesThem()
    {
        using var dir = new TempLogDir();
        var configuration = NewsTestConfiguration.Create(dir.Path, rollingInterval: "Infinite", buffered: true);
        using (var writer = new SerilogNewsLogWriter(configuration))
        {
            writer.Write(Accepted("<one@example.com>"));
            writer.Write(Accepted("<two@example.com>"));
        }

        var text = NewsTestConfiguration.ReadNewsFile(dir.Path);
        Assert.Contains("<one@example.com>", text, StringComparison.Ordinal);
        Assert.Contains("<two@example.com>", text, StringComparison.Ordinal);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void DailyRolling_WritesDatedFileName()
    {
        using var dir = new TempLogDir();
        var configuration = NewsTestConfiguration.Create(dir.Path, rollingInterval: "Day");
        using (var writer = new SerilogNewsLogWriter(configuration))
        {
            writer.Write(Accepted("<day@example.com>"));
            writer.Dispose();
        }

        var dated = Directory.GetFiles(dir.Path, "news-????????.log");
        Assert.Single(dated);
        Assert.Contains("<day@example.com>", NewsTestConfiguration.ReadNewsFile(dir.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationLogger_DoesNotReceiveNewsLines()
    {
        using var dir = new TempLogDir();
        var appSink = new CollectingSink();
        using var app = new LoggerConfiguration().WriteTo.Sink(appSink).CreateLogger();
        var configuration = NewsTestConfiguration.Create(dir.Path, rollingInterval: "Infinite");
        using (var writer = new SerilogNewsLogWriter(configuration))
        {
            writer.Write(Accepted("<isolated@example.com>"));
            writer.Dispose();
        }

        app.Information("application diagnostic");
        Assert.DoesNotContain(appSink.Lines, static line => line.Contains("<isolated@example.com>", StringComparison.Ordinal));
        Assert.Contains("<isolated@example.com>", NewsTestConfiguration.ReadNewsFile(dir.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void CustomNewsFileTypes_AreGone()
    {
        Assert.Null(Type.GetType("VectorNNTPD.TransitService.ArticleIngestion.InnNewsLogWriter, VectorNNTPD.TransitService"));
        Assert.Null(Type.GetType("VectorNNTPD.TransitService.Logging.NntpdLogCompression, VectorNNTPD.TransitService"));
    }

    private static NewsLogEvent Accepted(string messageId) =>
        new(NewsLogDisposition.Accepted, Encoding.ASCII.GetBytes(messageId));

    private sealed class CollectingSink : ILogEventSink
    {
        public List<string> Lines { get; } = [];

        public void Emit(LogEvent logEvent) => Lines.Add(logEvent.RenderMessage());
    }

    private sealed class TempLogDir : IDisposable
    {
        public TempLogDir()
        {
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "vectornntp-news-" + Guid.NewGuid().ToString("N"));
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
