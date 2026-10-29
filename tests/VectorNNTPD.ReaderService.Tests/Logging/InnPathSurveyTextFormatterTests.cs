using System.Text;
using Serilog.Events;
using Serilog.Parsing;
using VectorNNTPD.ReaderService.Logging;

namespace VectorNNTPD.ReaderService.Tests.Logging;

public sealed class InnPathSurveyTextFormatterTests
{
    [Fact]
    public void Format_IgnoresMessageTemplate_AndEmitsPathLine()
    {
        var evt = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("HACK {Message} {PathSurveyPath}"),
            [
                new LogEventProperty(NntpdPathSurveyLogging.PathProperty, new ScalarValue("peer.example!not-for-mail")),
            ]);

        using var writer = new StringWriter();
        new InnPathSurveyTextFormatter().Format(evt, writer);
        Assert.Equal("Path: peer.example!not-for-mail\n", writer.ToString());
        Assert.DoesNotContain("HACK", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Format_EmptyPath_EmitsPrefixAndNewline()
    {
        var evt = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("inpaths"),
            [
                new LogEventProperty(NntpdPathSurveyLogging.PathProperty, new ScalarValue(string.Empty)),
            ]);

        using var writer = new StringWriter();
        new InnPathSurveyTextFormatter().Format(evt, writer);
        Assert.Equal("Path: \n", writer.ToString());
    }

    [Fact]
    public void Format_PreservesExactPathBytesAsLatin1()
    {
        var path = "AbC!Example.COM!not-for-mail";
        var evt = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("inpaths"),
            [
                new LogEventProperty(NntpdPathSurveyLogging.PathProperty, new ScalarValue(path)),
            ]);

        using var writer = new StringWriter();
        new InnPathSurveyTextFormatter().Format(evt, writer);
        Assert.Equal("Path: " + path + "\n", writer.ToString());
        Assert.Equal(path, Encoding.Latin1.GetString(Encoding.Latin1.GetBytes(path)));
    }

    [Fact]
    public void Format_DoesNotEmitMessageIdNewsgroupsOrDisposition()
    {
        var evt = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("inpaths"),
            [
                new LogEventProperty(NntpdPathSurveyLogging.PathProperty, new ScalarValue("only.path")),
                new LogEventProperty("NewsMessageId", new ScalarValue("<id@example>")),
                new LogEventProperty("NewsDisposition", new ScalarValue('+')),
            ]);

        using var writer = new StringWriter();
        new InnPathSurveyTextFormatter().Format(evt, writer);
        var line = writer.ToString();
        Assert.Equal("Path: only.path\n", line);
        Assert.DoesNotContain("<id@example>", line, StringComparison.Ordinal);
        Assert.DoesNotContain("+", line, StringComparison.Ordinal);
    }
}
