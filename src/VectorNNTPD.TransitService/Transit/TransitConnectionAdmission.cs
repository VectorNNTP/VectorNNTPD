using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.CommandProcessor;

namespace VectorNNTPD.TransitService.Transit
{
    /// <summary>
    /// Admits an inbound connection against the identified Transit peer's MaxIncomingConnections.
    /// </summary>
    internal static class TransitConnectionAdmission
    {
        /// <summary>
        /// Consumes a cluster slot when the session is associated with a Transit peer.
        /// Non-transit clients are admitted without a slot and never create peer state.
        /// </summary>
        public static async ValueTask<TransitInboundAdmitResult> TryAdmitAsync(
            ITransitInboundConnectionLimiter limiter,
            NntpSession session,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(limiter);
            ArgumentNullException.ThrowIfNull(session);
            var peerName = session.Authorization.TransitPeerName;
            if (peerName is null)
            {
                return TransitInboundAdmitResult.Uncounted;
            }

            return await limiter.TryAcquireAsync(peerName, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Writes RFC 3977/4644 <c>400 Service temporarily unavailable</c> and completes.
        /// </summary>
        public static async ValueTask WriteUnavailableAsync(
            INntpConnection connection,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(connection);
            await using var writer = new NntpResponseWriter(connection.Output);
            await writer
                .WriteLineAsync(NntpResponses.ServiceTemporarilyUnavailable, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
