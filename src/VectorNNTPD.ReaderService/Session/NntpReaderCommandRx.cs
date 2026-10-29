using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Session
{
    /// <summary>
    /// MODE READER RX: one command line at a time via <see cref="NntpCommandLineReader"/>.
    /// </summary>
    /// <remarks>
    /// Does not use the continuous TAKETHIS scanner. TAKETHIS on this path is dispatched as a
    /// normal command; the handler reads the article after authorization gates.
    /// </remarks>
    internal sealed class NntpReaderCommandRx
    {
        private readonly NntpSession _session;
        private readonly NntpCommandDispatcher _dispatcher;
        private readonly ILogger _logger;
        private readonly byte[] _commandScratch = new byte[NntpCommandLineReader.MaxCommandLineBytes];

        /// <summary>Initializes a new instance of the <see cref="NntpReaderCommandRx"/> class.</summary>
        public NntpReaderCommandRx(NntpSession session, NntpCommandDispatcher dispatcher, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(logger);
            _session = session;
            _dispatcher = dispatcher;
            _logger = logger;
        }

        /// <summary>
        /// Reads and dispatches one command. Returns <see langword="false"/> on EOF.
        /// </summary>
        public async ValueTask<bool> ProcessOneAsync(NntpResponseWriter response, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(response);
            await _session.WaitForCheckCapacityAsync(cancellationToken).ConfigureAwait(false);
            var length = await NntpCommandLineReader
                .ReadLineBytesAsync(_session.Connection.Input, _commandScratch, cancellationToken)
                .ConfigureAwait(false);
            if (length == NntpCommandLineReader.OverlongLine)
            {
                await _session
                    .ProcessParsedCommandAsync(
                        _dispatcher,
                        response,
                        _logger,
                        NntpCommand.OverlongLine,
                        ReadOnlyMemory<byte>.Empty,
                        preReadArticle: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            if (length < 0)
            {
                return false;
            }

            var line = _commandScratch.AsMemory(0, length);
            if (_session.HasSaslExchange)
            {
                _session.BeginCommandWork();
                try
                {
                    if (_session.TakeThisWindow is not null)
                    {
                        await _session.TakeThisWindow.DrainAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (_session.Pipeline is not null)
                    {
                        await _session.Pipeline.DrainAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await Commands.AuthInfo
                        .ContinueSaslAsync(_session, response, line, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _session.EndCommandWork();
                }

                return true;
            }

            var command = NntpCommandParser.Parse(line.Span);
            await _session
                .ProcessParsedCommandAsync(
                    _dispatcher,
                    response,
                    _logger,
                    command,
                    line,
                    preReadArticle: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
    }
}
