using System.Text;
using Serilog.Events;
using Serilog.Parsing;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Logging;

namespace VectorNNTPD.ReaderService.Tests.Logging;

public sealed class InnNewsTextFormatterTests
{
    [Fact]
    public void Format_IgnoresMessageTemplate_AndEmitsInnLine()
    {
        var timestamp = new DateTimeOffset(2024, 8, 25, 13, 37, 41, 839, TimeSpan.Zero);
        var evt = new LogEvent(
            timestamp,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("HACK {NewsDisposition} {Message}"),
            [
                new LogEventProperty(NntpdNewsLogging.DispositionProperty, new ScalarValue('+')),
                new LogEventProperty(NntpdNewsLogging.FeedProperty, new ScalarValue("?")),
                new LogEventProperty(NntpdNewsLogging.MessageIdProperty, new ScalarValue("<cancel.4066@foo.com>")),
                new LogEventProperty(NntpdNewsLogging.SitesProperty, new ScalarValue(string.Empty)),
            ]);

        using var writer = new StringWriter();
        new InnNewsTextFormatter().Format(evt, writer);
        Assert.Equal("Aug 25 13:37:41.839 + ? <cancel.4066@foo.com> 0 ?\n", writer.ToString());
        Assert.DoesNotContain("HACK", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Format_Junk_UsesJ_SpacePaddedDay_AndOmitsSite()
    {
        var timestamp = new DateTimeOffset(2024, 1, 5, 0, 0, 0, 0, TimeSpan.Zero);
        var evt = new LogEvent(
            timestamp,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("news"),
            [
                new LogEventProperty(NntpdNewsLogging.DispositionProperty, new ScalarValue('j')),
                new LogEventProperty(NntpdNewsLogging.FeedProperty, new ScalarValue("?")),
                new LogEventProperty(NntpdNewsLogging.MessageIdProperty, new ScalarValue("<AbC@Example.COM>")),
            ]);

        using var writer = new StringWriter();
        new InnNewsTextFormatter().Format(evt, writer);
        var line = writer.ToString();
        Assert.Equal("Jan  5 00:00:00.000 j ? <AbC@Example.COM> 0\n", line);
        Assert.DoesNotContain(" 0 ?", line, StringComparison.Ordinal);
        Assert.DoesNotContain("newsgroup not carried", line, StringComparison.Ordinal);
        Assert.DoesNotContain(" SITE", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_Junk_UsesSuppliedReason_NotDerived()
    {
        var timestamp = new DateTimeOffset(2024, 1, 5, 0, 0, 0, 0, TimeSpan.Zero);
        var evt = new LogEvent(
            timestamp,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("news"),
            [
                new LogEventProperty(NntpdNewsLogging.DispositionProperty, new ScalarValue('j')),
                new LogEventProperty(NntpdNewsLogging.FeedProperty, new ScalarValue("?")),
                new LogEventProperty(NntpdNewsLogging.MessageIdProperty, new ScalarValue("<AbC@Example.COM>")),
                new LogEventProperty(NntpdNewsLogging.ReasonProperty, new ScalarValue("peer-only: junk.local")),
            ]);

        using var writer = new StringWriter();
        new InnNewsTextFormatter().Format(evt, writer);
        Assert.Equal("Jan  5 00:00:00.000 j ? <AbC@Example.COM> 0 peer-only: junk.local\n", writer.ToString());
    }

    [Fact]
    public void Format_Rejected_UsesSuppliedCodeAndReason()
    {
        var timestamp = new DateTimeOffset(2024, 8, 25, 13, 37, 54, 638, TimeSpan.Zero);
        var evt = new LogEvent(
            timestamp,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("HACK {Message}"),
            [
                new LogEventProperty(NntpdNewsLogging.DispositionProperty, new ScalarValue('-')),
                new LogEventProperty(NntpdNewsLogging.FeedProperty, new ScalarValue("?")),
                new LogEventProperty(NntpdNewsLogging.MessageIdProperty, new ScalarValue("<23k82@bar.net>")),
                new LogEventProperty(NntpdNewsLogging.ResponseCodeProperty, new ScalarValue(437)),
                new LogEventProperty(NntpdNewsLogging.ReasonProperty, new ScalarValue("Poison newsgroup")),
            ]);

        using var writer = new StringWriter();
        new InnNewsTextFormatter().Format(evt, writer);
        Assert.Equal("Aug 25 13:37:54.638 - ? <23k82@bar.net> 0 Poison newsgroup\n", writer.ToString());
        Assert.DoesNotContain("437", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("HACK", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Format_Moderated_UsesM_NotPlusOrQuestion()
    {
        var timestamp = new DateTimeOffset(2024, 8, 25, 13, 37, 41, 839, TimeSpan.Zero);
        var evt = new LogEvent(
            timestamp,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("news"),
            [
                new LogEventProperty(NntpdNewsLogging.DispositionProperty, new ScalarValue('m')),
                new LogEventProperty(NntpdNewsLogging.FeedProperty, new ScalarValue("?")),
                new LogEventProperty(NntpdNewsLogging.MessageIdProperty, new ScalarValue("<mod@example.com>")),
            ]);

        using var writer = new StringWriter();
        new InnNewsTextFormatter().Format(evt, writer);
        var line = writer.ToString();
        Assert.Equal("Aug 25 13:37:41.839 m ? <mod@example.com> 0\n", line);
        Assert.DoesNotContain(" 0 ?", line, StringComparison.Ordinal);
        Assert.DoesNotContain(" + ", line, StringComparison.Ordinal);
        Assert.NotEqual('?', (char)NewsLogDisposition.Moderated);
    }

    [Fact]
    public void Format_PreservesMessageIdBytesAsAscii()
    {
        var timestamp = new DateTimeOffset(2024, 8, 25, 13, 37, 41, 839, TimeSpan.Zero);
        var id = "<AbC-Preserve@Example.COM>";
        var evt = new LogEvent(
            timestamp,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("news"),
            [
                new LogEventProperty(NntpdNewsLogging.DispositionProperty, new ScalarValue('+')),
                new LogEventProperty(NntpdNewsLogging.FeedProperty, new ScalarValue("?")),
                new LogEventProperty(NntpdNewsLogging.MessageIdProperty, new ScalarValue(id)),
            ]);

        using var writer = new StringWriter();
        new InnNewsTextFormatter().Format(evt, writer);
        Assert.Contains(id, writer.ToString(), StringComparison.Ordinal);
        Assert.EndsWith(id + " 0 ?\n", writer.ToString(), StringComparison.Ordinal);
        Assert.Equal(id, Encoding.ASCII.GetString(Encoding.ASCII.GetBytes(id)));
    }

    [Fact]
    public void Format_NamedPeer_RoundTripsSizeAndOutboundPlaceholder_WithoutSession()
    {
        var timestamp = new DateTimeOffset(2026, 9, 27, 0, 4, 4, 293, TimeSpan.Zero);
        var evt = new LogEvent(
            timestamp,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("news"),
            [
                new LogEventProperty(NntpdNewsLogging.DispositionProperty, new ScalarValue('+')),
                new LogEventProperty(NntpdNewsLogging.FeedProperty, new ScalarValue("BlueWorldHosting")),
                new LogEventProperty(NntpdNewsLogging.MessageIdProperty, new ScalarValue("<bdp-backfill-53301945c0f74ac3afc1edda885d2294@livingmemorybear.invalid>")),
                new LogEventProperty(NntpdNewsLogging.SizeProperty, new ScalarValue(1584)),
                new LogEventProperty(NntpdNewsLogging.SitesProperty, new ScalarValue(string.Empty)),
            ]);

        using var writer = new StringWriter();
        new InnNewsTextFormatter().Format(evt, writer);
        Assert.Equal(
            "Sep 27 00:04:04.293 + BlueWorldHosting <bdp-backfill-53301945c0f74ac3afc1edda885d2294@livingmemorybear.invalid> 1584 ?\n",
            writer.ToString());
    }
}
