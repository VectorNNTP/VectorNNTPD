using VectorNNTPD.Common.Articles;
using VectorNNTPD.TransitService.Configuration;

namespace VectorNNTPD.TransitService.Transit
{
    /// <summary>
    /// Immutable policy snapshot for one named Transit peer.
    /// </summary>
    /// <remarks>
    /// Do not log <see cref="Password"/>. Use <see cref="HasPeerCredentials"/> in diagnostics.
    /// </remarks>
    public sealed class TransitPeerPolicy
    {
        /// <summary>Maximum accepted inbound or outbound connection limit.</summary>
        public const int MaxConnectionLimit = 4096;

        internal TransitPeerPolicy(
            string identifier,
            string peerName,
            int maxIncomingConnections,
            int maxOutgoingConnections,
            IReadOnlyList<IpPrefix> literalPrefixes,
            IReadOnlyList<string> dnsHostnames,
            IReadOnlyList<TransitConnectEndpoint> connectTo,
            string username,
            string password,
            TransitSslMode ssl,
            NewsfeedsPattern patterns,
            bool deferOnDuplicate,
            string pathToken,
            long maxSize,
            TransitMessageTypes messageTypes,
            NewsfeedsPattern receivePatterns,
            TransitMessageTypes receiveArticleTypes,
            long? sendMaxArticleBytes,
            string sendUsername,
            string sendPassword,
            IReadOnlyList<string> pathExclusions)
        {
            ArgumentNullException.ThrowIfNull(receivePatterns);
            ArgumentNullException.ThrowIfNull(sendUsername);
            ArgumentNullException.ThrowIfNull(sendPassword);
            ArgumentNullException.ThrowIfNull(pathExclusions);
            Identifier = identifier;
            PeerName = peerName;
            MaxIncomingConnections = maxIncomingConnections;
            MaxOutgoingConnections = maxOutgoingConnections;
            LiteralPrefixes = literalPrefixes;
            DnsHostnames = dnsHostnames;
            ConnectTo = connectTo;
            Username = username;
            Password = password;
            Ssl = ssl;
            Patterns = patterns;
            DeferOnDuplicate = deferOnDuplicate;
            PathToken = pathToken;
            MaxSize = maxSize;
            MessageTypes = messageTypes;
            ReceivePatterns = receivePatterns;
            ReceiveArticleTypes = receiveArticleTypes;
            SendMaxArticleBytes = sendMaxArticleBytes;
            SendUsername = sendUsername;
            SendPassword = sendPassword;
            PathExclusions = pathExclusions;
        }

        /// <summary>
        /// Gets the stable protocol/machine identifier (Transit dictionary key).
        /// </summary>
        /// <remarks>Exact configured string. Not derived from <see cref="PeerName"/>.</remarks>
        public string Identifier { get; }

        /// <summary>
        /// Gets the human-readable administrative display name.
        /// </summary>
        /// <remarks>Exact configured <c>PeerName</c>. Not a protocol argument.</remarks>
        public string PeerName { get; }

        /// <summary>Gets the inbound connection limit for this peer.</summary>
        public int MaxIncomingConnections { get; }

        /// <summary>
        /// Gets the outbound connection limit for this peer.
        /// <c>0</c> closes send. Outbound feeding is not executed from this value.
        /// </summary>
        public int MaxOutgoingConnections { get; }

        /// <summary>Gets literal AllowFrom prefixes (addresses and CIDRs).</summary>
        public IReadOnlyList<IpPrefix> LiteralPrefixes { get; }

        /// <summary>Gets AllowFrom DNS hostnames (resolved separately).</summary>
        public IReadOnlyList<string> DnsHostnames { get; }

        /// <summary>Gets parsed ConnectTo endpoints (not connected).</summary>
        public IReadOnlyList<TransitConnectEndpoint> ConnectTo { get; }

        /// <summary>Gets the Receive AUTHINFO username, or empty when receive credentials are not configured.</summary>
        public string Username { get; }

        /// <summary>
        /// Gets the peer AUTHINFO password, or empty when credentials are not configured.
        /// Never write this value to logs.
        /// </summary>
        public string Password { get; }

        /// <summary>Gets a value indicating whether both username and password are configured.</summary>
        public bool HasPeerCredentials => Username.Length > 0 && Password.Length > 0;

        /// <summary>Gets the canonical TLS mode.</summary>
        public TransitSslMode Ssl { get; }

        /// <summary>Gets the Send newsfeeds expression. Inbound acceptance does not apply it.</summary>
        public NewsfeedsPattern Patterns { get; }

        /// <summary>
        /// Gets whether a History hit on CHECK and IHAVE is deferred
        /// (<c>431</c>/<c>436</c>) instead of rejected (<c>438</c>/<c>435</c>).
        /// TAKETHIS does not read this value.
        /// </summary>
        public bool DeferOnDuplicate { get; }

        /// <summary>
        /// Gets the exact configured Path-header token (not consumed by routing yet).
        /// </summary>
        public string PathToken { get; }

        /// <summary>
        /// Gets the Receive article size limit in bytes.
        /// Inbound acceptance uses the smaller of this value and the published global <c>maxartsize</c>.
        /// A non-positive value, or a value above <see cref="int.MaxValue"/>, adds no peer ceiling.
        /// </summary>
        public long MaxSize { get; }

        /// <summary>Gets the Send article-type mask. Inbound acceptance does not apply it.</summary>
        public TransitMessageTypes MessageTypes { get; }

        /// <summary>
        /// Gets the Receive newsfeeds expression.
        /// TAKETHIS and IHAVE accept an article only when <see cref="NewsfeedsPattern.EvaluateArticle"/> returns match.
        /// </summary>
        public NewsfeedsPattern ReceivePatterns { get; }

        /// <summary>
        /// Gets the Receive article-type mask. <c>65535</c> is unrestricted.
        /// TAKETHIS and IHAVE require every <see cref="ArticleRecord.ArtType"/> flag to be present, via <see cref="ArticleTypeCapabilities.Allows"/>.
        /// </summary>
        public TransitMessageTypes ReceiveArticleTypes { get; }

        /// <summary>
        /// Gets the Send article size limit in bytes. Independent of <see cref="MaxSize"/>.
        /// <see langword="null"/> is unlimited. A value is an explicit ceiling. Outbound feeding does not apply it.
        /// </summary>
        public long? SendMaxArticleBytes { get; }

        /// <summary>Gets the Send username, or empty when send credentials are not configured.</summary>
        public string SendUsername { get; }

        /// <summary>
        /// Gets the Send password, or empty when send credentials are not configured.
        /// Never write this value to logs.
        /// </summary>
        public string SendPassword { get; }

        /// <summary>Gets a value indicating whether both send username and password are configured.</summary>
        public bool HasSendCredentials => SendUsername.Length > 0 && SendPassword.Length > 0;

        /// <summary>Gets additional Path hops that suppress sending to this peer.</summary>
        public IReadOnlyList<string> PathExclusions { get; }

        /// <inheritdoc />
        public override string ToString() => Identifier;

        /// <summary>Returns whether <paramref name="username"/> and <paramref name="password"/> match this peer.</summary>
        public bool CredentialsMatch(string username, string password)
        {
            ArgumentNullException.ThrowIfNull(username);
            ArgumentNullException.ThrowIfNull(password);
            return HasPeerCredentials
                   && string.Equals(Username, username, StringComparison.Ordinal)
                   && string.Equals(Password, password, StringComparison.Ordinal);
        }
    }
}
