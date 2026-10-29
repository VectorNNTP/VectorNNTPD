namespace VectorNNTPD.TransitService.Tests.Fixtures;

/// <summary>
/// xUnit 2 discovery-time skip unless <c>VECTORNNTP_NNTPDB_INTEGRATION</c> is set.
/// When the variable is set, the test runs and must fail if MySQL is unreachable.
/// </summary>
public sealed class NntpDbIntegrationFactAttribute : FactAttribute
{
    /// <summary>Initializes the attribute and skips when the opt-in string is absent.</summary>
    public NntpDbIntegrationFactAttribute()
    {
        if (NntpDbIntegration.TryGetConnectionString() is null)
        {
            Skip = NntpDbIntegration.SkipReason;
        }
    }
}

/// <summary>
/// xUnit 2 theory skip unless <c>VECTORNNTP_NNTPDB_INTEGRATION</c> is set.
/// </summary>
public sealed class NntpDbIntegrationTheoryAttribute : TheoryAttribute
{
    /// <summary>Initializes the attribute and skips when the opt-in string is absent.</summary>
    public NntpDbIntegrationTheoryAttribute()
    {
        if (NntpDbIntegration.TryGetConnectionString() is null)
        {
            Skip = NntpDbIntegration.SkipReason;
        }
    }
}
