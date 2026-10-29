namespace VectorNNTPD.ReaderService.Telemetry
{
    /// <summary>Source-generated standard application telemetry messages.</summary>
    internal static partial class ApplicationTelemetryLogMessages
    {
        [LoggerMessage(
            EventId = 2400,
            Level = LogLevel.Information,
            Message = "HistoryDb lookups={Lookups} hits={Hits} misses={Misses} errors={Errors} wait_ms={WaitMs}")]
        public static partial void HistoryDb(
            ILogger logger,
            long Lookups,
            long Hits,
            long Misses,
            long Errors,
            long WaitMs);

        [LoggerMessage(
            EventId = 2401,
            Level = LogLevel.Information,
            Message = "TransitIngressQueue articles={Articles} bytes={Bytes} peak_articles={PeakArticles} peak_bytes={PeakBytes} waiting={Waiting} limit={Limit} admit_fail={AdmissionFailures} admission_wait_ms={AdmissionWaitMs}")]
        public static partial void TransitIngressQueue(
            ILogger logger,
            int Articles,
            long Bytes,
            int PeakArticles,
            long PeakBytes,
            int Waiting,
            long Limit,
            long AdmissionFailures,
            long AdmissionWaitMs);

        [LoggerMessage(
            EventId = 2402,
            Level = LogLevel.Information,
            Message = "ActiveSessions active={Active} established={Established} idle={Idle} receiving={Receiving} waiting_history={WaitingHistory} waiting_queue={WaitingQueue} waiting_window={WaitingWindow} completing={Completing}")]
        public static partial void ActiveSessions(
            ILogger logger,
            int Active,
            int Established,
            int Idle,
            int Receiving,
            int WaitingHistory,
            int WaitingQueue,
            int WaitingWindow,
            int Completing);

        [LoggerMessage(
            EventId = 2403,
            Level = LogLevel.Information,
            Message = "{PeerName} active={Active}/{MaxIncomingConnections} peak={Peak} accepted={Accepted} transmitted={Transmitted} rejected={Rejected} articles={Articles} bytes={Bytes} avg_article={AverageArticleBytes} avg_mbps={AverageMbps:F2} checks={Checks}")]
        public static partial void TransitPeer(
            ILogger logger,
            string PeerName,
            int Active,
            int MaxIncomingConnections,
            int Peak,
            long Accepted,
            long Transmitted,
            long Rejected,
            long Articles,
            long Bytes,
            long AverageArticleBytes,
            double AverageMbps,
            long Checks);

        [LoggerMessage(
            EventId = 2404,
            Level = LogLevel.Information,
            Message = "IngestionWorker items={Items} arts_s={ArticlesPerSec:F2} busy_pct={BusyPct:F1} idle_ms={IdleMs} busy_ms={BusyMs} dq_p95_ms={DequeueP95Ms} to_pub_p95_ms={ToPublishP95Ms} encode_p95_ms={EncodeP95Ms} news_p95_ms={NewsP95Ms} item_avg_ms={ItemAvgMs} item_p95_ms={ItemP95Ms}")]
        public static partial void IngestionWorker(
            ILogger logger,
            long Items,
            double ArticlesPerSec,
            double BusyPct,
            long IdleMs,
            long BusyMs,
            long DequeueP95Ms,
            long ToPublishP95Ms,
            long EncodeP95Ms,
            long NewsP95Ms,
            double ItemAvgMs,
            long ItemP95Ms);

        [LoggerMessage(
            EventId = 2405,
            Level = LogLevel.Information,
            Message = "OverviewDbHandoff gate_p95_ms={GateP95Ms} pub_p95_ms={PublishP95Ms} confirm_avg_ms={ConfirmAvgMs} confirm_p95_ms={ConfirmP95Ms} handoff_avg_ms={HandoffAvgMs} handoff_p95_ms={HandoffP95Ms} confirm_busy_pct={ConfirmBusyPct:F1} fail={ConfirmFailures} timeout={ConfirmTimeouts} in_flight={InFlightPublishes} in_flight_max={MaxInFlightPublishes}")]
        public static partial void OverviewDbHandoff(
            ILogger logger,
            long GateP95Ms,
            long PublishP95Ms,
            double ConfirmAvgMs,
            long ConfirmP95Ms,
            double HandoffAvgMs,
            long HandoffP95Ms,
            double ConfirmBusyPct,
            long ConfirmFailures,
            long ConfirmTimeouts,
            int InFlightPublishes,
            int MaxInFlightPublishes);

        [LoggerMessage(
            EventId = 2406,
            Level = LogLevel.Information,
            Message = "TakeThisPipeline occupied={Occupied} occupied_max={OccupiedMax} occupied_full_sessions={OccupiedFullSessions} occupied_full_waits={OccupiedFullWaits} processing={Processing} processing_max={ProcessingMax} emit_gate_p95_ms={EmitGateP95Ms} enqueue_avg_ms={EnqueueAvgMs} enqueue_p95_ms={EnqueueP95Ms} budget_wait_p95_ms={BudgetWaitP95Ms} budget_waits={BudgetWaits} budget_exhausted={BudgetExhausted}")]
        public static partial void TakeThisPipeline(
            ILogger logger,
            int Occupied,
            int OccupiedMax,
            int OccupiedFullSessions,
            long OccupiedFullWaits,
            int Processing,
            int ProcessingMax,
            long EmitGateP95Ms,
            double EnqueueAvgMs,
            long EnqueueP95Ms,
            long BudgetWaitP95Ms,
            long BudgetWaits,
            int BudgetExhausted);

        [LoggerMessage(
            EventId = 2407,
            Level = LogLevel.Information,
            Message = "IngestionWorkerPool workers={Workers} min={MinWorkers} max={MaxWorkers} scale_up={ScaleUps} scale_down={ScaleDowns} queue_util={QueueUtilisation:F3} waiting={WaitingProducers}")]
        public static partial void IngestionWorkerPool(
            ILogger logger,
            int Workers,
            int MinWorkers,
            int MaxWorkers,
            long ScaleUps,
            long ScaleDowns,
            double QueueUtilisation,
            int WaitingProducers);

        [LoggerMessage(
            EventId = 2408,
            Level = LogLevel.Information,
            Message = "OverviewDbWorkQueue articles={Articles} bytes={Bytes} waiting={Waiting} publishers={Publishers} pub_min={MinPublishers} pub_max={MaxPublishers} scale_up={ScaleUps} scale_down={ScaleDowns}")]
        public static partial void OverviewDbWorkQueue(
            ILogger logger,
            int Articles,
            long Bytes,
            int Waiting,
            int Publishers,
            int MinPublishers,
            int MaxPublishers,
            long ScaleUps,
            long ScaleDowns);
    }
}
