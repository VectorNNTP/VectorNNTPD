using System.Buffers;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Session.Framing
{
    /// <summary>Scan mode for <see cref="NntpContinuousRxParser"/>.</summary>
    public enum NntpContinuousRxMode
    {
        /// <summary>Waiting for a command line terminated by CRLF.</summary>
        Command = 0,

        /// <summary>Inside a TAKETHIS multiline article; only the terminator ends the unit.</summary>
        Article = 1,
    }

    /// <summary>Kind of protocol unit produced by the continuous RX parser.</summary>
    public enum NntpContinuousRxKind
    {
        /// <summary>Need more Pipe bytes (incomplete command or article).</summary>
        NeedMore = 0,

        /// <summary>A complete non-article command line (without CRLF).</summary>
        Command = 1,

        /// <summary>A complete TAKETHIS command plus framed article bytes (no destuff).</summary>
        TakeThis = 2,
    }

    /// <summary>One consumed protocol unit. Pipe memory is not retained.</summary>
    public readonly struct NntpContinuousRxUnit
    {
        /// <summary>Initializes a new instance of the <see cref="NntpContinuousRxUnit"/> struct.</summary>
        public NntpContinuousRxUnit(
            NntpContinuousRxKind kind,
            NntpCommand command = default,
            NntpMultilineReadResult article = default)
        {
            Kind = kind;
            Command = command;
            Article = article;
        }

        /// <summary>Gets the unit kind.</summary>
        public NntpContinuousRxKind Kind { get; }

        /// <summary>Gets the parsed command. Indexes refer to <see cref="NntpContinuousRxParser.CurrentCommandLine"/>.</summary>
        public NntpCommand Command { get; }

        /// <summary>
        /// Gets the article result for <see cref="NntpContinuousRxKind.TakeThis"/>.
        /// STREAM framing copies wire bytes without destuffing; the terminator is omitted.
        /// </summary>
        public NntpMultilineReadResult Article { get; }

        /// <summary>Need-more sentinel.</summary>
        public static NntpContinuousRxUnit NeedMore { get; } = new(NntpContinuousRxKind.NeedMore);
    }

    /// <summary>
    /// Stateful command/article scanner over <see cref="ReadOnlySequence{T}"/>.
    /// </summary>
    /// <remarks>
    /// Command CRLF ends a command. Inside an article, the terminator is the five-octet
    /// <c>\r\n.\r\n</c> (or a leading <c>.\r\n</c> for an empty article). CHECK/QUIT/TAKETHIS
    /// text in the body is payload. Incomplete tails of at most four octets stay in the Pipe.
    /// STREAM article bytes are copied as received (no destuff) into an owned buffer before the
    /// caller advances the Pipe. The terminator is framing, not payload. MODE READER destuff
    /// remains line-oriented in <see cref="TryConsumeArticleOnly"/>.
    /// </remarks>
    public sealed class NntpContinuousRxParser
    {
        private readonly ArrayBufferWriter<byte> _article = new(64 * 1024);
        private readonly byte[] _commandScratch = new byte[NntpCommandLineReader.MaxCommandLineBytes];
        private int _commandLength;
        private bool _articleExceeded;
        private int _maxArticleBytes = int.MaxValue;
        private NntpCommand _pendingCommand;

        /// <summary>Gets the current scan mode.</summary>
        public NntpContinuousRxMode Mode { get; private set; }

        /// <summary>
        /// Gets the current command-line bytes. Valid until the next command line is consumed.
        /// </summary>
        public ReadOnlyMemory<byte> CurrentCommandLine => _commandScratch.AsMemory(0, _commandLength);

        /// <summary>Resets command/article state (does not release writer capacity).</summary>
        public void Reset()
        {
            Mode = NntpContinuousRxMode.Command;
            _pendingCommand = default;
            // Keep _commandLength/_commandScratch until the next command is copied so
            // CurrentCommandLine remains valid for dispatch after a TAKETHIS article.
            _articleExceeded = false;
            _maxArticleBytes = int.MaxValue;
            _article.ResetWrittenCount();
        }

        /// <summary>
        /// Attempts to consume one complete protocol unit from <paramref name="buffer"/>.
        /// Slices <paramref name="buffer"/> to the unconsumed remainder.
        /// </summary>
        /// <param name="buffer">Unread Pipe bytes. Sliced to the unconsumed tail.</param>
        /// <param name="consumeTakeThisArticle">
        /// When <see langword="true"/>, a TAKETHIS verb consumes the following article in this
        /// parser. When <see langword="false"/>, TAKETHIS is returned as a command line so session
        /// gates can reject it without consuming a body (existing 480/502 behaviour).
        /// </param>
        /// <param name="maxArticleBytes">Framed (non-destuffed) payload cap for TAKETHIS articles.</param>
        public NntpContinuousRxUnit TryConsume(
            ref ReadOnlySequence<byte> buffer,
            bool consumeTakeThisArticle,
            int maxArticleBytes)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxArticleBytes, 1);
            if (Mode == NntpContinuousRxMode.Article)
            {
                return TryConsumeArticle(ref buffer);
            }

            if (!NntpDelimiterSearch.TryReadLine(ref buffer, out var lineBytes))
            {
                return NntpContinuousRxUnit.NeedMore;
            }

            if (!TryCopyCommandLine(lineBytes))
            {
                return new NntpContinuousRxUnit(
                    NntpContinuousRxKind.Command,
                    NntpCommand.OverlongLine);
            }

            var command = NntpCommandParser.Parse(CurrentCommandLine.Span);

            if (consumeTakeThisArticle && command.Verb == NntpVerb.TakeThis)
            {
                _pendingCommand = command;
                Mode = NntpContinuousRxMode.Article;
                _maxArticleBytes = maxArticleBytes;
                _articleExceeded = false;
                _article.ResetWrittenCount();
                return TryConsumeArticle(ref buffer);
            }

            return new NntpContinuousRxUnit(NntpContinuousRxKind.Command, command);
        }

        private NntpContinuousRxUnit TryConsumeArticle(ref ReadOnlySequence<byte> buffer)
        {
            var atArticleStart = _article.WrittenCount == 0 && !_articleExceeded;
            if (NntpDelimiterSearch.TryFindArticleTerminator(
                    buffer,
                    atArticleStart,
                    out var payloadBytes,
                    out var consumedBytes))
            {
                if (payloadBytes > 0)
                {
                    AppendFramedWire(buffer.Slice(0, payloadBytes));
                }

                buffer = buffer.Slice(consumedBytes);
                var article = NntpArticleDestuffer.Complete(_article, _articleExceeded);
                var unit = new NntpContinuousRxUnit(
                    NntpContinuousRxKind.TakeThis,
                    _pendingCommand,
                    article);
                Reset();
                return unit;
            }

            // Incomplete terminator: copy the prefix that cannot be a delimiter lookbehind
            // so Pipe memory can be released. The last 0–4 octets stay unconsumed.
            var hold = (int)Math.Min(NntpDelimiterSearch.ArticleTerminatorLookbehind, buffer.Length);
            var copyBytes = (int)buffer.Length - hold;
            if (copyBytes > 0)
            {
                AppendFramedWire(buffer.Slice(0, copyBytes));
                buffer = buffer.Slice(copyBytes);
            }

            return NntpContinuousRxUnit.NeedMore;
        }

        /// <summary>
        /// Destuffs article lines from <paramref name="buffer"/> up to <paramref name="maxArticleBytes"/>.
        /// Used by <see cref="NntpMultilineDataReader"/> (MODE READER multiline semantics).
        /// </summary>
        public NntpContinuousRxUnit TryConsumeArticleOnly(
            ref ReadOnlySequence<byte> buffer,
            int maxArticleBytes)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxArticleBytes, 1);
            if (Mode != NntpContinuousRxMode.Article)
            {
                Mode = NntpContinuousRxMode.Article;
                _maxArticleBytes = maxArticleBytes;
                _articleExceeded = false;
                _article.ResetWrittenCount();
            }

            while (NntpDelimiterSearch.TryReadLine(ref buffer, out var lineBytes))
            {
                if (NntpDelimiterSearch.IsTerminatorLine(lineBytes))
                {
                    var article = NntpArticleDestuffer.Complete(_article, _articleExceeded);
                    Reset();
                    return new NntpContinuousRxUnit(NntpContinuousRxKind.TakeThis, article: article);
                }

                NntpArticleDestuffer.AppendUnstuffedLine(_article, lineBytes, maxArticleBytes, ref _articleExceeded);
            }

            return NntpContinuousRxUnit.NeedMore;
        }

        /// <summary>
        /// Copies framed STREAM wire octets (already including content CRLFs) without destuffing.
        /// </summary>
        private void AppendFramedWire(ReadOnlySequence<byte> wireBytes)
        {
            if (_articleExceeded || wireBytes.IsEmpty)
            {
                return;
            }

            var needed = checked((int)wireBytes.Length);
            if (_article.WrittenCount + needed > _maxArticleBytes)
            {
                _articleExceeded = true;
                return;
            }

            var span = _article.GetSpan(needed);
            wireBytes.CopyTo(span);
            _article.Advance(needed);
        }

        /// <summary>
        /// Copies a command line that fits. An overlong line is consumed by the caller and is not copied.
        /// </summary>
        private bool TryCopyCommandLine(ReadOnlySequence<byte> lineBytes)
        {
            if (lineBytes.Length > _commandScratch.Length)
            {
                _commandLength = 0;
                return false;
            }

            var length = checked((int)lineBytes.Length);
            lineBytes.CopyTo(_commandScratch);
            _commandLength = length;
            return true;
        }
    }
}
