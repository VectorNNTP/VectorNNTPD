namespace VectorNNTPD.ReaderService.Tests.Fixtures;

/// <summary>
/// Skips at discovery unless <c>VECTORNNTP_SPAMD_INTEGRATION</c> is set to <c>host:port</c>.
/// Ordinary CI does not set the variable, so these tests never dial a default address.
/// When the variable is set, the test runs and fails if that endpoint is unreachable or malformed.
/// </summary>
public sealed class SpamdIntegrationFactAttribute : FactAttribute
{
    /// <summary>Environment variable that opts in to a real SPAMD endpoint.</summary>
    public const string EndpointEnvironmentVariable = "VECTORNNTP_SPAMD_INTEGRATION";

    /// <summary>Skip text used when the opt-in variable is absent.</summary>
    public const string SkipReason =
        "Set VECTORNNTP_SPAMD_INTEGRATION=host:port to run opt-in SPAMD compatibility tests. "
        + "Ordinary CI does not set this and must not contact an external scanner.";

    /// <summary>Initializes the attribute and skips when the opt-in endpoint is absent.</summary>
    public SpamdIntegrationFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable(EndpointEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            Skip = SkipReason;
        }
    }

    /// <summary>Parses <c>host:port</c>. An unset variable is not an endpoint.</summary>
    /// <exception cref="InvalidOperationException">The variable is set but is not <c>host:port</c>.</exception>
    public static (string Host, int Port) RequireEndpoint()
    {
        var value = Environment.GetEnvironmentVariable(EndpointEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(SkipReason);
        }

        var separator = value.LastIndexOf(':');
        if (separator <= 0
            || separator == value.Length - 1
            || !int.TryParse(value[(separator + 1)..], out var port)
            || port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"{EndpointEnvironmentVariable} must be host:port. The value '{value}' is not usable.");
        }

        return (value[..separator].Trim(), port);
    }
}
