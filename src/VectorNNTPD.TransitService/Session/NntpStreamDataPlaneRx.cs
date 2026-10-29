using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Session.Framing;
using VectorNNTPD.TransitService.Diagnostics;

namespace VectorNNTPD.TransitService.Session
{
    /// <summary>
    /// Default / transit STREAM RX: continuous command/article units from the connection input pipe.
    /// </summary>
    /// <remarks>
    /// Authorized TAKETHIS is admitted into <see cref="TakeThisPipeline"/>: HistoryDB peek starts
    /// at the command line, then this RX task frames one stuffed-wire copy with
    /// <see cref="IHaveArticleReader"/>, attaches the owned buffer to a slot, and returns so
    /// the next command can be parsed. Pipeline workers never read <c>Connection.Input</c>.
    /// Unauthorized TAKETHIS is returned as a command line so 480/502 gates do not consume a
    /// following QUIT.
    /// </remarks>
    internal sealed class NntpStreamDataPlaneRx
    {
        private readonly NntpSession _session;
        private readonly NntpCommandDispatcher _dispatcher;
        private readonly ILogger _logger;
        private readonly NntpContinuousRxParser _parser = new();

        /// <summary>Initializes a new instance of the <see cref="NntpStreamDataPlaneRx"/> class.</summary>
        public NntpStreamDataPlaneRx(NntpSession session, NntpCommandDispatcher dispatcher, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(logger);
            _session = session;
            _dispatcher = dispatcher;
            _logger = logger;
        }

        /// <summary>
        /// Reads and dispatches one protocol unit. Returns <see langword="false"/> on EOF or an
        /// incomplete TAKETHIS article.
        /// </summary>
        public async ValueTask<bool> ProcessOneAsync(NntpResponseWriter response, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(response);
            if (_session.Pipeline?.IsFull == true || _session.TakeThisWindow?.IsFull == true)
            {
                _session.SetActivityState(FeedSessionState.WaitingWindow);
            }

            await _session.WaitForCheckCapacityAsync(cancellationToken).ConfigureAwait(false);
            await _session.WaitForTakeThisCapacityAsync(cancellationToken).ConfigureAwait(false);
            _session.SetActivityState(FeedSessionState.Idle);

            // Command line only. Authorized TAKETHIS starts HistoryDB peek, frames the
            // article on this RX task, hands the owned buffer to the pipeline, and returns.
            // Unauthorized TAKETHIS stays a command so 480/502 gates do not consume a
            // following QUIT.
            var unit = await NntpContinuousRxReader
                .ReadUnitAsync(
                    _session.Connection.Input,
                    _parser,
                    consumeTakeThisArticle: false,
                    _session.ArticleIngestion.MaxArticleBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            if (unit.Kind == NntpContinuousRxKind.NeedMore)
            {
                return false;
            }

            if (_session.HasSaslExchange)
            {
                _session.BeginCommandWork();
                try
                {
                    await _session.TakeThisWindow!.DrainAsync(cancellationToken).ConfigureAwait(false);
                    await _session.Pipeline!.DrainAsync(cancellationToken).ConfigureAwait(false);
                    await Commands.AuthInfo
                        .ContinueSaslAsync(_session, response, _parser.CurrentCommandLine, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _session.EndCommandWork();
                }

                return true;
            }

            // Transit TAKETHIS admit: only on a Transit host with transit authority.
            // Reader hosts leave TAKETHIS as a command line so the dispatcher returns 502.
            if (unit.Command.IsValid
                && unit.Command.Verb == NntpVerb.TakeThis
                && _session.ServerRole == Hosting.NntpServerRole.Transit
                && _session.Authorization.AuthorizedTransit)
            {
                // Capture once: nullable session properties must not be re-read between check and use
                // (concurrent teardown can clear them after a prior null check).
                var takeThisWindow = _session.TakeThisWindow
                    ?? throw new InvalidOperationException(
                        "Transit TAKETHIS admit requires an initialized TakeThis admission window.");
                var pipeline = _session.Pipeline
                    ?? throw new InvalidOperationException(
                        "Transit TAKETHIS admit requires an initialized CHECK pipeline.");
                await pipeline.DrainAsync(cancellationToken).ConfigureAwait(false);
                await takeThisWindow
                    .AdmitAuthorizedAsync(unit.Command, _parser.CurrentCommandLine, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            var preRead = unit.Kind == NntpContinuousRxKind.TakeThis ? unit.Article : (NntpMultilineReadResult?)null;
            await _session
                .ProcessParsedCommandAsync(
                    _dispatcher,
                    response,
                    _logger,
                    unit.Command,
                    _parser.CurrentCommandLine,
                    preRead,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
    }
}
