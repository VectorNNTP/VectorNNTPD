using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using VectorNNTPD.Common.NntpDb;

namespace VectorNNTPD.BackFillerService.Tests.TestDoubles
{
    /// <summary>
    /// Replaces the production MySQL factory after hosting registration so host tests can start
    /// <see cref="NntpDbService"/> without a server.
    /// </summary>
    internal static class SuccessfulNntpDbConnectionFactory
    {
        /// <summary>
        /// Removes every <see cref="INntpDbConnectionFactory"/> registration from <paramref name="services"/>
        /// and registers an in-memory factory so host startup tests avoid MySQL.
        /// </summary>
        /// <param name="services">Host service collection to mutate before building the provider.</param>
        internal static void Replace(IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(INntpDbConnectionFactory))
                {
                    services.RemoveAt(i);
                }
            }

            services.AddSingleton<INntpDbConnectionFactory, Factory>();
        }

        /// <summary>Always opens an in-memory session; ignores the connection string.</summary>
        private sealed class Factory : INntpDbConnectionFactory
        {
            /// <inheritdoc />
            public Task<INntpDbSession> OpenAsync(string connectionString, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<INntpDbSession>(new Session());
            }
        }

        /// <summary>Fixed shared-configuration rows for host startup; SQL commands are unsupported.</summary>
        private sealed class Session : INntpDbSession, INntpSharedConfigurationRowSource
        {
            /// <inheritdoc />
            public ValueTask<int> SelectOneAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(1);
            }

            /// <summary>
            /// Returns a fixed shared-configuration candidate row for startup paths; ignores database state.
            /// </summary>
            /// <param name="cancellationToken">Honored before materializing the static row list.</param>
            /// <returns>A single hard-coded candidate suitable for host configuration tests.</returns>
            public ValueTask<IReadOnlyList<NntpSharedConfigurationCandidate>> ReadCandidatesAsync(
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<NntpSharedConfigurationCandidate> rows =
                [
                    new NntpSharedConfigurationCandidate(
                        1024,
                        "news.usenet.ninja",
                        null,
                        "https://acme-v02.api.letsencrypt.org/directory",
                        14,
                        "0123456789abcdef0123456789abcdef",
                        "usenet.ninja"),
                ];
                return ValueTask.FromResult(rows);
            }

            /// <summary>Not supported; this session never runs ad hoc SQL.</summary>
            /// <returns>Never returns; always throws.</returns>
            public MySqlCommand CreateCommand() =>
                throw new NotSupportedException("This test session does not execute SQL.");

            /// <summary>Not supported; this session does not open MySQL transactions.</summary>
            /// <param name="cancellationToken">Honored before throwing.</param>
            public ValueTask<MySqlTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new NotSupportedException("This test session does not execute SQL.");
            }

            /// <summary>Not supported; isolation level is ignored because no transaction is created.</summary>
            /// <param name="isolationLevel">Included in the exception message for diagnostics only.</param>
            /// <param name="cancellationToken">Honored before throwing.</param>
            public ValueTask<MySqlTransaction> BeginTransactionAsync(
                System.Data.IsolationLevel isolationLevel,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new NotSupportedException(
                    "This test session does not execute SQL at isolation " + isolationLevel + ".");
            }

            /// <summary>No-op dispose; the session holds no connections or transactions.</summary>
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
