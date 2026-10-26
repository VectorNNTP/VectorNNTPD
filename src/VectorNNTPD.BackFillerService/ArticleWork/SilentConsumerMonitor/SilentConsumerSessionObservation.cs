namespace VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// Immutable projection of one consumer session for silent-consumer evaluation.
    /// Avoids coupling evaluators to session mutation and keeps unit tests broker-free.
    /// </summary>
    /// <param name="Backbone">Provider backbone label.</param>
    /// <param name="ConnectionNumber">One-based consume slot for this backbone (same as reconcile).</param>
    /// <param name="State">Local session lifecycle state.</param>
    /// <param name="QueuedCount">
    /// Instance-local <c>_inFlight - _active</c>: admitted work not yet inside the pipeline handler.
    /// Includes deliveries that hold the dispatch lock but have not incremented <c>_active</c>
    /// (for example a <c>DispatchAcquireHold</c>). Not the broker ready count.
    /// </param>
    /// <param name="ActiveCount">Deliveries that have entered the pipeline handler (<c>_active</c>).</param>
    /// <param name="HasOpenChannel">Whether the consume channel is present and open.</param>
    /// <param name="IsActiveProcessingStalled">
    /// Same predicate reconcile uses for Event 5328. Idle sessions (no active work) never stall.
    /// </param>
    internal readonly record struct SilentConsumerSessionObservation(
        string Backbone,
        int ConnectionNumber,
        ArticleWorkConsumerState State,
        int QueuedCount,
        int ActiveCount,
        bool HasOpenChannel,
        bool IsActiveProcessingStalled)
    {
        /// <summary>
        /// Canonical session identity <c>backbone/connectionNumber</c>
        /// (<see cref="ArticleWorkConsumerSession.ComposeSessionKey"/>).
        /// </summary>
        public string SessionKey =>
            ArticleWorkConsumerSession.ComposeSessionKey(Backbone, ConnectionNumber);

        /// <summary>Projects a live session using the same stall threshold as reconcile.</summary>
        /// <param name="session">Live consumer session.</param>
        /// <param name="stallThreshold">
        /// Active-processing stall threshold (same as <c>ArticleWorkConsumerService.ProcessingStallThreshold</c>).
        /// </param>
        /// <returns>Immutable observation for evaluators/tests.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="session"/> is null.</exception>
        public static SilentConsumerSessionObservation FromSession(
            ArticleWorkConsumerSession session,
            TimeSpan stallThreshold)
        {
            ArgumentNullException.ThrowIfNull(session);
            return new SilentConsumerSessionObservation(
                session.Backbone,
                session.ConnectionNumber,
                session.State,
                session.QueuedCount,
                session.ActiveCount,
                session.Channel is { IsOpen: true },
                session.IsActiveProcessingStalled(stallThreshold));
        }
    }
}
