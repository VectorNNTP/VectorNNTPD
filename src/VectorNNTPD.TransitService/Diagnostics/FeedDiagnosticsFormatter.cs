using System.Globalization;
using System.Text;

namespace VectorNNTPD.TransitService.Diagnostics
{
    /// <summary>Formats a feed-diagnostics snapshot for a single Information log.</summary>
    /// <remarks>
    /// Output contains peer names, remote IP/port, counters, and wait totals.
    /// It does not include Message-IDs, credentials, or article payloads.
    /// </remarks>
    public static class FeedDiagnosticsFormatter
    {
        /// <summary>Builds the periodic snapshot text.</summary>
        public static string Format(FeedDiagnosticsSnapshot snapshot, bool includeSessions)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            var sb = new StringBuilder(1024);
            sb.Append(CultureInfo.InvariantCulture, $"FEED interval={FormatSeconds(snapshot.Interval)}");
            sb.Append(CultureInfo.InvariantCulture, $" articles={snapshot.IntervalArticles}");
            sb.Append(CultureInfo.InvariantCulture, $" rate={snapshot.ArticleMegabitsPerSecond:F1} Mbps");
            sb.Append(CultureInfo.InvariantCulture, $" articles/sec={snapshot.ArticlesPerSecond:F1}");
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"  stage rx_app bytes={snapshot.IntervalTcpBytes}");
            sb.Append(CultureInfo.InvariantCulture, $" mbps={snapshot.TcpMegabitsPerSecond:F1}");
            sb.Append(CultureInfo.InvariantCulture, $" articles_rx={snapshot.IntervalArticlesReceived}");
            sb.Append(CultureInfo.InvariantCulture, $" articles/sec={snapshot.ArticlesReceivedPerSecond:F1}");
            sb.Append(CultureInfo.InvariantCulture, $" commands={snapshot.IntervalCommands}");
            sb.Append(CultureInfo.InvariantCulture, $" commands/sec={snapshot.CommandsPerSecond:F1}");
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"  stage ingest admitted={snapshot.IntervalQueuedArticles}");
            sb.Append(CultureInfo.InvariantCulture, $" bytes={snapshot.IntervalQueuedBytes}");
            sb.Append(CultureInfo.InvariantCulture, $" mbps={snapshot.IngestMegabitsPerSecond:F1}");
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"  stage process completed={snapshot.IntervalArticles}");
            sb.Append(CultureInfo.InvariantCulture, $" mbps={snapshot.ProcessMegabitsPerSecond:F1}");
            sb.AppendLine();
            sb.Append("  stage routing status=not_implemented");
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"  stage spool written={snapshot.IntervalSpoolArticles}");
            sb.Append(CultureInfo.InvariantCulture, $" bytes={snapshot.IntervalSpoolBytes}");
            sb.Append(CultureInfo.InvariantCulture, $" mbps={snapshot.SpoolMegabitsPerSecond:F1}");
            sb.Append(CultureInfo.InvariantCulture, $" workers={snapshot.SpoolActiveWorkers}");
            sb.AppendLine();

            var queueWaitMs = 0d;
            var historyWaitMs = 0d;
            foreach (var session in snapshot.Sessions)
            {
                queueWaitMs += session.QueueWaitTime.TotalMilliseconds;
                historyWaitMs += session.HistoryWaitTime.TotalMilliseconds;
            }

            sb.Append(CultureInfo.InvariantCulture, $"  queue articles={snapshot.QueueCount} bytes={snapshot.QueueBytes}");
            sb.Append(CultureInfo.InvariantCulture, $" peak_articles={snapshot.QueuePeakCount} peak_bytes={snapshot.QueuePeakBytes}");
            sb.Append(CultureInfo.InvariantCulture, $" waiting={snapshot.QueueWaitingProducers}");
            sb.Append(CultureInfo.InvariantCulture, $" limit={snapshot.QueueMemoryLimit}");
            sb.Append(CultureInfo.InvariantCulture, $" admit_fail={snapshot.QueueAdmissionFailures}");
            sb.Append(CultureInfo.InvariantCulture, $" admission_wait_ms={queueWaitMs:F0}");
            sb.AppendLine();

            var lookups = snapshot.HistoryHits + snapshot.HistoryMisses + snapshot.HistoryErrors;
            sb.Append(CultureInfo.InvariantCulture, $"  history lookups={lookups} hits={snapshot.HistoryHits} misses={snapshot.HistoryMisses} errors={snapshot.HistoryErrors}");
            sb.Append(CultureInfo.InvariantCulture, $" wait_ms={historyWaitMs:F0}");
            sb.AppendLine();

            var idle = 0;
            var receiving = 0;
            var waitingHistory = 0;
            var waitingQueue = 0;
            var waitingWindow = 0;
            var completing = 0;
            foreach (var session in snapshot.Sessions)
            {
                switch (session.State)
                {
                    case FeedSessionState.Receiving:
                        receiving++;
                        break;
                    case FeedSessionState.WaitingHistory:
                        waitingHistory++;
                        break;
                    case FeedSessionState.WaitingQueue:
                        waitingQueue++;
                        break;
                    case FeedSessionState.WaitingWindow:
                        waitingWindow++;
                        break;
                    case FeedSessionState.Completing:
                        completing++;
                        break;
                    default:
                        idle++;
                        break;
                }
            }

            sb.Append(CultureInfo.InvariantCulture, $"  sessions active={snapshot.ActiveSessions}");
            sb.Append(CultureInfo.InvariantCulture, $" established={snapshot.ActiveSessions}");
            sb.Append(CultureInfo.InvariantCulture, $" idle={idle} receiving={receiving}");
            sb.Append(CultureInfo.InvariantCulture, $" waiting_history={waitingHistory} waiting_queue={waitingQueue}");
            sb.Append(CultureInfo.InvariantCulture, $" waiting_window={waitingWindow} completing={completing}");
            sb.AppendLine();

            foreach (var peer in snapshot.Peers)
            {
                var avg = peer.ArticlesReceived > 0 ? peer.ArticleBytes / (double)peer.ArticlesReceived : 0;
                sb.Append(CultureInfo.InvariantCulture, $"  {peer.PeerName}");
                sb.Append(CultureInfo.InvariantCulture, $" active={peer.ActiveConnections}/{peer.ConfiguredMaxConnections}");
                sb.Append(CultureInfo.InvariantCulture, $" peak={peer.PeakConnections}");
                sb.Append(CultureInfo.InvariantCulture, $" accepted={peer.AcceptedConnections} rejected={peer.RejectedConnections}");
                sb.Append(CultureInfo.InvariantCulture, $" articles={peer.ArticlesCompleted} bytes={peer.ArticleBytes}");
                sb.Append(CultureInfo.InvariantCulture, $" avg_article={avg:F0} checks={peer.Checks}");
                sb.AppendLine();
            }

            if (!includeSessions || snapshot.Sessions.Count == 0)
            {
                return sb.ToString();
            }

            var shown = 0;
            foreach (var session in snapshot.Sessions)
            {
                if (shown >= 50)
                {
                    sb.AppendLine("  ...");
                    break;
                }

                sb.Append(CultureInfo.InvariantCulture, $"    {session.Remote} peer={session.PeerName}");
                sb.Append(CultureInfo.InvariantCulture, $" age={FormatSeconds(session.Age)} state={session.State}");
                sb.Append(CultureInfo.InvariantCulture, $" articles={session.ArticlesCompleted} bytes={session.ArticleBytes}");
                sb.Append(CultureInfo.InvariantCulture, $" iv_bytes={session.IntervalBytes}");
                sb.Append(
                    CultureInfo.InvariantCulture,
                    $" iv_mbps={FeedRateCalculator.MegabitsPerSecond(session.IntervalBytes, snapshot.Interval):F1}");
                sb.Append(
                    CultureInfo.InvariantCulture,
                    $" iv_art={session.IntervalArticlesReceived}");
                sb.Append(
                    CultureInfo.InvariantCulture,
                    $" iv_art/s={FeedRateCalculator.PerSecond(session.IntervalArticlesReceived, snapshot.Interval):F1}");
                sb.Append(CultureInfo.InvariantCulture, $" last={FormatSeconds(session.SinceLastArticle)} idle={FormatSeconds(session.Idle)}");
                sb.Append(CultureInfo.InvariantCulture, $" rx={FormatSeconds(session.ReceiveTime)} hist={FormatSeconds(session.HistoryWaitTime)} q={FormatSeconds(session.QueueWaitTime)}");
                sb.AppendLine();
                shown++;
            }

            return sb.ToString();
        }

        private static string FormatSeconds(TimeSpan value) =>
            value.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s";
    }
}
