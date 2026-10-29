using System.Globalization;

namespace VectorNNTPD.TransitService.PostFilter.Quota
{
    /// <summary>
    /// One in-flight POST reservation: <c>{nodeId}:{incarnation}:{generation}</c>.
    /// </summary>
    /// <remarks>
    /// Not session occupancy. <c>nodeId</c> and <c>incarnation</c> follow
    /// <c>DistributedSessionStateTracker</c> ownership. <c>generation</c> is unique
    /// per reservation so one process can hold many concurrent POSTs.
    /// </remarks>
    internal readonly record struct PostFilterReservationId
    {
        /// <summary>Initializes a reservation identity.</summary>
        public PostFilterReservationId(string nodeId, string incarnation, long generation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
            ArgumentException.ThrowIfNullOrWhiteSpace(incarnation);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(generation, 0);
            NodeId = nodeId;
            Incarnation = incarnation;
            Generation = generation;
        }

        /// <summary>Gets the NNTPD node identity.</summary>
        public string NodeId { get; }

        /// <summary>Gets the process-lifetime incarnation.</summary>
        public string Incarnation { get; }

        /// <summary>Gets the per-reservation generation.</summary>
        public long Generation { get; }

        /// <summary>Gets <c>{nodeId}:{incarnation}:{generation}</c>.</summary>
        public string Token => string.Concat(NodeId, ":", Incarnation, ":", Generation.ToString(CultureInfo.InvariantCulture));

        /// <summary>Gets the quota-hash field <c>r:{token}</c>.</summary>
        public string Field => string.Concat(PostFilterQuotaKeys.ReservationFieldPrefix, Token);
    }
}
