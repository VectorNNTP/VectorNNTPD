using System.Text;
using VectorNNTPD.ReaderService.Newsgroups;

namespace VectorNNTPD.ReaderService.Tests.Newsgroups;

public sealed class NntpWildmatTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("a*")]
    [InlineData("a*,!*b")]
    [InlineData("*.recovery")]
    [InlineData("*")]
    public void TryValidate_AcceptsRfcPatterns(string wildmat)
    {
        Assert.True(NntpWildmat.TryValidate(Encoding.ASCII.GetBytes(wildmat)));
    }

    [Theory]
    [InlineData("u[ks].*")]
    [InlineData(@"a\b")]
    [InlineData("a]b")]
    [InlineData("")]
    [InlineData("a*,")]
    [InlineData("!*b")]
    public void TryValidate_RejectsForbiddenOrEmptyPatterns(string wildmat)
    {
        Assert.False(NntpWildmat.TryValidate(Encoding.ASCII.GetBytes(wildmat)));
    }

    [Theory]
    [InlineData("misc.test", "misc.test", true)]
    [InlineData("MISC.TEST", "misc.test", true)]
    [InlineData("misc.test", "MISC.TEST", true)]
    [InlineData("alt.rfc-writers.recovery", "*.recovery", true)]
    [InlineData("tx.natives.recovery", "*.recovery", true)]
    [InlineData("misc.test", "*.recovery", false)]
    [InlineData("aaa", "a*,!*b", true)]
    [InlineData("abb", "a*,!*b", false)]
    [InlineData("ccb", "a*,!*b,*c*", true)]
    [InlineData("xxx", "a*,!*b,*c*", false)]
    [InlineData("misc.test", "misc.?est", true)]
    [InlineData("misc.test", "misc.??st", true)]
    [InlineData("misc.test", "misc.?", false)]
    public void IsMatch_FollowsRfcExamples_CaseInsensitive(string text, string wildmat, bool expected)
    {
        var textBytes = Encoding.ASCII.GetBytes(text);
        var pattern = Encoding.ASCII.GetBytes(wildmat);
        Assert.Equal(expected, NntpWildmat.IsMatch(textBytes, pattern));
        Assert.Equal(expected, NntpWildmat.IsMatchValidated(textBytes, pattern));
    }
}
