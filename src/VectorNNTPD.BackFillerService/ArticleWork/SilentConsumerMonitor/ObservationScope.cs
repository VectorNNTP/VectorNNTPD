namespace VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor
{
    /// <summary>Whether an observation describes this process or a shared queue aggregate.</summary>
    internal enum ObservationScope
    {
        /// <summary>Derived from this instance's consumer sessions / stall clock.</summary>
        Instance = 1,

        /// <summary>Derived from Management queue gauges/rates (multi-consumer aggregate).</summary>
        SharedQueue = 2,
    }

    /// <summary>
    /// Operational silent-consumer condition.
    /// Investigation labels D / D2 / E map to ProcessingStall / DispatchStall / ConsumerLoss.
    /// </summary>
    internal enum SilentConsumerCondition
    {
        /// <summary>In-flight active processing stall (investigation Scenario D).</summary>
        ProcessingStall = 1,

        /// <summary>Dispatch / queued-before-active stall (investigation Scenario D2).</summary>
        DispatchStall = 2,

        /// <summary>Consumer loss with backlog (investigation Scenario E).</summary>
        ConsumerLoss = 3,
    }

    /// <summary>Alert / observation lifecycle phase.</summary>
    internal enum ObservationPhase
    {
        /// <summary>Condition clear.</summary>
        Clear = 0,

        /// <summary>Condition open.</summary>
        Open = 1,
    }

    /// <summary>Condition signal for one evaluation tick.</summary>
    internal enum ConditionSignal
    {
        /// <summary>Cannot evaluate; must not open or resolve.</summary>
        Unknown = 0,

        /// <summary>Condition present.</summary>
        Active = 1,

        /// <summary>Condition absent.</summary>
        Inactive = 2,
    }

    /// <summary>State-machine transition.</summary>
    internal enum ObservationTransition
    {
        /// <summary>No change.</summary>
        None = 0,

        /// <summary>Newly opened.</summary>
        Opened = 1,

        /// <summary>Still open.</summary>
        Continued = 2,

        /// <summary>Resolved.</summary>
        Resolved = 3,
    }

    /// <summary>Stable observation identity within one process.</summary>
    /// <param name="Scope">Instance vs shared queue.</param>
    /// <param name="Condition">ProcessingStall, DispatchStall, or ConsumerLoss.</param>
    /// <param name="QueueOrBackbone">
    /// Shared: queue name. Instance ProcessingStall/DispatchStall: canonical session key
    /// (<c>backbone/connectionNumber</c>). Instance ConsumerLoss: the process-scoped token <c>local</c>.
    /// </param>
    internal readonly record struct ObservationKey(
        ObservationScope Scope,
        SilentConsumerCondition Condition,
        string QueueOrBackbone);
}
