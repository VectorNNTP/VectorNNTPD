using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Transit;

namespace VectorNNTPD.ReaderService.Session.SpeedTest
{
    /// <summary>
    /// Read-only Transit lookup and concurrency gate for <c>SPEEDTEST</c>.
    /// </summary>
    /// <remarks>
    /// Resolves Transit identifiers against the current immutable snapshot. Does not open
    /// outbound sockets and does not retain a mutable options object across a reload.
    /// </remarks>
    public interface ISpeedTestCoordinator
    {
        /// <summary>
        /// Resolves <paramref name="identifier"/> against the current Transit snapshot
        /// using exact ordinal identifier match (the configuration dictionary key).
        /// </summary>
        /// <returns>
        /// <see langword="true"/> when a configured Transit peer exists with that exact identifier.
        /// </returns>
        bool TryResolvePeer(ReadOnlySpan<byte> identifier, out TransitPeerPolicy? peer);

        /// <summary>
        /// Attempts to acquire a SPEEDTEST slot for <paramref name="identifier"/> using
        /// the current configured limits.
        /// </summary>
        bool TryAcquire(string identifier, out SpeedTestLease? lease);
    }

    /// <summary>
    /// Held SPEEDTEST concurrency slot. Dispose releases host and per-peer counters.
    /// </summary>
    public sealed class SpeedTestLease : IDisposable
    {
        private readonly Action<string> _release;
        private readonly string _peerName;
        private int _disposed;

        internal SpeedTestLease(string peerName, SpeedTestLimits limits, Action<string> release)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(peerName);
            ArgumentNullException.ThrowIfNull(release);
            _peerName = peerName;
            Limits = limits;
            _release = release;
        }

        /// <summary>Gets the limits captured when this slot was acquired.</summary>
        public SpeedTestLimits Limits { get; }

        /// <summary>Gets the Transit identifier this slot is counted against.</summary>
        public string PeerName => _peerName;

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _release(_peerName);
        }
    }
}
