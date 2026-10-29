using System.Collections.Concurrent;
using VectorNNTPD.TransitService.Diagnostics;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Session
{
    /// <summary>
    /// Process-wide established-session registry. Independent of FeedDiagnostics.
    /// </summary>
    public sealed class NntpSessionCensus : INntpSessionCensus
    {
        private readonly ConcurrentDictionary<NntpSession, string?> _sessions = new();
        private readonly ITransitPeerMetrics? _peerMetrics;

        /// <summary>Initializes a census that optionally updates <paramref name="peerMetrics"/> on register/unregister.</summary>
        public NntpSessionCensus(ITransitPeerMetrics? peerMetrics = null)
        {
            _peerMetrics = peerMetrics;
        }

        /// <inheritdoc />
        public void Register(NntpSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            var peerId = session.Authorization.TransitPeerName;
            _sessions[session] = peerId;
            if (peerId is not null)
            {
                _peerMetrics?.RecordSessionRegistered(peerId);
            }
        }

        /// <inheritdoc />
        public void Unregister(NntpSession session)
        {
            if (session is null)
            {
                return;
            }

            if (_sessions.TryRemove(session, out var peerId) && peerId is not null)
            {
                _peerMetrics?.RecordSessionUnregistered(peerId);
            }
        }

        /// <inheritdoc />
        public void ReleasePeerAttribution(NntpSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            if (!_sessions.TryGetValue(session, out var peerId) || peerId is null)
            {
                return;
            }

            if (_sessions.TryUpdate(session, null, peerId))
            {
                _peerMetrics?.RecordSessionUnregistered(peerId);
            }
        }

        /// <inheritdoc />
        public NntpSessionCensusSnapshot Capture()
        {
            var idle = 0;
            var receiving = 0;
            var waitingHistory = 0;
            var waitingQueue = 0;
            var waitingWindow = 0;
            var completing = 0;
            var active = 0;
            var takeThisOccupied = 0;
            var takeThisOccupiedMax = 0;
            var takeThisFullSessions = 0;
            var takeThisProcessing = 0;
            var takeThisProcessingMax = 0;
            foreach (var session in _sessions.Keys)
            {
                active++;
                switch (session.ActivityState)
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

                var window = session.TakeThisWindow;
                if (window is null)
                {
                    continue;
                }

                var occupied = window.Occupied;
                takeThisOccupied += occupied;
                if (occupied > takeThisOccupiedMax)
                {
                    takeThisOccupiedMax = occupied;
                }

                if (occupied >= TakeThisPipeline.Depth)
                {
                    takeThisFullSessions++;
                }

                var processing = window.ActiveArticleProcessing;
                takeThisProcessing += processing;
                var processingPeak = window.MaxActiveArticleProcessing;
                if (processingPeak > takeThisProcessingMax)
                {
                    takeThisProcessingMax = processingPeak;
                }
            }

            return new NntpSessionCensusSnapshot(
                active,
                active,
                idle,
                receiving,
                waitingHistory,
                waitingQueue,
                waitingWindow,
                completing,
                takeThisOccupied,
                takeThisOccupiedMax,
                takeThisFullSessions,
                takeThisProcessing,
                takeThisProcessingMax);
        }
    }
}
