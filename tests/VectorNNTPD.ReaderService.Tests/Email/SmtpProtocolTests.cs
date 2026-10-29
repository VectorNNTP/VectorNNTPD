using System.Text;
using VectorNNTPD.ReaderService.Email.Smtp;

namespace VectorNNTPD.ReaderService.Tests.Email;

public sealed class SmtpProtocolTests
{
    [Theory]
    [InlineData("250 OK", 250, false, "OK")]
    [InlineData("250-PIPELINING", 250, true, "PIPELINING")]
    [InlineData("354", 354, false, "")]
    public void ParseReplyLine_Structured(string line, int code, bool continuation, string text)
    {
        Assert.True(SmtpResponseReader.TryParseReplyLine(
            Encoding.ASCII.GetBytes(line),
            out var parsed,
            out var cont,
            out var body));
        Assert.Equal(code, parsed);
        Assert.Equal(continuation, cont);
        Assert.Equal(text, body);
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("2OK")]
    [InlineData("25X OK")]
    [InlineData("250XOK")]
    public void ParseReplyLine_RejectsMalformed(string line)
    {
        Assert.False(SmtpResponseReader.TryParseReplyLine(Encoding.ASCII.GetBytes(line), out _, out _, out _));
    }

    [Fact]
    public void Capabilities_ParseExtensions()
    {
        var response = new SmtpResponse(
            250,
            [
                "test.example",
                "STARTTLS",
                "AUTH PLAIN LOGIN",
                "SIZE 1000",
                "8BITMIME",
                "SMTPUTF8",
            ],
            enhancedStatus: null,
            text: "ehlo");
        var caps = SmtpCapabilities.Parse(response);
        Assert.True(caps.StartTls);
        Assert.True(caps.EightBitMime);
        Assert.True(caps.SmtpUtf8);
        Assert.Equal(1000, caps.MaxSize);
        Assert.True(caps.SupportsAuth("PLAIN"));
        Assert.True(caps.SupportsAuth("LOGIN"));
        Assert.False(caps.SupportsAuth("CRAM-MD5"));
    }

    [Fact]
    public async Task DataEncoder_DotStuffsAndTerminates()
    {
        using var stream = new MemoryStream();
        await SmtpDataEncoder.WriteStuffedAsync(
            stream,
            ".hidden\r\nnormal\r\n"u8.ToArray(),
            CancellationToken.None);
        var text = Encoding.ASCII.GetString(stream.ToArray());
        Assert.Equal("..hidden\r\nnormal\r\n.\r\n", text);
    }

    [Fact]
    public void EnhancedStatus_IsSplit()
    {
        Assert.True(SmtpResponseReader.TrySplitEnhanced("2.0.0 OK", out var enhanced, out var rest));
        Assert.Equal("2.0.0", enhanced);
        Assert.Equal("OK", rest);
    }
}
