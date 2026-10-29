namespace VectorNNTPD.Common.Networking.Listeners
{
    /// <summary>
    /// Optional host telemetry for consecutive accept failures on <see cref="SocketAcceptListener"/>.
    /// </summary>
    public interface IListenAcceptFailureObserver
    {
        /// <summary>Reports one accept <see cref="System.Net.Sockets.SocketException"/>.</summary>
        /// <param name="socketError">Socket error code text.</param>
        /// <param name="consecutiveFailures">Failures since the last successful accept.</param>
        /// <param name="maxFailures">Configured stop threshold.</param>
        void OnAcceptFailed(string socketError, int consecutiveFailures, int maxFailures);

        /// <summary>Reports that accepts resumed after one or more failures.</summary>
        /// <param name="recoveredAfterFailures">Consecutive failures cleared by the successful accept.</param>
        void OnAcceptRecovered(int recoveredAfterFailures);

        /// <summary>Reports that the accept loop is stopping after reaching the failure threshold.</summary>
        /// <param name="consecutiveFailures">Failure count that triggered the stop.</param>
        /// <param name="socketError">Last socket error code text.</param>
        void OnAcceptLoopStopping(int consecutiveFailures, string socketError);
    }
}
