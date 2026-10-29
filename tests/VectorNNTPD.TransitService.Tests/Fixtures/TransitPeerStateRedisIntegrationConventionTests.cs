namespace VectorNNTPD.TransitService.Tests.Fixtures;

public sealed class TransitPeerStateRedisIntegrationConventionTests
{
    [Fact]
    public void OptInVariable_MatchesSessionStateDedicatedTestRedis()
    {
        Assert.Equal(
            SessionStateRedisIntegration.EndpointEnvironmentVariable,
            TransitPeerStateRedisIntegration.EndpointEnvironmentVariable);
        Assert.Equal(
            SessionStateRedisIntegration.AuthorizedEndpoint,
            TransitPeerStateRedisIntegration.AuthorizedEndpoint);
        Assert.Contains("do not use Redis:Host", TransitPeerStateRedisIntegration.SkipReason, StringComparison.Ordinal);
        Assert.Contains(TransitPeerStateRedisIntegration.AuthorizedEndpoint, TransitPeerStateRedisIntegration.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrationFact_SkipsWhenOptInVariableIsUnset()
    {
        if (TransitPeerStateRedisIntegration.TryGetOptions() is not null)
        {
            return;
        }

        var attribute = new TransitPeerStateRedisIntegrationFactAttribute();
        Assert.Equal(TransitPeerStateRedisIntegration.SkipReason, attribute.Skip);
    }
}
