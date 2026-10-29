using MySqlConnector;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.TransitService.Tests.Fixtures;

/// <summary>
/// Opt-in live NntpDB seam. Ordinary tests never read <c>ConnectionStrings:NntpDB</c>
/// or the committed production appsettings target.
/// </summary>
internal static class NntpDbIntegration
{
    /// <summary>
    /// Dedicated MySQL connection string for live <c>nntpmoderators</c> tests.
    /// </summary>
    public const string ConnectionStringEnvironmentVariable = "VECTORNNTP_NNTPDB_INTEGRATION";

    /// <summary>Reason used when the live MySQL tests are not configured.</summary>
    public const string SkipReason =
        "Set VECTORNNTP_NNTPDB_INTEGRATION to a dedicated MySQL connection string "
        + "that contains nntpmoderators. Ordinary tests do not open MySQL and do not "
        + "use ConnectionStrings:NntpDB.";

    /// <summary>Returns the opt-in connection string, or <see langword="null"/> when unset.</summary>
    public static string? TryGetConnectionString()
    {
        var value = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Describes the target without passwords or the full connection string.</summary>
    public static string DescribeTarget(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new MySqlConnectionStringBuilder(connectionString);
        return "Server=" + builder.Server
            + ";Database=" + builder.Database
            + ";User ID=" + builder.UserID;
    }

    /// <summary>Builds NntpDB options for the live factory. Never logs the string.</summary>
    public static NntpDbOptions CreateOptions(string connectionString) =>
        new()
        {
            ConnectionString = connectionString,
            StartupTimeout = TimeSpan.FromSeconds(15),
        };
}
