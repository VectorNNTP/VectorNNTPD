using System.Text;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Tests.Session;

/// <summary>
/// RFC 3977 §9.8 <c>article-number = 1*16DIGIT</c>. No CLR integer parse exists.
/// </summary>
public sealed class NntpArticleNumberTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("0001")]
    [InlineData("2147483647")]
    [InlineData("2147483648")]
    [InlineData("9999999999999999")]
    public void Syntax_AcceptsRfcDigitTokensThrough16Digits(string token)
    {
        Assert.True(NntpArticleNumber.IsSyntax(Encoding.ASCII.GetBytes(token)));
        Assert.True(NntpCommandTestParse.ParseCommand("ARTICLE " + token).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("10000000000000000")]
    [InlineData("12345678901234567")]
    [InlineData("12a")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    public void Syntax_RejectsEmptySeventeenDigitsAndNonDigits(string token)
    {
        Assert.False(NntpArticleNumber.IsSyntax(Encoding.ASCII.GetBytes(token)));
        if (token.Length == 0)
        {
            return;
        }

        Assert.Equal(NntpParseStatus.InvalidArgument, NntpCommandTestParse.ParseCommand("ARTICLE " + token).Status);
    }

    [Fact]
    public void MaxDigits_IsRfc16()
    {
        Assert.Equal(16, NntpArticleNumber.MaxDigits);
        Assert.True(NntpArticleNumber.IsSyntax("9999999999999999"u8));
        Assert.False(NntpArticleNumber.IsSyntax("10000000000000000"u8));
    }
}
