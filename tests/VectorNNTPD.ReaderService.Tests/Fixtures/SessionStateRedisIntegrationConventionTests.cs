using VectorNNTPD.ReaderService.Configuration;

namespace VectorNNTPD.ReaderService.Tests.Fixtures;

public sealed class SessionStateRedisIntegrationConventionTests
{
    [Fact]
    public void DescribeTarget_UsesHostAndPortOnly()
    {
        var description = SessionStateRedisIntegration.DescribeTarget(
            new RedisOptions { Host = ["198.18.0.70"], Port = 6379 });
        Assert.Equal(SessionStateRedisIntegration.AuthorizedEndpoint, description);
    }

    [Fact]
    public void TryGetOptions_ReadsOnlyOptInVariable()
    {
        Assert.Equal("VECTORNNTP_REDIS_INTEGRATION", SessionStateRedisIntegration.EndpointEnvironmentVariable);
        Assert.Contains("do not use Redis:Host", SessionStateRedisIntegration.SkipReason, StringComparison.Ordinal);
        Assert.Contains(SessionStateRedisIntegration.AuthorizedEndpoint, SessionStateRedisIntegration.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrationFact_SkipsWhenOptInVariableIsUnset()
    {
        if (SessionStateRedisIntegration.TryGetOptions() is not null)
        {
            return;
        }

        var attribute = new SessionStateRedisIntegrationFactAttribute();
        Assert.Equal(SessionStateRedisIntegration.SkipReason, attribute.Skip);
    }
}
