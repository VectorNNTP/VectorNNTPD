using System.Net;

namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    /// <summary>
    /// Fail-closed helpers so lab benchmarks cannot silently target a real upstream.
    /// </summary>
    internal static class LocalUpstreamIsolation
    {
        /// <summary>Opt-in flag required alongside a loopback provider host for local-peer stress plans.</summary>
        internal const string RequireLocalUpstreamEnvironmentVariable =
            "VECTORNNTPD_BACKFILLER_E2E_STRESS_REQUIRE_LOCAL_UPSTREAM";

        /// <summary>
        /// Declared local peer hostname that BackFiller must use (lab MySQL <c>hostname</c>).
        /// Required when <see cref="RequireLocalUpstreamEnvironmentVariable"/> is <c>1</c>.
        /// </summary>
        internal const string LocalPeerHostEnvironmentVariable =
            "VECTORNNTPD_BACKFILLER_E2E_STRESS_LOCAL_PEER_HOST";

        /// <summary>
        /// Declared local peer TCP port matching the running <see cref="LocalArticleNntpPeer"/>.
        /// Required when <see cref="RequireLocalUpstreamEnvironmentVariable"/> is <c>1</c>.
        /// </summary>
        internal const string LocalPeerPortEnvironmentVariable =
            "VECTORNNTPD_BACKFILLER_E2E_STRESS_LOCAL_PEER_PORT";

        /// <summary>
        /// Returns whether <paramref name="host"/> is loopback (<c>127.0.0.1</c>, <c>::1</c>, <c>localhost</c>).
        /// </summary>
        internal static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
        }

        /// <summary>
        /// Throws when <see cref="RequireLocalUpstreamEnvironmentVariable"/> is <c>1</c> and
        /// <paramref name="providerHost"/> is not loopback.
        /// </summary>
        internal static void EnsureLocalUpstreamOrThrow(string providerHost)
        {
            var require = Environment.GetEnvironmentVariable(RequireLocalUpstreamEnvironmentVariable);
            if (!string.Equals(require, "1", StringComparison.Ordinal))
            {
                return;
            }

            if (!IsLoopbackHost(providerHost))
            {
                throw new InvalidOperationException(
                    $"Local-upstream stress mode is enabled ({RequireLocalUpstreamEnvironmentVariable}=1) "
                    + $"but provider host '{providerHost}' is not loopback. Refusing to proceed.");
            }
        }

        /// <summary>
        /// Fail-closed gate for local-peer stress: requires the flag, a loopback declared host,
        /// and a valid peer port. Does not contact the network.
        /// </summary>
        /// <returns>Declared loopback host and port.</returns>
        internal static (string Host, int Port) RequireLocalPeerEndpointOrThrow()
        {
            var require = Environment.GetEnvironmentVariable(RequireLocalUpstreamEnvironmentVariable);
            if (!string.Equals(require, "1", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Set {RequireLocalUpstreamEnvironmentVariable}=1 to run local-peer stress phases.");
            }

            var host = Environment.GetEnvironmentVariable(LocalPeerHostEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(host) || !IsLoopbackHost(host))
            {
                throw new InvalidOperationException(
                    $"{LocalPeerHostEnvironmentVariable} must be a loopback host when local-upstream mode is enabled.");
            }

            if (!int.TryParse(Environment.GetEnvironmentVariable(LocalPeerPortEnvironmentVariable), out var port)
                || port is < 1 or > 65535)
            {
                throw new InvalidOperationException(
                    $"{LocalPeerPortEnvironmentVariable} must be an integer TCP port in 1..65535.");
            }

            return (host, port);
        }
    }
}
