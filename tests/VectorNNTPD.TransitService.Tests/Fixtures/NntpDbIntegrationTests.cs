namespace VectorNNTPD.TransitService.Tests.Fixtures;

public sealed class NntpDbIntegrationTests
{
    [Fact]
    public void DescribeTarget_OmitsPasswordAndFullConnectionString()
    {
        const string connectionString =
            "Server=198.18.0.70;Port=3306;Database=nntpdb;User ID=nntpd;Password=super-secret;";

        var description = NntpDbIntegration.DescribeTarget(connectionString);

        Assert.Contains("Server=198.18.0.70", description, StringComparison.Ordinal);
        Assert.Contains("Database=nntpdb", description, StringComparison.Ordinal);
        Assert.Contains("User ID=nntpd", description, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", description, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(connectionString, description, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetConnectionString_ReadsOnlyOptInVariable()
    {
        Assert.Equal("VECTORNNTP_NNTPDB_INTEGRATION", NntpDbIntegration.ConnectionStringEnvironmentVariable);
        Assert.Contains("do not use ConnectionStrings:NntpDB", NntpDbIntegration.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrationFact_SkipsWhenOptInVariableIsUnset()
    {
        if (NntpDbIntegration.TryGetConnectionString() is not null)
        {
            return;
        }

        var attribute = new NntpDbIntegrationFactAttribute();
        Assert.Equal(NntpDbIntegration.SkipReason, attribute.Skip);
    }
}
