using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Nntp;

namespace VectorNNTPD.BackFillerService.Accounts
{
    /// <summary>
    /// Lab-only rewriter that replaces published provider dial targets with the isolation allowlist
    /// endpoint and clears credentials so AUTHINFO is not attempted against the local peer.
    /// </summary>
    /// <remarks>
    /// Does not mutate MySQL rows. Applied after <see cref="ProviderAccountMapper.Map"/> on each refresh
    /// when <see cref="LabUpstreamIsolationOptions.RewritePublishedDestinations"/> is true.
    /// Production behaviour is unchanged when that flag is false.
    /// </remarks>
    internal static class LabUpstreamDestinationRewriter
    {
        /// <summary>
        /// Returns <paramref name="providers"/> unchanged when rewrite is disabled; otherwise every
        /// definition is retargeted to the allowlisted cleartext loopback endpoint without credentials.
        /// </summary>
        /// <param name="providers">Mapped providers from the account source.</param>
        /// <param name="options">Lab isolation options already validated at startup when rewrite is enabled.</param>
        /// <returns>Effective definitions used for pool publication.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Rewrite is enabled but isolation is incomplete (should have failed <c>ValidateOnStart</c>).
        /// </exception>
        internal static IReadOnlyList<BackFillerProviderDefinition> Apply(
            IReadOnlyList<BackFillerProviderDefinition> providers,
            LabUpstreamIsolationOptions options)
        {
            ArgumentNullException.ThrowIfNull(providers);
            ArgumentNullException.ThrowIfNull(options);

            if (!options.RewritePublishedDestinations)
            {
                return providers;
            }

            if (!options.Enabled)
            {
                throw new InvalidOperationException(
                    "LabUpstreamIsolation RewritePublishedDestinations requires Enabled=true.");
            }

            if (options.AllowedPort is null or < 1 or > 65535)
            {
                throw new InvalidOperationException(
                    "LabUpstreamIsolation AllowedPort must be in 1..65535 when RewritePublishedDestinations is true.");
            }

            var host = LabUpstreamIsolationOptions.RequiredLiteralHost;
            var port = options.AllowedPort.Value;
            var rewritten = new BackFillerProviderDefinition[providers.Count];
            for (var i = 0; i < providers.Count; i++)
            {
                var source = providers[i];
                rewritten[i] = new BackFillerProviderDefinition(
                    source.Backbone,
                    host,
                    port,
                    UseTls: false,
                    Username: null,
                    Password: null,
                    source.MinSessions,
                    source.MaxSessions,
                    source.KeepAliveSeconds);
            }

            return rewritten;
        }
    }
}
