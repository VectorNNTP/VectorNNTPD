using VectorNNTPD.BackFillerService.Accounts;

namespace VectorNNTPD.BackFillerService.Tests.Fixtures
{
    /// <summary>Factory for provider account rows used in catalog and NNTP tests.</summary>
    internal static class ProviderAccountTestRows
    {
        /// <summary>Default password on synthesized account rows (not a production secret).</summary>
        internal const string SecretPassword = "account-password-secret-xyz";

        /// <summary>Builds a provider row. Defaults are Giganews, news.example.test:563, SSL, four connections, and <see cref="SecretPassword"/>.</summary>
        internal static ProviderAccountRow Create(
            string? backbone = "Giganews",
            string? hostname = "news.example.test",
            int? port = 563,
            int? maxConnections = 4,
            string? username = "nntp-user",
            string? password = SecretPassword,
            string? useSsl = "y",
            byte? keepAlive = 0)
        {
            return new ProviderAccountRow(
                backbone,
                hostname,
                keepAlive,
                maxConnections,
                username,
                password,
                port,
                useSsl);
        }
    }
}
