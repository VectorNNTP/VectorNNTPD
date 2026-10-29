using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Transit;

namespace VectorNNTPD.ReaderService.Diagnostics
{
    /// <summary>No-op feed diagnostics used when the reporter is disabled.</summary>
    public sealed class NullFeedDiagnostics : IFeedDiagnostics
    {
        /// <summary>Shared disabled instance.</summary>
        public static NullFeedDiagnostics Instance { get; } = new();

        private NullFeedDiagnostics()
        {
        }

        /// <inheritdoc />
        public bool IsEnabled => false;

        /// <inheritdoc />
        public void OnRejected(string peerName, string remote)
        {
        }

        /// <inheritdoc />
        public FeedSessionProbe? OnAccepted(NntpSession session) => null;

        /// <inheritdoc />
        public void OnReleased(FeedSessionProbe? probe)
        {
        }

        /// <inheritdoc />
        public void RecordTcpBytes(int bytes)
        {
        }

        /// <inheritdoc />
        public void BeginSpoolWork()
        {
        }

        /// <inheritdoc />
        public void EndSpoolWork(int bytes, bool persisted)
        {
        }

        /// <inheritdoc />
        public FeedDiagnosticsSnapshot CaptureSnapshot(
            IArticleIngestionQueue queue,
            TransitConfigurationStore store) =>
            FeedDiagnosticsSnapshot.Empty;
    }
}
