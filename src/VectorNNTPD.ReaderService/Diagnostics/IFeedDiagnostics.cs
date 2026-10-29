using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Transit;

namespace VectorNNTPD.ReaderService.Diagnostics
{
    /// <summary>
    /// Opt-in real-feed observability. Disabled implementations are no-ops.
    /// </summary>
    public interface IFeedDiagnostics
    {
        /// <summary>Gets whether probes should be attached and snapshots emitted.</summary>
        bool IsEnabled { get; }

        /// <summary>Records a Transit admission rejection (no session runs).</summary>
        void OnRejected(string peerName, string remote);

        /// <summary>Attaches and registers a probe after a session is admitted.</summary>
        FeedSessionProbe? OnAccepted(NntpSession session);

        /// <summary>Unregisters a probe when the session ends.</summary>
        void OnReleased(FeedSessionProbe? probe);

        /// <summary>
        /// Adds application-layer bytes read from an upstream connection (plain TCP payload,
        /// or TLS-decrypted octets). No-op when disabled.
        /// </summary>
        void RecordTcpBytes(int bytes);

        /// <summary>Marks the single spool drain loop as busy around one persist.</summary>
        void BeginSpoolWork();

        /// <summary>Records one successfully persisted spool article and clears the busy mark.</summary>
        void EndSpoolWork(int bytes, bool persisted);

        /// <summary>Builds a point-in-time snapshot for the reporter.</summary>
        FeedDiagnosticsSnapshot CaptureSnapshot(
            IArticleIngestionQueue queue,
            TransitConfigurationStore store);
    }
}
