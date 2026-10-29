using Microsoft.Extensions.Configuration;
using VectorNNTPD.ReaderService.Configuration;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

public sealed class PostFilterLeftoverConfigurationTests
{
    [Fact]
    public void LeftoverNntpdPostFilterSection_FailsStartupValidation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nntpd:PostFilter:Gate"] = "Active",
                ["Nntpd:PostFilter:Quota:MaxMessagesShort"] = "100",
            })
            .Build();
        var validator = new PostFilterLeftoverConfigurationValidator(configuration);
        var result = validator.Validate(null, new NntpdOptions());
        Assert.False(result.Succeeded);
        Assert.Contains("nntppostfiltercurrent", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsentNntpdPostFilterSection_Succeeds()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var validator = new PostFilterLeftoverConfigurationValidator(configuration);
        var result = validator.Validate(null, new NntpdOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void NntpdOptions_HasNoPostFilterProperty()
    {
        Assert.Null(typeof(NntpdOptions).GetProperty("PostFilter"));
    }
}
