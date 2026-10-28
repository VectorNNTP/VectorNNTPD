using System.Net;
using System.Net.Sockets;

namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    /// <summary>
    /// Fail-closed preflight for local ARTICLE peer stress. Validates declared env, every published
    /// upstream in an operator snapshot, peer reachability, and port match against <c>127.0.0.1</c>.
    /// </summary>
    /// <remarks>
    /// This gate does <b>not</b> wrap the running BackFiller <see cref="VectorNNTPD.BackFillerService.Nntp.INntpTransportFactory"/>.
    /// Dial-time enforcement inside a separately running service requires a production-path allowlist
    /// (documented in ISOLATION_AND_ACCOUNTING_FIXES.md) and is out of scope until approved.
    /// </remarks>
    internal static class LocalUpstreamIsolationGate
    {
        /// <summary>Only accepted host for the planned local peer (literal IPv4; no DNS).</summary>
        internal const string RequiredLiteralHost = "127.0.0.1";

        /// <summary>Path to sanitized upstream snapshot JSON.</summary>
        internal const string UpstreamSnapshotPathEnvironmentVariable =
            "VECTORNNTPD_BACKFILLER_E2E_STRESS_UPSTREAM_SNAPSHOT";

        /// <summary>Max age of the snapshot before it is considered stale (default 15 minutes).</summary>
        internal const string SnapshotMaxAgeSecondsEnvironmentVariable =
            "VECTORNNTPD_BACKFILLER_E2E_STRESS_UPSTREAM_SNAPSHOT_MAX_AGE_SECONDS";

        /// <summary>Inputs for one isolation evaluation (fully injectable for tests).</summary>
        internal sealed record Requirements
        {
            public required bool RequireLocalUpstreamFlag { get; init; }

            public string? DeclaredHost { get; init; }

            public int? DeclaredPort { get; init; }

            public required UpstreamProviderSnapshot? Snapshot { get; init; }

            public required TimeSpan MaxSnapshotAge { get; init; }

            public required DateTimeOffset NowUtc { get; init; }

            /// <summary>
            /// Returns whether TCP connect to host:port succeeds. Injected so unit tests never dial WAN.
            /// </summary>
            public required Func<string, int, bool> PeerReachable { get; init; }
        }

        /// <summary>
        /// Builds <see cref="Requirements"/> from environment and optional snapshot path.
        /// Does not open MySQL. Peer probe defaults to a loopback TCP connect attempt.
        /// </summary>
        internal static Requirements FromEnvironment(Func<string, int, bool>? peerReachable = null)
        {
            var require = string.Equals(
                Environment.GetEnvironmentVariable(
                    LocalUpstreamIsolation.RequireLocalUpstreamEnvironmentVariable),
                "1",
                StringComparison.Ordinal);

            int? port = null;
            if (int.TryParse(
                    Environment.GetEnvironmentVariable(LocalUpstreamIsolation.LocalPeerPortEnvironmentVariable),
                    out var parsed)
                && parsed is >= 1 and <= 65535)
            {
                port = parsed;
            }

            UpstreamProviderSnapshot? snapshot = null;
            var snapshotPath = Environment.GetEnvironmentVariable(UpstreamSnapshotPathEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(snapshotPath) && File.Exists(snapshotPath))
            {
                snapshot = UpstreamProviderSnapshot.ReadAsync(snapshotPath, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }

            var maxAgeSeconds = 15 * 60;
            if (int.TryParse(
                    Environment.GetEnvironmentVariable(SnapshotMaxAgeSecondsEnvironmentVariable),
                    out var configuredAge)
                && configuredAge > 0)
            {
                maxAgeSeconds = configuredAge;
            }

            return new Requirements
            {
                RequireLocalUpstreamFlag = require,
                DeclaredHost = Environment.GetEnvironmentVariable(
                    LocalUpstreamIsolation.LocalPeerHostEnvironmentVariable),
                DeclaredPort = port,
                Snapshot = snapshot,
                MaxSnapshotAge = TimeSpan.FromSeconds(maxAgeSeconds),
                NowUtc = DateTimeOffset.UtcNow,
                PeerReachable = peerReachable ?? ProbeLoopbackTcp,
            };
        }

        /// <summary>
        /// Validates only the require-flag and declared <c>127.0.0.1</c>:port (workload discovery).
        /// Does not inspect published providers or peer reachability.
        /// </summary>
        internal static (string Host, int Port) EnsureDeclaredEndpointOrThrow(Requirements requirements)
        {
            ArgumentNullException.ThrowIfNull(requirements);

            if (!requirements.RequireLocalUpstreamFlag)
            {
                throw new InvalidOperationException(
                    $"Missing local-upstream requirement: set "
                    + $"{LocalUpstreamIsolation.RequireLocalUpstreamEnvironmentVariable}=1.");
            }

            if (string.IsNullOrWhiteSpace(requirements.DeclaredHost))
            {
                throw new InvalidOperationException(
                    $"Missing host: set {LocalUpstreamIsolation.LocalPeerHostEnvironmentVariable}={RequiredLiteralHost}.");
            }

            if (!string.Equals(requirements.DeclaredHost, RequiredLiteralHost, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Declared peer host must be literal {RequiredLiteralHost} (no DNS, no localhost, no IPv6). "
                    + $"Got '{requirements.DeclaredHost}'.");
            }

            if (requirements.DeclaredPort is null)
            {
                throw new InvalidOperationException(
                    $"Missing or invalid port: set {LocalUpstreamIsolation.LocalPeerPortEnvironmentVariable} "
                    + "to the peer TCP port (1..65535).");
            }

            return (RequiredLiteralHost, requirements.DeclaredPort.Value);
        }

        /// <summary>
        /// Fail-closed evaluation. Throws <see cref="InvalidOperationException"/> with actionable,
        /// non-secret messages when isolation cannot be established.
        /// </summary>
        internal static (string Host, int Port) EnsureReadyOrThrow(Requirements requirements)
        {
            ArgumentNullException.ThrowIfNull(requirements);
            ArgumentNullException.ThrowIfNull(requirements.PeerReachable);

            var (_, port) = EnsureDeclaredEndpointOrThrow(requirements);
            if (requirements.Snapshot is null)
            {
                throw new InvalidOperationException(
                    "Unknown or uninspectable effective upstream destination: provide a sanitized "
                    + $"provider snapshot via {UpstreamSnapshotPathEnvironmentVariable}. "
                    + "Without it the harness cannot verify every published account is local-only.");
            }

            ValidateSnapshot(requirements.Snapshot, port, requirements.NowUtc, requirements.MaxSnapshotAge);

            if (!requirements.PeerReachable(RequiredLiteralHost, port))
            {
                throw new InvalidOperationException(
                    $"Local peer is unavailable at {RequiredLiteralHost}:{port}. "
                    + "Refusing to submit work (no external upstream fallback).");
            }

            return (RequiredLiteralHost, port);
        }

        /// <summary>Validates every published provider against the intended peer endpoint.</summary>
        internal static void ValidateSnapshot(
            UpstreamProviderSnapshot snapshot,
            int expectedPort,
            DateTimeOffset nowUtc,
            TimeSpan maxAge)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            if (string.IsNullOrWhiteSpace(snapshot.Source)
                || string.Equals(snapshot.Source, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Upstream snapshot Source is missing or unknown; refuse uninspectable effective destination.");
            }

            if (string.IsNullOrWhiteSpace(snapshot.PoolRefreshEvidence))
            {
                throw new InvalidOperationException(
                    "Upstream snapshot lacks PoolRefreshEvidence (stale or unverified session-pool configuration). "
                    + "Record log events 5601/5603 or equivalent after account refresh.");
            }

            var age = nowUtc - snapshot.CapturedUtc;
            if (age < TimeSpan.Zero || age > maxAge)
            {
                throw new InvalidOperationException(
                    $"Upstream snapshot is stale or from the future (age={age}, max={maxAge}). Recapture after pool refresh.");
            }

            if (snapshot.Providers is null || snapshot.Providers.Count == 0)
            {
                throw new InvalidOperationException(
                    "Upstream snapshot lists zero providers; cannot prove local-only dialing.");
            }

            foreach (var provider in snapshot.Providers)
            {
                if (string.IsNullOrWhiteSpace(provider.Backbone))
                {
                    throw new InvalidOperationException("Upstream snapshot contains a provider with empty Backbone.");
                }

                if (string.IsNullOrWhiteSpace(provider.Host))
                {
                    throw new InvalidOperationException(
                        $"Provider '{provider.Backbone}' has a missing host.");
                }

                if (!string.Equals(provider.Host, RequiredLiteralHost, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Provider '{provider.Backbone}' host '{provider.Host}' is not {RequiredLiteralHost}. "
                        + "Every published account for the lab ServerId must target the local peer.");
                }

                if (provider.Port != expectedPort)
                {
                    throw new InvalidOperationException(
                        $"Provider '{provider.Backbone}' port {provider.Port} does not match peer port {expectedPort}.");
                }

                if (provider.UseTls)
                {
                    throw new InvalidOperationException(
                        $"Provider '{provider.Backbone}' has UseTls=true; local peer is cleartext (usessl=n required).");
                }

                // Reject IPv6 literals if somehow present as Host string.
                if (IPAddress.TryParse(provider.Host, out var address)
                    && address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    throw new InvalidOperationException(
                        $"Provider '{provider.Backbone}' uses IPv6; planned peer requires {RequiredLiteralHost} only.");
                }
            }
        }

        /// <summary>Default peer probe: TCP connect to loopback only (never used for non-loopback hosts).</summary>
        internal static bool ProbeLoopbackTcp(string host, int port)
        {
            if (!string.Equals(host, RequiredLiteralHost, StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(IPAddress.Loopback, port);
                return connect.Wait(TimeSpan.FromSeconds(2)) && client.Connected;
            }
            catch (Exception ex) when (ex is SocketException or AggregateException or ObjectDisposedException)
            {
                return false;
            }
        }
    }
}
