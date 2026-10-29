using VectorNNTPD.ReaderService.PostFilter.Quota;

namespace VectorNNTPD.ReaderService.PostFilter
{
    /// <summary>Process-lifetime reservation identity: node + incarnation + monotonic generation.</summary>
    internal sealed class PostFilterReservationIdentity
    {
        private readonly string _nodeId;
        private readonly string _incarnation;
        private long _generation;

        /// <summary>Initializes identity for this process.</summary>
        public PostFilterReservationIdentity(string nodeId, string? incarnation = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
            _nodeId = nodeId;
            _incarnation = string.IsNullOrWhiteSpace(incarnation)
                ? Guid.NewGuid().ToString("N")
                : incarnation;
        }

        /// <summary>Gets the node identity.</summary>
        public string NodeId => _nodeId;

        /// <summary>Gets the process incarnation.</summary>
        public string Incarnation => _incarnation;

        /// <summary>Allocates the next per-POST generation.</summary>
        public PostFilterReservationId Next() =>
            new(_nodeId, _incarnation, Interlocked.Increment(ref _generation));
    }
}
