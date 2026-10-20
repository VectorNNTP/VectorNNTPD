using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.RabbitMq;

namespace VectorNNTPD.BackFillerService.Accounts
{
    /// <summary>
    /// Maps <c>nntpbackfilleraccounts</c> rows onto Phase 4 <see cref="BackFillerProviderDefinition"/> values.
    /// Unknown, duplicate, and invalid rows are rejected instead of published.
    /// </summary>
    internal static class ProviderAccountMapper
    {
        /// <summary>
        /// Maps a query result. The first valid row for a canonical backbone wins.
        /// <c>maxconnections</c> becomes <see cref="BackFillerProviderDefinition.MaxSessions"/>.
        /// <c>keepalive</c> becomes <see cref="BackFillerProviderDefinition.KeepAliveSeconds"/>.
        /// <c>MinSessions</c> is unused leftover (table has no min column). <c>MaxSessions</c> is the eager desired count.
        /// </summary>
        /// <param name="rows">Parsed rows. Not mutated. Invalid rows are reported in the result rather than thrown.</param>
        /// <returns>Accepted definitions and one rejection record per skipped row.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="rows"/> is null.</exception>
        internal static ProviderAccountMapResult Map(IReadOnlyList<ProviderAccountRow> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            var providers = new List<BackFillerProviderDefinition>();
            var rejected = new List<RejectedProviderAccountRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                if (!TryMap(row, out var provider, out var reason))
                {
                    rejected.Add(new RejectedProviderAccountRow(row.Backbone ?? string.Empty, reason));
                    continue;
                }

                if (!seen.Add(provider.Backbone))
                {
                    rejected.Add(new RejectedProviderAccountRow(provider.Backbone, "Duplicate backbone for this server id."));
                    continue;
                }

                providers.Add(provider);
            }

            return new ProviderAccountMapResult(providers, rejected);
        }

        /// <summary>Resolves a backbone token to the canonical Phase 2/3 label.</summary>
        /// <param name="backbone">Raw backbone text. Leading and trailing white space is ignored.</param>
        /// <param name="canonical">
        /// Matching <see cref="BackFillerRabbitMqTopology.ProviderBackbones"/> label when this method returns
        /// <see langword="true"/>. Empty when <paramref name="backbone"/> is null or white space.
        /// The trimmed input when it matches no known backbone.
        /// </param>
        /// <returns><see langword="true"/> when the trimmed text matches a known backbone, ordinal-ignore-case.</returns>
        private static bool TryCanonicalizeBackbone(string? backbone, out string canonical)
        {
            if (string.IsNullOrWhiteSpace(backbone))
            {
                canonical = string.Empty;
                return false;
            }

            var trimmed = backbone.Trim();
            var match = BackFillerRabbitMqTopology.ProviderBackbones.FirstOrDefault(candidate =>
                string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                canonical = trimmed;
                return false;
            }

            canonical = match;
            return true;
        }

        /// <summary>Parses the persisted <c>usessl</c> enum.</summary>
        /// <param name="raw">Stored <c>usessl</c> text. Leading and trailing white space is ignored.</param>
        /// <param name="useSsl"><see langword="true"/> for <c>y</c> and <see langword="false"/> for <c>n</c> or any rejected value.</param>
        /// <returns><see langword="true"/> only for <c>y</c> or <c>n</c> after trimming. The comparison is case-sensitive.</returns>
        private static bool TryParseUseSsl(string? raw, out bool useSsl)
        {
            switch (raw?.Trim())
            {
                case "y":
                    useSsl = true;
                    return true;
                case "n":
                    useSsl = false;
                    return true;
                default:
                    useSsl = false;
                    return false;
            }
        }

        /// <summary>
        /// Validates one row and builds a definition with <see cref="BackFillerProviderDefinition.MinSessions"/> set to zero.
        /// </summary>
        /// <param name="row">
        /// Parsed row. Hostname is trimmed on success.
        /// When either credential is SQL <see langword="null"/>, authentication is disabled and both credential fields on the
        /// definition are <see langword="null"/>. When both credentials are non-null, the username is trimmed and the password
        /// is copied unchanged.
        /// </param>
        /// <param name="provider">The definition when this method returns <see langword="true"/>; otherwise the default null assignment.</param>
        /// <param name="reason">Empty on success. On failure, a stable rejection sentence and <paramref name="provider"/> is not published.</param>
        /// <returns><see langword="true"/> when every field below is accepted.</returns>
        /// <remarks>
        /// Rejects an unknown backbone, a missing hostname, a port outside 1–65535,
        /// <c>maxconnections</c> below 1, a missing keepalive, and a <c>usessl</c> value other than <c>y</c> or <c>n</c>.
        /// A SQL <see langword="null"/> username or password disables authentication without rejecting the row and
        /// clears both runtime credential fields. A non-null empty, whitespace, or control-laden credential is still
        /// rejected even when the other credential cell is SQL <see langword="null"/>.
        /// </remarks>
        private static bool TryMap(ProviderAccountRow row, out BackFillerProviderDefinition provider, out string reason)
        {
            provider = null!;
            if (!TryCanonicalizeBackbone(row.Backbone, out var backbone))
            {
                reason = "Unknown or unsupported backbone.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(row.Hostname))
            {
                reason = "Hostname is required.";
                return false;
            }

            if (row.Port is null or < 1 or > 65535)
            {
                reason = "Port must be between 1 and 65535.";
                return false;
            }

            if (row.MaxConnections is null or < 1)
            {
                reason = "MaxConnections must be at least 1.";
                return false;
            }

            if (row.KeepAliveSeconds is null)
            {
                reason = "KeepAlive is required.";
                return false;
            }

            if (!TryResolveCredentials(row.Username, row.Password, out var username, out var password, out reason))
            {
                return false;
            }

            if (!TryParseUseSsl(row.UseSslRaw, out var useTls))
            {
                reason = "UseSsl must be 'y' or 'n'.";
                return false;
            }

            provider = new BackFillerProviderDefinition(
                backbone,
                row.Hostname.Trim(),
                row.Port.Value,
                useTls,
                username,
                password,
                MinSessions: 0,
                MaxSessions: row.MaxConnections.Value,
                KeepAliveSeconds: row.KeepAliveSeconds.Value);
            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// Resolves credential cells into the runtime authentication pair.
        /// </summary>
        /// <param name="username">Database username cell. <see langword="null"/> disables authentication.</param>
        /// <param name="password">Database password cell. <see langword="null"/> disables authentication.</param>
        /// <param name="resolvedUsername">Trimmed username when authentication is enabled; otherwise <see langword="null"/>.</param>
        /// <param name="resolvedPassword">Password when authentication is enabled; otherwise <see langword="null"/>.</param>
        /// <param name="reason">Rejection reason when a non-null credential is invalid.</param>
        /// <returns><see langword="false"/> only when a non-null credential fails validation.</returns>
        private static bool TryResolveCredentials(
            string? username,
            string? password,
            out string? resolvedUsername,
            out string? resolvedPassword,
            out string reason)
        {
            resolvedUsername = null;
            resolvedPassword = null;

            // Validate any non-null credential cell before deciding auth mode. A present blank or
            // control-laden value is still rejected even when the other credential is SQL NULL.
            if (username is not null)
            {
                if (string.IsNullOrWhiteSpace(username))
                {
                    reason = "Username is required.";
                    return false;
                }

                if (ContainsAsciiControlCharacters(username))
                {
                    reason = "Username contains invalid control characters.";
                    return false;
                }
            }

            if (password is not null)
            {
                if (string.IsNullOrWhiteSpace(password))
                {
                    reason = "Password is required.";
                    return false;
                }

                if (ContainsAsciiControlCharacters(password))
                {
                    reason = "Password contains invalid control characters.";
                    return false;
                }
            }

            // SQL NULL on either credential disables authentication and drops both runtime values.
            if (username is null || password is null)
            {
                reason = string.Empty;
                return true;
            }

            resolvedUsername = username.Trim();
            resolvedPassword = password;
            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// Returns <see langword="true"/> if the string contains any ASCII control characters (0x00–0x1F or 0x7F).
        /// </summary>
        /// <param name="value">Username or password text being validated.</param>
        /// <returns>
        /// <see langword="true"/> when <paramref name="value"/> is non-empty and any character is a C0 control or DEL.
        /// </returns>
        private static bool ContainsAsciiControlCharacters(string? value)
        {
            // C0 controls (0x00–0x1F) and DEL (0x7F).
            return !string.IsNullOrEmpty(value)
                && value.Any(static ch => ch <= 0x1F || ch == 0x7F);
        }
    }
}
