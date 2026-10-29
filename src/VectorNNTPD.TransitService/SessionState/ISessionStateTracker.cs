using System.Net;

namespace VectorNNTPD.TransitService.SessionState
{
    /// <summary>
    /// Atomically admits and releases concurrent authenticated reader sessions per account.
    /// </summary>
    /// <remarks>
    /// <c>session_limit</c> 0 and <c>srcip_limit</c> 0 are unlimited.
    /// <c>session_limit</c> is the cluster-wide cap on authenticated sessions.
    /// <c>srcip_limit</c> is a cluster-wide cap on distinct source IPs that currently
    /// have at least one admitted session, not a per-IP session cap. Production uses
    /// <see cref="DistributedSessionStateTracker"/>: both limits are enforced
    /// atomically through Redis ownership leases.
    /// </remarks>
    public interface ISessionStateTracker
    {
        /// <summary>
        /// Attempts to admit <paramref name="sessionId"/> for <paramref name="accountName"/>.
        /// Re-admitting the same session id is idempotent.
        /// </summary>
        ValueTask<SessionAdmissionResult> TryAdmitAsync(
            string accountName,
            string sessionId,
            IPAddress sourceAddress,
            int sessionLimit,
            int srcIpLimit,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Admits a session and, when <paramref name="rateLimitBps"/> (bits per second)
        /// is greater than zero, tracks the cluster session count for rate allocation.
        /// </summary>
        ValueTask<SessionAdmissionResult> TryAdmitAsync(
            string accountName,
            string sessionId,
            IPAddress sourceAddress,
            int sessionLimit,
            int srcIpLimit,
            int rateLimitBps,
            CancellationToken cancellationToken = default) =>
            TryAdmitAsync(accountName, sessionId, sourceAddress, sessionLimit, srcIpLimit, cancellationToken);

        /// <summary>Releases a previously admitted session. Idempotent when the session is unknown.</summary>
        ValueTask ReleaseAsync(string accountName, string sessionId, CancellationToken cancellationToken = default);
    }
}
