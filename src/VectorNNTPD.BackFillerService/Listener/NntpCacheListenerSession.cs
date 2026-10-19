using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.Common.Articles.Validation;

namespace VectorNNTPD.BackFillerService.Listener
{
    /// <summary>
    /// Partial RFC 3977 session on an already-authenticated TLS transport.
    /// Serves <c>ARTICLE</c> by Message-ID, <c>DATE</c>, and <c>QUIT</c>. Does not own the socket or the certificate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The greeting is <c>201</c> because this server does not implement <c>POST</c>.
    /// Commands are parsed from bytes. A command line longer than 512 octets, including CRLF, is rejected
    /// whole and is not split into a later command. Bytes after one command stay in the buffer for the next.
    /// </para>
    /// <para>
    /// A successful <c>ARTICLE</c> writes <c>220 0</c>, the multiline article image, and one <c>.CRLF</c> terminator.
    /// Header lines that begin with <c>.</c> gain one stuffing dot in that image only. The retained body is written
    /// as its own slice, so a stuffing dot already stored on a body line is not added again. The retained entry is removed
    /// only after every response write completes. A failure before that rolls the claim
    /// back when the entry is still eligible. Completion of the local writes is not proof the peer processed the article.
    /// A disconnect during the write can leave the peer with a prefix and, after rollback, a later retry can deliver
    /// the article again. A commit followed by a lost connection cannot be delivered again.
    /// </para>
    /// </remarks>
    internal sealed class NntpCacheListenerSession
    {
        /// <summary>RFC 3977 command-line limit, including the terminating CRLF. A longer line is rejected whole.</summary>
        private const int MaximumCommandOctets = 512;

        /// <summary>Initial <c>201</c> greeting. Posting is not implemented.</summary>
        private static readonly byte[] Greeting = "201 NNTP Service Ready, posting prohibited\r\n"u8.ToArray();

        /// <summary>Suffix of the <c>220</c> response, including CRLF.</summary>
        private static readonly byte[] ArticleFollowsSuffix = " article follows\r\n"u8.ToArray();

        /// <summary>Fixed prefix of a successful <c>ARTICLE</c> response. The article number is always <c>0</c>.</summary>
        private static readonly byte[] ArticlePrefix = "220 0 "u8.ToArray();

        /// <summary>Terminating dot line written after the article image.</summary>
        private static readonly byte[] Terminator = ".\r\n"u8.ToArray();

        /// <summary><c>430</c> when no retained article matches the Message-ID.</summary>
        private static readonly byte[] NoSuchArticle = "430 No Such Article Found\r\n"u8.ToArray();

        /// <summary>
        /// <c>403</c> when the Message-ID matches an entry that is in delivery or not frameable. Nothing is claimed.
        /// </summary>
        private static readonly byte[] NotAvailable = "403 Article not available\r\n"u8.ToArray();

        /// <summary>
        /// <c>412</c> when <c>ARTICLE</c> has no argument or the argument is a numeric article number. Number selection is not implemented.
        /// </summary>
        private static readonly byte[] NoNewsgroup = "412 No newsgroup selected\r\n"u8.ToArray();

        /// <summary>
        /// <c>501</c> for an overlong command, <c>QUIT</c> or <c>DATE</c> with an argument, <c>ARTICLE</c> with more than one argument, or a Message-ID that is not a valid token.
        /// </summary>
        private static readonly byte[] SyntaxError = "501 Syntax error\r\n"u8.ToArray();

        /// <summary><c>500</c> when the command is not <c>ARTICLE</c>, <c>DATE</c>, or <c>QUIT</c>.</summary>
        private static readonly byte[] UnknownCommand = "500 Unknown command\r\n"u8.ToArray();

        /// <summary><c>205</c> written before the session stops reading after <c>QUIT</c>.</summary>
        private static readonly byte[] Closing = "205 Connection closing\r\n"u8.ToArray();

        /// <summary>ASCII command verb compared case-insensitively.</summary>
        private static readonly byte[] ArticleKeyword = "ARTICLE"u8.ToArray();

        /// <summary>ASCII command verb compared case-insensitively.</summary>
        private static readonly byte[] DateKeyword = "DATE"u8.ToArray();

        /// <summary>ASCII command verb compared case-insensitively.</summary>
        private static readonly byte[] QuitKeyword = "QUIT"u8.ToArray();

        /// <summary>Fixed prefix of an RFC 3977 <c>111</c> DATE response, before the fourteen UTC digits.</summary>
        private static readonly byte[] DatePrefix = "111 "u8.ToArray();

        /// <summary>Connected transport. Disposed by the listener, not by this session.</summary>
        private readonly ICacheListenerTransport _transport;

        /// <summary>Retention authority. This session does not index or copy articles.</summary>
        private readonly IArticleRetentionAuthority _retention;

        /// <summary>
        /// Clock for <c>DATE</c>. Production passes the same <see cref="TimeProvider"/> instance used for article admission.
        /// </summary>
        private readonly TimeProvider _time;

        /// <summary>Unread command bytes. The command being handled stays at the front until its response writes finish.</summary>
        private byte[] _pending = new byte[MaximumCommandOctets];

        /// <summary>Valid prefix length of <see cref="_pending"/>.</summary>
        private int _pendingCount;

        /// <summary>
        /// Reused <c>111</c> response. Twenty octets. The next <c>DATE</c> does not write it until the previous write has returned.
        /// </summary>
        private readonly byte[] _dateResponse = new byte[DatePrefix.Length + 14 + 2];

        /// <summary>
        /// Reused <c>220</c> response. Sized for the longest RFC 5536 message-id.
        /// The next <c>ARTICLE</c> does not write it until the previous status write has returned.
        /// </summary>
        private readonly byte[] _articleFollows = new byte[
            ArticlePrefix.Length + NntpMessageIdValidation.MaxMessageIdLength + ArticleFollowsSuffix.Length];

        /// <summary>Initializes a session over an established transport.</summary>
        /// <param name="transport">Connected transport. Not disposed by this session.</param>
        /// <param name="retention">Article owner. Claims are committed only after a complete article write.</param>
        /// <param name="time">
        /// Clock for <c>DATE</c>. Null uses <see cref="TimeProvider.System"/>.
        /// The listener service passes the retention clock.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="transport"/> or <paramref name="retention"/> is null.</exception>
        internal NntpCacheListenerSession(
            ICacheListenerTransport transport,
            IArticleRetentionAuthority retention,
            TimeProvider? time = null)
        {
            ArgumentNullException.ThrowIfNull(transport);
            ArgumentNullException.ThrowIfNull(retention);
            _transport = transport;
            _retention = retention;
            _time = time ?? TimeProvider.System;
        }

        /// <summary>
        /// Sends the greeting, then reads commands until <c>QUIT</c>, peer close, or cancellation.
        /// </summary>
        /// <param name="cancellationToken">Cancels a blocked read or write. Does not commit an in-progress delivery.</param>
        /// <returns>A task that completes when the session stops reading.</returns>
        /// <remarks>
        /// <see cref="IOException"/> and <see cref="OperationCanceledException"/> propagate to the listener.
        /// An article claim is rolled back if the write sequence throws.
        /// </remarks>
        internal async Task RunAsync(CancellationToken cancellationToken)
        {
            await WriteFullyAsync(Greeting, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await ReadCommandAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (read.Kind == CommandReadKind.Disconnected)
                    {
                        return;
                    }

                    if (read.Kind == CommandReadKind.Overlong)
                    {
                        await WriteFullyAsync(SyntaxError, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var line = read.Line;
                    if (EqualsIgnoreCase(Keyword(line), QuitKeyword))
                    {
                        if (HasArgument(line))
                        {
                            await WriteFullyAsync(SyntaxError, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        await WriteFullyAsync(Closing, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (EqualsIgnoreCase(Keyword(line), DateKeyword))
                    {
                        if (HasArgument(line))
                        {
                            await WriteFullyAsync(SyntaxError, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        await WriteFullyAsync(BuildDateResponse(), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (!EqualsIgnoreCase(Keyword(line), ArticleKeyword))
                    {
                        await WriteFullyAsync(UnknownCommand, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await HandleArticleAsync(line, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (read.Consumed > 0)
                    {
                        RemoveFront(read.Consumed);
                    }
                }
            }
        }

        /// <summary>Resolves one <c>ARTICLE</c> command and writes the response without holding the retention gate.</summary>
        /// <param name="line">Command line without its CRLF.</param>
        /// <param name="cancellationToken">Cancels the response writes.</param>
        /// <returns>A task that completes when the response has been written or the claim has been rolled back.</returns>
        /// <remarks>
        /// <see cref="WriteFullyAsync"/> returns only after the transport write has completed or thrown, so the
        /// claim lease is still held while <see cref="ICacheListenerTransport.WriteAsync"/> reads
        /// <see cref="ArticleRetentionAuthority.ArticleDeliveryClaim.ArticleBytes"/>. A stuffed header is a separate
        /// small buffer. The body write is the retained slice and is not a copy. Commit and dispose run after
        /// every one of those awaits, on this call. This session is the only owner of the claim, so settlement does not overlap the write.
        /// </remarks>
        private async Task HandleArticleAsync(ReadOnlyMemory<byte> line, CancellationToken cancellationToken)
        {
            if (!TryReadSingleArgument(line, out var argument))
            {
                await WriteFullyAsync(argument.IsEmpty ? NoNewsgroup : SyntaxError, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsArticleNumber(argument.Span))
            {
                await WriteFullyAsync(NoNewsgroup, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!NntpMessageIdValidation.IsValidMessageId(argument.Span))
            {
                await WriteFullyAsync(SyntaxError, cancellationToken).ConfigureAwait(false);
                return;
            }

            var claimed = _retention.TryClaimForDelivery(argument.Span);
            if (claimed.Kind == ArticleDeliveryClaimKind.NotFound)
            {
                await WriteFullyAsync(NoSuchArticle, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (claimed.Kind != ArticleDeliveryClaimKind.Claimed)
            {
                await WriteFullyAsync(NotAvailable, cancellationToken).ConfigureAwait(false);
                return;
            }

            using var claim = claimed.Claim!;
            var article = claim.ArticleBytes;
            var bodyStart = claim.DeliveryFraming.BodyStart;
            byte[]? stuffedHeader = null;
            var splitBody = bodyStart >= 0
                && ArticleWorkDeliveryFraming.TryCreateStuffedHeader(article.Span, bodyStart, out stuffedHeader);

            await WriteFullyAsync(BuildFollowsLine(argument.Span), cancellationToken).ConfigureAwait(false);
            if (splitBody)
            {
                await WriteFullyAsync(stuffedHeader, cancellationToken).ConfigureAwait(false);
                await WriteFullyAsync(article[bodyStart..], cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteFullyAsync(article, cancellationToken).ConfigureAwait(false);
            }

            await WriteFullyAsync(Terminator, cancellationToken).ConfigureAwait(false);
            claim.Commit();
        }

        /// <summary>Reads one command, or reports that the peer closed or the line exceeded 512 octets.</summary>
        /// <param name="cancellationToken">Cancels the transport read.</param>
        /// <returns>The next command, an overlong marker, or disconnect.</returns>
        /// <remarks>
        /// A command result refers to the front of <see cref="_pending"/> and does not copy it.
        /// The caller drops those bytes only after the handler has finished, including its response writes.
        /// </remarks>
        private async Task<CommandRead> ReadCommandAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var crlf = IndexOfCrlf(_pending.AsSpan(0, _pendingCount));
                if (crlf >= 0)
                {
                    var lineLength = crlf + 2;
                    if (lineLength > MaximumCommandOctets)
                    {
                        RemoveFront(lineLength);
                        return CommandRead.Overlong();
                    }

                    return CommandRead.Command(_pending.AsMemory(0, crlf), lineLength);
                }

                if (_pendingCount >= MaximumCommandOctets)
                {
                    if (!await DiscardThroughCrlfAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return CommandRead.Disconnected();
                    }

                    return CommandRead.Overlong();
                }

                if (_pendingCount == _pending.Length)
                {
                    Array.Resize(ref _pending, _pending.Length * 2);
                }

                var read = await _transport.ReadAsync(_pending.AsMemory(_pendingCount), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return CommandRead.Disconnected();
                }

                _pendingCount += read;
            }
        }

        /// <summary>Drops bytes until a CRLF, keeping any pipelined bytes that follow it.</summary>
        /// <param name="cancellationToken">Cancels the transport read.</param>
        /// <returns><see langword="false"/> when the peer closes before a CRLF arrives.</returns>
        /// <remarks>
        /// A trailing CR is kept when the buffer has no CRLF yet. The next read may supply the LF,
        /// and dropping that CR would hide the terminator or glue the following command to a bare LF.
        /// </remarks>
        private async Task<bool> DiscardThroughCrlfAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var crlf = IndexOfCrlf(_pending.AsSpan(0, _pendingCount));
                if (crlf >= 0)
                {
                    RemoveFront(crlf + 2);
                    return true;
                }

                var keepTrailingCr = _pendingCount > 0 && _pending[_pendingCount - 1] == (byte)'\r';
                _pendingCount = 0;
                if (keepTrailingCr)
                {
                    _pending[0] = (byte)'\r';
                    _pendingCount = 1;
                }

                var read = await _transport.ReadAsync(_pending.AsMemory(_pendingCount), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return false;
                }

                _pendingCount += read;
            }
        }

        /// <summary>Writes every byte of <paramref name="payload"/>, or throws before the caller treats the write as complete.</summary>
        /// <param name="payload">Bytes to write. Empty input completes without a transport call.</param>
        /// <param name="cancellationToken">Cancels the transport write.</param>
        /// <returns>A task that completes when every byte has been accepted.</returns>
        /// <exception cref="IOException">
        /// The transport accepted zero bytes or more bytes than remain in <paramref name="payload"/>.
        /// </exception>
        /// <remarks>
        /// The whole remaining buffer is passed on each call. A transport whose write completes the buffer,
        /// including <see cref="StreamCacheListenerTransport"/>, therefore performs one write.
        /// A positive count shorter than the buffer is progress: the next call starts at the first unaccepted byte
        /// and that transport call carries its own no-progress deadline. Bytes are not copied into a chunk buffer.
        /// </remarks>
        private async Task WriteFullyAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            var written = 0;
            while (written < payload.Length)
            {
                var accepted = await _transport.WriteAsync(payload[written..], cancellationToken).ConfigureAwait(false);
                if (accepted <= 0 || accepted > payload.Length - written)
                {
                    throw new IOException("Transport returned an accepted count outside the remaining buffer.");
                }

                written += accepted;
            }
        }

        /// <summary>Drops <paramref name="count"/> bytes from the front of the pending buffer.</summary>
        /// <param name="count">Bytes already parsed, including a CRLF when the caller consumed a line.</param>
        private void RemoveFront(int count)
        {
            var remaining = _pendingCount - count;
            if (remaining > 0)
            {
                Buffer.BlockCopy(_pending, count, _pending, 0, remaining);
            }

            _pendingCount = Math.Max(0, remaining);
        }

        /// <summary>Finds the first CRLF in <paramref name="buffer"/>.</summary>
        /// <param name="buffer">Pending command bytes.</param>
        /// <returns>The index of CR, or -1 when no CRLF is present. A bare CR or LF is not a terminator.</returns>
        private static int IndexOfCrlf(ReadOnlySpan<byte> buffer)
        {
            for (var i = 0; i < buffer.Length - 1; i++)
            {
                if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n')
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Returns the keyword at the start of <paramref name="line"/>, stopping at the first space or tab.</summary>
        /// <param name="line">Command line without CRLF.</param>
        /// <returns>The keyword span. Empty when <paramref name="line"/> is empty.</returns>
        private static ReadOnlySpan<byte> Keyword(ReadOnlyMemory<byte> line)
        {
            var span = line.Span;
            var end = 0;
            while (end < span.Length && span[end] is not (byte)' ' and not (byte)'\t')
            {
                end++;
            }

            return span[..end];
        }

        /// <summary>Reports whether <paramref name="line"/> has a non-empty argument after the keyword.</summary>
        /// <param name="line">Command line without CRLF.</param>
        /// <returns><see langword="true"/> when any octet remains after the keyword and its following spaces or tabs.</returns>
        private static bool HasArgument(ReadOnlyMemory<byte> line)
        {
            var span = line.Span;
            var index = KeywordLength(span);
            while (index < span.Length && span[index] is (byte)' ' or (byte)'\t')
            {
                index++;
            }

            return index < span.Length;
        }

        /// <summary>
        /// Reads the single argument of an <c>ARTICLE</c> command.
        /// </summary>
        /// <param name="line">Command line without CRLF. The argument is a slice of this memory.</param>
        /// <param name="argument">
        /// The argument when there is exactly one. Empty when the argument is missing.
        /// When extra arguments are present, <paramref name="argument"/> is non-empty and the method returns false.
        /// The memory is a slice of <paramref name="line"/>, not a copy.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when there is exactly one argument.
        /// <see langword="false"/> when the argument is missing or more than one argument is present.
        /// </returns>
        private static bool TryReadSingleArgument(ReadOnlyMemory<byte> line, out ReadOnlyMemory<byte> argument)
        {
            var span = line.Span;
            var index = KeywordLength(span);
            while (index < span.Length && span[index] is (byte)' ' or (byte)'\t')
            {
                index++;
            }

            if (index >= span.Length)
            {
                argument = ReadOnlyMemory<byte>.Empty;
                return false;
            }

            var start = index;
            while (index < span.Length && span[index] is not (byte)' ' and not (byte)'\t')
            {
                index++;
            }

            var end = index;
            while (index < span.Length && span[index] is (byte)' ' or (byte)'\t')
            {
                index++;
            }

            argument = line.Slice(start, end - start);
            return index >= span.Length;
        }

        /// <summary>Counts the keyword octets at the start of <paramref name="line"/>.</summary>
        /// <param name="line">Command line without CRLF.</param>
        /// <returns>The index of the first space, tab, or the line length.</returns>
        private static int KeywordLength(ReadOnlySpan<byte> line)
        {
            var end = 0;
            while (end < line.Length && line[end] is not (byte)' ' and not (byte)'\t')
            {
                end++;
            }

            return end;
        }

        /// <summary>Reports whether <paramref name="argument"/> is an article-number form.</summary>
        /// <param name="argument">The single command argument.</param>
        /// <returns><see langword="true"/> when every octet is a decimal digit.</returns>
        private static bool IsArticleNumber(ReadOnlySpan<byte> argument)
        {
            if (argument.IsEmpty)
            {
                return false;
            }

            foreach (var value in argument)
            {
                if (value is < (byte)'0' or > (byte)'9')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Compares an ASCII keyword without allocating.</summary>
        /// <param name="value">Keyword octets from the command.</param>
        /// <param name="expected">Uppercase keyword.</param>
        /// <returns><see langword="true"/> when the letters match ignoring ASCII case.</returns>
        private static bool EqualsIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> expected)
        {
            if (value.Length != expected.Length)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var current = value[i];
                if ((uint)(current - (byte)'a') <= (byte)'z' - (byte)'a')
                {
                    current = (byte)(current - 32);
                }

                if (current != expected[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Builds RFC 3977 <c>111 yyyymmddhhmmss</c> from <see cref="_time"/>, in UTC, ending in CRLF.
        /// Seconds are the clock's whole seconds. The response has no subsecond field.
        /// </summary>
        /// <returns>Twenty octets in <see cref="_dateResponse"/>: the status, fourteen digits, and CRLF.</returns>
        /// <remarks>The returned memory stays valid until the next <c>DATE</c> on this session, which waits for the write to finish.</remarks>
        private ReadOnlyMemory<byte> BuildDateResponse()
        {
            var utc = _time.GetUtcNow().UtcDateTime;
            DatePrefix.CopyTo(_dateResponse);
            var digits = _dateResponse.AsSpan(DatePrefix.Length, 14);
            WriteDigits(digits[..4], utc.Year);
            WriteDigits(digits.Slice(4, 2), utc.Month);
            WriteDigits(digits.Slice(6, 2), utc.Day);
            WriteDigits(digits.Slice(8, 2), utc.Hour);
            WriteDigits(digits.Slice(10, 2), utc.Minute);
            WriteDigits(digits.Slice(12, 2), utc.Second);
            _dateResponse[^2] = (byte)'\r';
            _dateResponse[^1] = (byte)'\n';
            return _dateResponse;
        }

        /// <summary>Writes <paramref name="value"/> as ASCII digits into <paramref name="destination"/>, most significant digit first.</summary>
        /// <param name="destination">Exact width of the field. Four for the year and two for each other field.</param>
        /// <param name="value">Non-negative field value. Wider values keep only the least significant digits.</param>
        private static void WriteDigits(Span<byte> destination, int value)
        {
            for (var i = destination.Length - 1; i >= 0; i--)
            {
                destination[i] = (byte)((byte)'0' + (value % 10));
                value /= 10;
            }
        }

        /// <summary>Writes <c>220 0 &lt;message-id&gt; article follows</c> ending in CRLF into <see cref="_articleFollows"/>.</summary>
        /// <param name="messageId">Message-ID octets from the command. Not rewritten. At most <see cref="NntpMessageIdValidation.MaxMessageIdLength"/> octets.</param>
        /// <returns>The status line, including CRLF. Valid until the next successful <c>ARTICLE</c> on this session.</returns>
        private ReadOnlyMemory<byte> BuildFollowsLine(ReadOnlySpan<byte> messageId)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(messageId.Length, NntpMessageIdValidation.MaxMessageIdLength);
            var length = ArticlePrefix.Length + messageId.Length + ArticleFollowsSuffix.Length;
            ArticlePrefix.CopyTo(_articleFollows);
            messageId.CopyTo(_articleFollows.AsSpan(ArticlePrefix.Length));
            ArticleFollowsSuffix.CopyTo(_articleFollows.AsSpan(ArticlePrefix.Length + messageId.Length));
            return _articleFollows.AsMemory(0, length);
        }

        /// <summary>One read from the command buffer.</summary>
        private readonly struct CommandRead
        {
            /// <summary>Stores a classified read.</summary>
            /// <param name="kind">Whether a command, an overlong line, or disconnect was observed.</param>
            /// <param name="line">Command bytes without CRLF. Empty unless <paramref name="kind"/> is a command.</param>
            /// <param name="consumed">
            /// Bytes at the front of the pending buffer that the caller drops after the handler finishes.
            /// Zero when this read already discarded its line or has no line.
            /// </param>
            private CommandRead(CommandReadKind kind, ReadOnlyMemory<byte> line, int consumed)
            {
                Kind = kind;
                Line = line;
                Consumed = consumed;
            }

            /// <summary>Whether a complete command, an overlong discard, or peer close was read from the buffer.</summary>
            internal CommandReadKind Kind { get; }

            /// <summary>Command octets without CRLF when <see cref="Kind"/> is <see cref="CommandReadKind.Command"/>; otherwise empty.</summary>
            internal ReadOnlyMemory<byte> Line { get; }

            /// <summary>Pending-buffer prefix the caller removes after this line is no longer used, including its CRLF.</summary>
            internal int Consumed { get; }

            /// <summary>Creates a parsed command that still occupies the front of the pending buffer.</summary>
            /// <param name="line">Command bytes without CRLF. A slice of the pending buffer.</param>
            /// <param name="consumed">Line length including CRLF.</param>
            /// <returns>A command read.</returns>
            internal static CommandRead Command(ReadOnlyMemory<byte> line, int consumed) =>
                new(CommandReadKind.Command, line, consumed);

            /// <summary>Creates an overlong-line result whose bytes were already discarded.</summary>
            /// <returns>A read that must be answered with <c>501</c> and not interpreted.</returns>
            internal static CommandRead Overlong() => new(CommandReadKind.Overlong, ReadOnlyMemory<byte>.Empty, 0);

            /// <summary>Creates a disconnect result.</summary>
            /// <returns>A read that ends the session without a further response.</returns>
            internal static CommandRead Disconnected() => new(CommandReadKind.Disconnected, ReadOnlyMemory<byte>.Empty, 0);
        }

        /// <summary>Classification of one command-buffer read.</summary>
        private enum CommandReadKind : byte
        {
            /// <summary>
            /// A CRLF-terminated command of at most 512 octets. The line references the session pending buffer and is not a copy.
            /// </summary>
            /// <remarks>
            /// Do not retain or use that memory after the pending buffer is consumed, compacted, resized, or reused.
            /// </remarks>
            Command = 0,

            /// <summary>The line exceeded 512 octets and was discarded through its CRLF.</summary>
            Overlong = 1,

            /// <summary>The peer closed before another command was complete.</summary>
            Disconnected = 2,
        }
    }
}
