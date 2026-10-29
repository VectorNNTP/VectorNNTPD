using System.Net;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Transit;

namespace VectorNNTPD.ReaderService.Tests.Transit;

internal static class TransitTestPeers
{
    /// <summary>Canonical integration-test Transit identifier for the source-IP → AllowFrom path.</summary>
    public const string DefaultPeerName = "test-peer";

    /// <summary>Canonical integration-test display name (not a protocol argument).</summary>
    public const string DefaultPeerDisplayName = "Test Peer";

    public static TransitPeerOptions Peer(
        int maxIncoming = 8,
        int maxOutgoing = 1,
        string[]? allowFrom = null,
        string[]? connectTo = null,
        string username = "",
        string password = "",
        string ssl = "",
        string patterns = "*",
        bool deferOnDuplicate = true,
        string pathToken = "",
        long maxSize = TransitPeerOptions.DefaultMaxSize,
        string[]? messageTypes = null,
        string peerName = DefaultPeerDisplayName) =>
        new()
        {
            PeerName = peerName,
            MaxIncomingConnections = maxIncoming,
            MaxOutgoingConnections = maxOutgoing,
            AllowFrom = allowFrom ?? [],
            ConnectTo = connectTo ?? [],
            Username = username,
            Password = password,
            Ssl = ssl,
            Patterns = patterns,
            DeferOnDuplicate = deferOnDuplicate,
            PathToken = pathToken,
            MaxSize = maxSize,
            MessageTypes = messageTypes ?? ["default"],
        };

    public static TransitPeersOptions Dictionary(string name, TransitPeerOptions peer)
    {
        return new TransitPeersOptions { [name] = peer };
    }

    public static TransitConfigurationSnapshot Snapshot(string name, TransitPeerOptions peer) =>
        TransitConfigurationSnapshot.Create(Dictionary(name, peer));

    /// <summary>
    /// Named-peer authorization whose <c>AllowFrom</c> is the supplied source addresses.
    /// Sessions identify this peer only when the connection's effective client IP matches.
    /// </summary>
    public static TransitPeerAuthorization ForAllowFrom(
        IPAddress source,
        string name = DefaultPeerName,
        int maxIncoming = 10,
        int maxOutgoing = 0,
        string username = "",
        string password = "",
        string patterns = "*",
        bool deferOnDuplicate = true) =>
        ForAllowFrom(
            [source],
            name,
            maxIncoming,
            maxOutgoing,
            username,
            password,
            patterns,
            deferOnDuplicate);

    /// <summary>Named-peer authorization for one or more literal AllowFrom addresses.</summary>
    public static TransitPeerAuthorization ForAllowFrom(
        IReadOnlyList<IPAddress> sources,
        string name = DefaultPeerName,
        int maxIncoming = 10,
        int maxOutgoing = 0,
        string username = "",
        string password = "",
        string patterns = "*",
        bool deferOnDuplicate = true)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return TransitPeerAuthorization.CreateStatic(
            Snapshot(
                name,
                Peer(
                    maxIncoming: maxIncoming,
                    maxOutgoing: maxOutgoing,
                    allowFrom: [.. sources.Select(static a => a.ToString())],
                    username: username,
                    password: password,
                    patterns: patterns,
                    deferOnDuplicate: deferOnDuplicate)));
    }
}
