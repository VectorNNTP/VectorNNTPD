using VectorNNTPD.TransitService.Configuration;

namespace VectorNNTPD.TransitService.Tests.Transit;

public sealed class TransitMessageTypesTests
{
    public static TheoryData<string, TransitMessageTypes> DiabloTokens => new()
    {
        { "none", TransitMessageTypes.None },
        { "default", TransitMessageTypes.Default },
        { "control", TransitMessageTypes.Control },
        { "cancel", TransitMessageTypes.Cancel },
        { "mime", TransitMessageTypes.Mime },
        { "binary", TransitMessageTypes.Binary },
        { "binaries", TransitMessageTypes.Binary },
        { "uuencode", TransitMessageTypes.UuEncode },
        { "base64", TransitMessageTypes.Base64 },
        { "yenc", TransitMessageTypes.Yenc },
        { "bommanews", TransitMessageTypes.BommaNews },
        { "unidata", TransitMessageTypes.UniData },
        { "multipart", TransitMessageTypes.Multipart },
        { "html", TransitMessageTypes.Html },
        { "ps", TransitMessageTypes.PostScript },
        { "binhex", TransitMessageTypes.BinHex },
        { "partial", TransitMessageTypes.Partial },
        { "pgp", TransitMessageTypes.PgpMessage },
        { "all", TransitMessageTypes.All },
    };

    [Theory]
    [MemberData(nameof(DiabloTokens))]
    public void ParseToken_MapsDiabloNames(string token, TransitMessageTypes expected)
    {
        Assert.True(TransitMessageTypesParser.TryParseToken(token, out var actual));
        Assert.Equal(expected, actual);
        Assert.True(TransitMessageTypesParser.TryParseToken("  " + token.ToUpperInvariant() + "  ", out var padded));
        Assert.Equal(expected, padded);
    }

    [Fact]
    public void BinaryAndBinaries_AreTheSameFlag()
    {
        Assert.True(TransitMessageTypesParser.TryParseToken("binary", out var binary));
        Assert.True(TransitMessageTypesParser.TryParseToken("binaries", out var binaries));
        Assert.Equal(TransitMessageTypes.Binary, binary);
        Assert.Equal(binary, binaries);
    }

    [Fact]
    public void All_IsUnionOfConcreteFlags()
    {
        const TransitMessageTypes concrete =
            TransitMessageTypes.Default
            | TransitMessageTypes.Control
            | TransitMessageTypes.Cancel
            | TransitMessageTypes.Mime
            | TransitMessageTypes.Binary
            | TransitMessageTypes.UuEncode
            | TransitMessageTypes.Base64
            | TransitMessageTypes.Yenc
            | TransitMessageTypes.BommaNews
            | TransitMessageTypes.UniData
            | TransitMessageTypes.Multipart
            | TransitMessageTypes.Html
            | TransitMessageTypes.PostScript
            | TransitMessageTypes.BinHex
            | TransitMessageTypes.Partial
            | TransitMessageTypes.PgpMessage;
        Assert.Equal(concrete, TransitMessageTypes.All);
        Assert.NotEqual(TransitMessageTypes.None, TransitMessageTypes.Default);
        Assert.NotEqual(TransitMessageTypes.Default, TransitMessageTypes.All);
    }

    [Fact]
    public void Parse_DefaultArrayAndDuplicates()
    {
        Assert.True(TransitMessageTypesParser.TryParse(null, out var omitted, out _));
        Assert.Equal(TransitMessageTypes.Default, omitted);

        Assert.True(TransitMessageTypesParser.TryParse([], out var empty, out _));
        Assert.Equal(TransitMessageTypes.Default, empty);

        Assert.True(TransitMessageTypesParser.TryParse(["default"], out var defaults, out _));
        Assert.Equal(TransitMessageTypes.Default, defaults);

        Assert.True(TransitMessageTypesParser.TryParse(["binary", "BINARY", "binaries"], out var dupes, out _));
        Assert.Equal(TransitMessageTypes.Binary, dupes);

        Assert.True(TransitMessageTypesParser.TryParse(["default", "control", "binary"], out var combo, out _));
        Assert.Equal(
            TransitMessageTypes.Default | TransitMessageTypes.Control | TransitMessageTypes.Binary,
            combo);
    }

    [Fact]
    public void Parse_RejectsUnknownAndBlankTokens()
    {
        Assert.False(TransitMessageTypesParser.TryParse(["default", "audio"], out _, out var error));
        Assert.Contains("[1]", error, StringComparison.Ordinal);
        Assert.False(TransitMessageTypesParser.TryParse(["  "], out _, out _));
    }

    [Fact]
    public void CanonicalNames_DoNotInventTypes()
    {
        Assert.Equal(["none"], TransitMessageTypesParser.ToCanonicalNames(TransitMessageTypes.None));
        Assert.Equal(["all"], TransitMessageTypesParser.ToCanonicalNames(TransitMessageTypes.All));
        Assert.Equal(["binary"], TransitMessageTypesParser.ToCanonicalNames(TransitMessageTypes.Binary));
        Assert.DoesNotContain("binaries", TransitMessageTypesParser.ToCanonicalNames(TransitMessageTypes.Binary));
    }

    [Fact]
    public void EnumMemberValues_AreStablePowersOfTwo()
    {
        Assert.Equal(0, (int)TransitMessageTypes.None);
        Assert.Equal(1, (int)TransitMessageTypes.Default);
        Assert.Equal(2, (int)TransitMessageTypes.Control);
        Assert.Equal(4, (int)TransitMessageTypes.Cancel);
        Assert.Equal(8, (int)TransitMessageTypes.Mime);
        Assert.Equal(16, (int)TransitMessageTypes.Binary);
        Assert.Equal(32, (int)TransitMessageTypes.UuEncode);
        Assert.Equal(64, (int)TransitMessageTypes.Base64);
        Assert.Equal(128, (int)TransitMessageTypes.Yenc);
        Assert.Equal(256, (int)TransitMessageTypes.BommaNews);
        Assert.Equal(512, (int)TransitMessageTypes.UniData);
        Assert.Equal(1024, (int)TransitMessageTypes.Multipart);
        Assert.Equal(2048, (int)TransitMessageTypes.Html);
        Assert.Equal(4096, (int)TransitMessageTypes.PostScript);
        Assert.Equal(8192, (int)TransitMessageTypes.BinHex);
        Assert.Equal(16384, (int)TransitMessageTypes.Partial);
        Assert.Equal(32768, (int)TransitMessageTypes.PgpMessage);
    }
}
