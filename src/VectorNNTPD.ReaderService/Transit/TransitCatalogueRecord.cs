namespace VectorNNTPD.ReaderService.Transit
{
    /// <summary>
    /// One published Transit catalogue row set, before pattern and endpoint compilation.
    /// </summary>
    public sealed class TransitCatalogueRecord
    {
        /// <summary>Empty published catalogue used by offline hosts. No peers; junk logging stays on.</summary>
        public static TransitCatalogueRecord Empty { get; } = new(1, 1, 1, 1, true, true, []);

        /// <summary>Initializes a catalogue row set.</summary>
        public TransitCatalogueRecord(
            long publicationId,
            long receiveRevision,
            long sendRevision,
            long globalRevision,
            bool wantTrash,
            bool logTrash,
            IReadOnlyList<TransitCataloguePeerRecord> peers)
        {
            ArgumentNullException.ThrowIfNull(peers);
            PublicationId = publicationId;
            ReceiveRevision = receiveRevision;
            SendRevision = sendRevision;
            GlobalRevision = globalRevision;
            WantTrash = wantTrash;
            LogTrash = logTrash;
            Peers = peers;
        }

        /// <summary>Gets <c>nntptransitpublication.publication_id</c>.</summary>
        public long PublicationId { get; }

        /// <summary>Gets the receive revision named by the publication.</summary>
        public long ReceiveRevision { get; }

        /// <summary>Gets the send revision named by the publication.</summary>
        public long SendRevision { get; }

        /// <summary>Gets the global revision named by the publication.</summary>
        public long GlobalRevision { get; }

        /// <summary>Gets the site-wide want-trash flag.</summary>
        public bool WantTrash { get; }

        /// <summary>Gets the site-wide log-trash flag.</summary>
        public bool LogTrash { get; }

        /// <summary>Gets every peer in the publication. Receive and send identifiers are the same set.</summary>
        public IReadOnlyList<TransitCataloguePeerRecord> Peers { get; }
    }

    /// <summary>One peer's identity plus both planes, still in catalogue column form.</summary>
    public sealed class TransitCataloguePeerRecord
    {
        /// <summary>Initializes one peer row.</summary>
        public TransitCataloguePeerRecord(
            string identifier,
            string peerName,
            int maxInbound,
            string receiveUsername,
            string receivePassword,
            bool deferOnDuplicate,
            long receiveMaxArticleBytes,
            int receiveArticleTypes,
            string receivePatterns,
            IReadOnlyList<string> allowFrom,
            int maxOutbound,
            string sslMode,
            string sendUsername,
            string sendPassword,
            long? sendMaxArticleBytes,
            int sendArticleTypes,
            string sendPatterns,
            string pathToken,
            IReadOnlyList<TransitCatalogueEndpointRecord> endpoints,
            IReadOnlyList<string> pathExclusions)
        {
            ArgumentNullException.ThrowIfNull(identifier);
            ArgumentNullException.ThrowIfNull(peerName);
            ArgumentNullException.ThrowIfNull(receiveUsername);
            ArgumentNullException.ThrowIfNull(receivePassword);
            ArgumentNullException.ThrowIfNull(receivePatterns);
            ArgumentNullException.ThrowIfNull(allowFrom);
            ArgumentNullException.ThrowIfNull(sslMode);
            ArgumentNullException.ThrowIfNull(sendUsername);
            ArgumentNullException.ThrowIfNull(sendPassword);
            ArgumentNullException.ThrowIfNull(sendPatterns);
            ArgumentNullException.ThrowIfNull(pathToken);
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentNullException.ThrowIfNull(pathExclusions);
            Identifier = identifier;
            PeerName = peerName;
            MaxInbound = maxInbound;
            ReceiveUsername = receiveUsername;
            ReceivePassword = receivePassword;
            DeferOnDuplicate = deferOnDuplicate;
            ReceiveMaxArticleBytes = receiveMaxArticleBytes;
            ReceiveArticleTypes = receiveArticleTypes;
            ReceivePatterns = receivePatterns;
            AllowFrom = allowFrom;
            MaxOutbound = maxOutbound;
            SslMode = sslMode;
            SendUsername = sendUsername;
            SendPassword = sendPassword;
            SendMaxArticleBytes = sendMaxArticleBytes;
            SendArticleTypes = sendArticleTypes;
            SendPatterns = sendPatterns;
            PathToken = pathToken;
            Endpoints = endpoints;
            PathExclusions = pathExclusions;
        }

        /// <summary>Gets the peer identifier.</summary>
        public string Identifier { get; }

        /// <summary>Gets the display name.</summary>
        public string PeerName { get; }

        /// <summary>Gets <c>max_inbound</c>. <c>0</c> closes receive.</summary>
        public int MaxInbound { get; }

        /// <summary>Gets the receive username.</summary>
        public string ReceiveUsername { get; }

        /// <summary>Gets the receive password. Never log this value.</summary>
        public string ReceivePassword { get; }

        /// <summary>Gets whether duplicate offers are deferred.</summary>
        public bool DeferOnDuplicate { get; }

        /// <summary>Gets the receive article size limit.</summary>
        public long ReceiveMaxArticleBytes { get; }

        /// <summary>Gets the receive article-type mask.</summary>
        public int ReceiveArticleTypes { get; }

        /// <summary>Gets the receive newsfeeds expression.</summary>
        public string ReceivePatterns { get; }

        /// <summary>Gets AllowFrom entries in ordinal order.</summary>
        public IReadOnlyList<string> AllowFrom { get; }

        /// <summary>Gets <c>max_outbound</c>. <c>0</c> closes send.</summary>
        public int MaxOutbound { get; }

        /// <summary>Gets <c>ssl_mode</c> (<c>None</c>, <c>Tls</c>, or <c>StartTls</c>).</summary>
        public string SslMode { get; }

        /// <summary>Gets the send username.</summary>
        public string SendUsername { get; }

        /// <summary>Gets the send password. Never log this value.</summary>
        public string SendPassword { get; }

        /// <summary>
        /// Gets the send article size limit in bytes.
        /// <see langword="null"/> is unlimited. A value is an explicit ceiling and is not filled from the receive limit.
        /// </summary>
        public long? SendMaxArticleBytes { get; }

        /// <summary>Gets the send article-type mask.</summary>
        public int SendArticleTypes { get; }

        /// <summary>Gets the send newsfeeds expression.</summary>
        public string SendPatterns { get; }

        /// <summary>Gets the Path token.</summary>
        public string PathToken { get; }

        /// <summary>Gets ConnectTo endpoints in ordinal order.</summary>
        public IReadOnlyList<TransitCatalogueEndpointRecord> Endpoints { get; }

        /// <summary>Gets extra Path exclusions in ordinal order.</summary>
        public IReadOnlyList<string> PathExclusions { get; }
    }

    /// <summary>One send endpoint stored as an unbracketed host and an explicit port.</summary>
    public sealed class TransitCatalogueEndpointRecord
    {
        /// <summary>Initializes an endpoint row.</summary>
        public TransitCatalogueEndpointRecord(string host, int port)
        {
            ArgumentNullException.ThrowIfNull(host);
            Host = host;
            Port = port;
        }

        /// <summary>Gets the unbracketed host.</summary>
        public string Host { get; }

        /// <summary>Gets the TCP port.</summary>
        public int Port { get; }
    }
}
