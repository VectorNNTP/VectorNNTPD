namespace VectorNNTPD.ReaderService.Tests.Fixtures;

/// <summary>
/// xUnit 2 discovery-time skip unless <c>VECTORNNTP_REDIS_INTEGRATION</c> is set.
/// When the variable is set, the test runs and must fail if Redis is unreachable.
/// </summary>
public sealed class SessionStateRedisIntegrationFactAttribute : FactAttribute
{
    /// <summary>Initializes the attribute and skips when the opt-in endpoint is absent.</summary>
    public SessionStateRedisIntegrationFactAttribute()
    {
        if (SessionStateRedisIntegration.TryGetOptions() is null)
        {
            Skip = SessionStateRedisIntegration.SkipReason;
        }
    }
}
