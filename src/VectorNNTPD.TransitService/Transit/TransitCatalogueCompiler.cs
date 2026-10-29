using System.Globalization;
using VectorNNTPD.TransitService.Configuration;

namespace VectorNNTPD.TransitService.Transit
{
    /// <summary>Compiles one Transit catalogue record into the immutable snapshot.</summary>
    internal static class TransitCatalogueCompiler
    {
        /// <summary>Maximum article-type mask. Matches <see cref="TransitMessageTypes.All"/>.</summary>
        public const int UnrestrictedArticleTypes = 65535;

        /// <summary>Compiles <paramref name="record"/> or throws when a plane is not a valid snapshot.</summary>
        public static TransitConfigurationSnapshot Compile(TransitCatalogueRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (record.PublicationId < 1)
            {
                throw new InvalidOperationException("Transit publication_id must be at least 1.");
            }

            var peers = new Dictionary<string, TransitPeerPolicy>(record.Peers.Count, StringComparer.Ordinal);
            foreach (var peer in record.Peers)
            {
                if (!peers.TryAdd(peer.Identifier, CompilePeer(peer)))
                {
                    throw new InvalidOperationException(
                        "Transit publication contains a duplicate peer identifier '" + peer.Identifier + "'.");
                }
            }

            return new TransitConfigurationSnapshot(peers, record.WantTrash, record.LogTrash, record.PublicationId);
        }

        private static TransitPeerPolicy CompilePeer(TransitCataloguePeerRecord peer)
        {
            ValidateLimit(peer.Identifier, "max_inbound", peer.MaxInbound);
            ValidateLimit(peer.Identifier, "max_outbound", peer.MaxOutbound);
            ValidateSize(peer.Identifier, "receive max_article_bytes", peer.ReceiveMaxArticleBytes);
            ValidateSendSize(peer.Identifier, peer.SendMaxArticleBytes);
            var receiveTypes = ParseArticleTypes(peer.Identifier, "receive", peer.ReceiveArticleTypes);
            var sendTypes = ParseArticleTypes(peer.Identifier, "send", peer.SendArticleTypes);
            var receivePatterns = ParsePatterns(peer.Identifier, "receive", peer.ReceivePatterns);
            var sendPatterns = ParsePatterns(peer.Identifier, "send", peer.SendPatterns);
            ValidateCredentials(peer.Identifier, "receive", peer.ReceiveUsername, peer.ReceivePassword);
            ValidateCredentials(peer.Identifier, "send", peer.SendUsername, peer.SendPassword);
            if (!Enum.TryParse<TransitSslMode>(peer.SslMode, ignoreCase: false, out var ssl) || !Enum.IsDefined(ssl))
            {
                throw new InvalidOperationException(
                    "Transit peer '" + peer.Identifier + "' ssl_mode is not None, Tls, or StartTls.");
            }

            if (string.IsNullOrWhiteSpace(peer.PeerName))
            {
                throw new InvalidOperationException("Transit peer '" + peer.Identifier + "' peer_name is empty.");
            }

            var literals = new List<IpPrefix>();
            var hostnames = new List<string>();
            for (var i = 0; i < peer.AllowFrom.Count; i++)
            {
                if (!TransitAllowFromEntry.TryParse(peer.AllowFrom[i], out var entry, out var error))
                {
                    throw new InvalidOperationException(
                        "Transit peer '" + peer.Identifier + "' AllowFrom[" + i.ToString(CultureInfo.InvariantCulture)
                        + "] is invalid: " + (error ?? "unparsed."));
                }

                switch (entry)
                {
                    case TransitAllowFromEntry.Literal literal:
                        literals.Add(literal.Prefix);
                        break;
                    case TransitAllowFromEntry.Hostname hostname:
                        if (!hostnames.Contains(hostname.DnsName, StringComparer.OrdinalIgnoreCase))
                        {
                            hostnames.Add(hostname.DnsName);
                        }

                        break;
                    default:
                        throw new InvalidOperationException(
                            "Transit peer '" + peer.Identifier + "' AllowFrom entry was not parsed.");
                }
            }

            var connectTo = new List<TransitConnectEndpoint>(peer.Endpoints.Count);
            var seenEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < peer.Endpoints.Count; i++)
            {
                var endpoint = peer.Endpoints[i];
                var key = endpoint.Host + "\n" + endpoint.Port.ToString(CultureInfo.InvariantCulture);
                if (!seenEndpoints.Add(key))
                {
                    throw new InvalidOperationException(
                        "Transit peer '" + peer.Identifier + "' has a duplicate Send endpoint.");
                }

                var text = endpoint.Host.Contains(':', StringComparison.Ordinal)
                    ? "[" + endpoint.Host + "]:" + endpoint.Port.ToString(CultureInfo.InvariantCulture)
                    : endpoint.Host + ":" + endpoint.Port.ToString(CultureInfo.InvariantCulture);
                if (!TransitConnectEndpoint.TryParse(text, out var parsed, out var error) || parsed is null)
                {
                    throw new InvalidOperationException(
                        "Transit peer '" + peer.Identifier + "' Send endpoint is invalid: " + (error ?? "unparsed."));
                }

                connectTo.Add(parsed);
            }

            var exclusions = new List<string>(peer.PathExclusions.Count);
            var seenExclusions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in peer.PathExclusions)
            {
                if (string.IsNullOrWhiteSpace(token) || !seenExclusions.Add(token))
                {
                    throw new InvalidOperationException(
                        "Transit peer '" + peer.Identifier + "' has a duplicate or empty Path exclusion.");
                }

                exclusions.Add(token);
            }

            return new TransitPeerPolicy(
                peer.Identifier,
                peer.PeerName,
                peer.MaxInbound,
                peer.MaxOutbound,
                literals,
                hostnames,
                connectTo,
                peer.ReceiveUsername,
                peer.ReceivePassword,
                ssl,
                sendPatterns,
                peer.DeferOnDuplicate,
                peer.PathToken,
                peer.ReceiveMaxArticleBytes,
                sendTypes,
                receivePatterns,
                receiveTypes,
                peer.SendMaxArticleBytes,
                peer.SendUsername,
                peer.SendPassword,
                exclusions);
        }

        private static void ValidateLimit(string identifier, string column, int value)
        {
            if (value < 0 || value > TransitPeerPolicy.MaxConnectionLimit)
            {
                throw new InvalidOperationException(
                    "Transit peer '" + identifier + "' " + column + " is outside 0–"
                    + TransitPeerPolicy.MaxConnectionLimit.ToString(CultureInfo.InvariantCulture) + ".");
            }
        }

        private static void ValidateSize(string identifier, string column, long value)
        {
            if (value < 1 || value > int.MaxValue)
            {
                throw new InvalidOperationException(
                    "Transit peer '" + identifier + "' " + column + " is outside 1–2147483647.");
            }
        }

        /// <summary>
        /// Accepts SQL <c>NULL</c> as unlimited. An explicit send ceiling uses the same positive range as receive.
        /// </summary>
        private static void ValidateSendSize(string identifier, long? value)
        {
            if (value is null)
            {
                return;
            }

            ValidateSize(identifier, "send max_article_bytes", value.Value);
        }

        private static TransitMessageTypes ParseArticleTypes(string identifier, string plane, int mask)
        {
            if (mask < 0 || mask > UnrestrictedArticleTypes)
            {
                throw new InvalidOperationException(
                    "Transit peer '" + identifier + "' " + plane + " article_types is outside 0–65535.");
            }

            return (TransitMessageTypes)mask;
        }

        private static NewsfeedsPattern ParsePatterns(string identifier, string plane, string expression)
        {
            NewsfeedsPattern? pattern = null;
            string? error = null;
            if (string.IsNullOrWhiteSpace(expression)
                || !NewsfeedsPattern.TryParse(expression, out pattern, out error)
                || pattern is null)
            {
                throw new InvalidOperationException(
                    "Transit peer '" + identifier + "' " + plane + " patterns are invalid: "
                    + (error ?? "empty."));
            }

            return pattern;
        }

        private static void ValidateCredentials(string identifier, string plane, string username, string password)
        {
            var userSet = username.Length > 0;
            var passwordSet = password.Length > 0;
            if (userSet != passwordSet)
            {
                throw new InvalidOperationException(
                    "Transit peer '" + identifier + "' " + plane + " credentials must both be set or both be blank.");
            }
        }
    }
}
