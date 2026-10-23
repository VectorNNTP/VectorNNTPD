using System.Buffers;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles.Validation;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;

namespace VectorNNTPD.StorageService.Listener
{
    /// <summary>
    /// RFC 4644 STREAMING feed session over an established TLS transport.
    /// Accepts <c>CAPABILITIES</c>, <c>MODE STREAM</c>, <c>CHECK</c>, <c>TAKETHIS</c>, and <c>QUIT</c>.
    /// A successful <c>TAKETHIS</c> commits into <see cref="IAcceptedArticleQueue"/> before <c>239</c> is written.
    /// </summary>
    public sealed partial class StorageNntpFeedSession : IAsyncDisposable
    {
        private const int MaximumCommandOctets = 512;

        private static readonly byte[] Greeting = "200 VectorNNTP StorageServer ready\r\n"u8.ToArray();
        private static readonly byte[] CapabilitiesHeader = "101 Capability list follows\r\n"u8.ToArray();
        private static readonly byte[] CapabilitiesBody = "VERSION 2\r\nSTREAMING\r\nIMPLEMENTATION VectorNNTP-StorageServer\r\n.\r\n"u8.ToArray();
        private static readonly byte[] ModeStreamOk = "203 Streaming allowed\r\n"u8.ToArray();
        private static readonly byte[] Closing = "205 Connection closing\r\n"u8.ToArray();
        private static readonly byte[] SyntaxError = "501 Syntax error\r\n"u8.ToArray();
        private static readonly byte[] UnknownCommand = "500 Unknown command\r\n"u8.ToArray();
        private static readonly byte[] TemporaryFailure = "400 Temporary failure\r\n"u8.ToArray();

        private static readonly byte[] CapabilitiesKeyword = "CAPABILITIES"u8.ToArray();
        private static readonly byte[] ModeKeyword = "MODE"u8.ToArray();
        private static readonly byte[] StreamKeyword = "STREAM"u8.ToArray();
        private static readonly byte[] CheckKeyword = "CHECK"u8.ToArray();
        private static readonly byte[] TakeThisKeyword = "TAKETHIS"u8.ToArray();
        private static readonly byte[] QuitKeyword = "QUIT"u8.ToArray();

        private readonly IStorageListenerTransport _transport;
        private readonly StorageServerListenerRuntimeOptions _listener;
        private readonly ILogger _logger;
        private readonly IAcceptedArticleQueue? _acceptedArticles;
        private readonly StoreAssemblyAdmission? _assemblyAdmission;
        private readonly Func<ArticleId, bool>? _articlePresent;
        private readonly NntpArticleParser _parser;
        private byte[] _pending;
        private readonly byte[] _readScratch;
        private readonly byte[] _statusScratch;
        private int _pendingCount;
        private int _disposed;

        /// <summary>Initializes a STREAMING feed session over an established transport.</summary>
        public StorageNntpFeedSession(
            IStorageListenerTransport transport,
            StorageServerListenerRuntimeOptions listener,
            ILogger logger,
            string pathIdentity,
            IAcceptedArticleQueue? acceptedArticles = null,
            StoreAssemblyAdmission? assemblyAdmission = null,
            Func<ArticleId, bool>? articlePresent = null)
        {
            ArgumentNullException.ThrowIfNull(transport);
            ArgumentNullException.ThrowIfNull(listener);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentException.ThrowIfNullOrWhiteSpace(pathIdentity);
            _transport = transport;
            _listener = listener;
            _logger = logger;
            _acceptedArticles = acceptedArticles;
            _assemblyAdmission = assemblyAdmission;
            _articlePresent = articlePresent;
            _parser = new NntpArticleParser(pathIdentity.Trim());
            var capacity = Math.Max(listener.ParserAccumulationMaxBytes, MaximumCommandOctets);
            _pending = new byte[MaximumCommandOctets];
            _readScratch = new byte[Math.Min(capacity, 64 * 1024)];
            _statusScratch = new byte[4 + 1 + NntpMessageIdValidation.MaxMessageIdLength + 2];
        }

        /// <summary>Sends the greeting, then reads commands until <c>QUIT</c>, peer close, or cancellation.</summary>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            await WriteFullyAsync(Greeting, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                var command = await ReadCommandAsync(cancellationToken).ConfigureAwait(false);
                if (command.Kind == CommandReadKind.Disconnected)
                {
                    return;
                }

                if (command.Kind == CommandReadKind.Overlong)
                {
                    await WriteFullyAsync(SyntaxError, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var line = command.Line.Span;
                if (TryMatchKeyword(line, QuitKeyword, out var quitRest))
                {
                    if (!quitRest.IsEmpty)
                    {
                        await WriteFullyAsync(SyntaxError, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await WriteFullyAsync(Closing, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchKeyword(line, CapabilitiesKeyword, out var capsRest))
                {
                    if (!capsRest.IsEmpty)
                    {
                        await WriteFullyAsync(SyntaxError, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await WriteFullyAsync(CapabilitiesHeader, cancellationToken).ConfigureAwait(false);
                    await WriteFullyAsync(CapabilitiesBody, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (TryMatchModeStream(line, out var modeStreamSyntaxError))
                {
                    await WriteFullyAsync(
                            modeStreamSyntaxError ? SyntaxError : ModeStreamOk,
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (TryMatchKeyword(line, CheckKeyword, out var checkRest))
                {
                    var checkArg = SkipSpaces(checkRest).ToArray();
                    await HandleCheckAsync(checkArg, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (TryMatchKeyword(line, TakeThisKeyword, out var takeRest))
                {
                    var takeArg = SkipSpaces(takeRest).ToArray();
                    var keepOpen = await HandleTakeThisAsync(takeArg, cancellationToken).ConfigureAwait(false);
                    if (!keepOpen)
                    {
                        return;
                    }

                    continue;
                }

                await WriteFullyAsync(UnknownCommand, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return ValueTask.CompletedTask;
            }

            return ValueTask.CompletedTask;
        }

        private async Task HandleCheckAsync(byte[] argument, CancellationToken cancellationToken)
        {
            if (!TryParseMessageIdArgument(argument, out var messageId))
            {
                await WriteFullyAsync(SyntaxError, cancellationToken).ConfigureAwait(false);
                return;
            }

            var messageIdBytes = messageId.ToArray();
            var alreadyHave = _articlePresent?.Invoke(ArticleId.FromMessageId(messageIdBytes)) == true;
            await WriteStatusAsync(
                    alreadyHave ? (byte)'4' : (byte)'2',
                    (byte)'3',
                    (byte)'8',
                    messageIdBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<bool> HandleTakeThisAsync(byte[] argument, CancellationToken cancellationToken)
        {
            if (!TryParseMessageIdArgument(argument, out var requestMessageId))
            {
                // Peer already started the multiline; drain it so the next command is not contaminated.
                await DrainArticleAsync(cancellationToken).ConfigureAwait(false);
                await WriteFullyAsync(SyntaxError, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var requestIdBytes = requestMessageId.ToArray();
            var requestId = ArticleId.FromMessageId(requestIdBytes);

            StoreAssemblyAdmission? admission = null;
            var gate = _assemblyAdmission;
            if (gate is not null)
            {
                if (!gate.TryEnter())
                {
                    await DrainArticleAsync(cancellationToken).ConfigureAwait(false);
                    await WriteStatusAsync(
                            (byte)'4',
                            (byte)'3',
                            (byte)'9',
                            requestIdBytes,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

                admission = gate;
            }

            byte[]? rented = null;
            QueueReservation? reservation = null;
            try
            {
                var destuffed = await ReadDestuffedArticleAsync(cancellationToken).ConfigureAwait(false);
                rented = destuffed.Array;
                var payload = destuffed.Memory;

                if (_articlePresent?.Invoke(requestId) == true)
                {
                    await WriteStatusAsync((byte)'4', (byte)'3', (byte)'9', requestIdBytes, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

                var created = ArticleRecordFactory.TryCreate(
                    _parser,
                    payload,
                    ArticlePathMode.Normalize);
                if (!created.IsAccepted)
                {
                    await WriteStatusAsync((byte)'4', (byte)'3', (byte)'9', requestIdBytes, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

                var record = created.Record;
                if (!record.ArtId.Equals(requestId))
                {
                    record.ReleaseUnusedCanonicalBuffer();
                    await WriteStatusAsync((byte)'4', (byte)'3', (byte)'9', requestIdBytes, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

                var queue = _acceptedArticles;
                if (queue is null || queue.IsCompleted)
                {
                    record.ReleaseUnusedCanonicalBuffer();
                    await WriteFullyAsync(TemporaryFailure, cancellationToken).ConfigureAwait(false);
                    return false;
                }

                if (record.ArtSize > queue.MaxQueuedBytes)
                {
                    record.ReleaseUnusedCanonicalBuffer();
                    await WriteStatusAsync((byte)'4', (byte)'3', (byte)'9', requestIdBytes, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

                if (!queue.TryReserve(record.ArtSize, out var reserved))
                {
                    _transport.SetReadProgressSuspended(true);
                    try
                    {
                        reservation = await queue.ReserveAsync(record.ArtSize, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _transport.SetReadProgressSuspended(false);
                    }

                    if (reservation is null)
                    {
                        record.ReleaseUnusedCanonicalBuffer();
                        await WriteStatusAsync((byte)'4', (byte)'3', (byte)'9', requestIdBytes, cancellationToken)
                            .ConfigureAwait(false);
                        return true;
                    }
                }
                else
                {
                    reservation = reserved;
                }

                var queued = new AcceptedArticle(record.ArtId, record.ArtHash, record.ArtSize, record.ArtData);
                bool committed;
                try
                {
                    committed = queue.TryCommit(reservation.Value, queued);
                }
                catch
                {
                    queue.Release(reservation.Value);
                    reservation = null;
                    record.ReleaseUnusedCanonicalBuffer();
                    throw;
                }

                reservation = null;
                if (!committed)
                {
                    record.ReleaseUnusedCanonicalBuffer();
                    await WriteFullyAsync(TemporaryFailure, cancellationToken).ConfigureAwait(false);
                    return false;
                }

                // ArtData remains reachable via the queued Memory; Exact buffers are not pooled.
                StorageNntpFeedSessionLogMessages.ArticleAccepted(
                    _logger,
                    record.ArtId.ToLowerHexString());
                await WriteStatusAsync((byte)'2', (byte)'3', (byte)'9', requestIdBytes, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (EndOfStreamException)
            {
                if (reservation is { } held)
                {
                    _acceptedArticles?.Release(held);
                }

                throw;
            }
            catch (InvalidOperationException)
            {
                // Oversized article or unrecoverable framing residue: RFC 4644 temporary failure + close.
                if (reservation is { } held)
                {
                    _acceptedArticles?.Release(held);
                }

                await WriteFullyAsync(TemporaryFailure, cancellationToken).ConfigureAwait(false);
                return false;
            }
            finally
            {
                if (rented is not null)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }

                admission?.Exit();
            }
        }

        private async Task DrainArticleAsync(CancellationToken cancellationToken)
        {
            try
            {
                var destuffed = await ReadDestuffedArticleAsync(cancellationToken).ConfigureAwait(false);
                if (destuffed.Array is not null)
                {
                    ArrayPool<byte>.Shared.Return(destuffed.Array);
                }
            }
            catch (EndOfStreamException ex)
            {
                ExpectedException.Ignore(ex);
            }
        }

        private Task<(byte[]? Array, ReadOnlyMemory<byte> Memory)> ReadDestuffedArticleAsync(
            CancellationToken cancellationToken)
            => ReadDestuffedCoreAsync(ArticleResourceLimits.MaxArticleBytes, cancellationToken);

        private async Task<(byte[]? Array, ReadOnlyMemory<byte> Memory)> ReadDestuffedCoreAsync(
            int maxBytes,
            CancellationToken cancellationToken)
        {
            var rented = ArrayPool<byte>.Shared.Rent(Math.Min(Math.Max(maxBytes, 1024), 1024 * 1024));
            var written = 0;
            var atLineStart = true;
            var input = ArrayPool<byte>.Shared.Rent(_readScratch.Length);
            var inputCount = 0;
            var inputOffset = 0;
            try
            {
                // Seed from any leftover command-buffer bytes that follow the TAKETHIS line.
                if (_pendingCount > 0)
                {
                    if (_pendingCount > input.Length)
                    {
                        ArrayPool<byte>.Shared.Return(input);
                        input = ArrayPool<byte>.Shared.Rent(_pendingCount);
                    }

                    _pending.AsSpan(0, _pendingCount).CopyTo(input);
                    inputCount = _pendingCount;
                    _pendingCount = 0;
                }

                while (true)
                {
                    if (inputCount - inputOffset == 0)
                    {
                        inputOffset = 0;
                        inputCount = await _transport.ReadAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
                        if (inputCount == 0)
                        {
                            throw new EndOfStreamException("NNTP TAKETHIS ended before multiline terminator.");
                        }
                    }

                    var span = input.AsSpan(inputOffset, inputCount - inputOffset);
                    var index = 0;
                    var needMore = false;
                    while (index < span.Length)
                    {
                        if (atLineStart && span[index] == (byte)'.')
                        {
                            if (index + 1 >= span.Length
                                || (span[index + 1] == (byte)'\r' && index + 2 >= span.Length))
                            {
                                needMore = true;
                                break;
                            }

                            var next = span[index + 1];
                            if (next == (byte)'\n')
                            {
                                inputOffset += index + 2;
                                PreserveLeftover(input, inputOffset, inputCount - inputOffset);
                                ArrayPool<byte>.Shared.Return(input);
                                return (rented, rented.AsMemory(0, written));
                            }

                            if (next == (byte)'\r' && span[index + 2] == (byte)'\n')
                            {
                                inputOffset += index + 3;
                                PreserveLeftover(input, inputOffset, inputCount - inputOffset);
                                ArrayPool<byte>.Shared.Return(input);
                                return (rented, rented.AsMemory(0, written));
                            }

                            index += 1;
                            atLineStart = false;
                            continue;
                        }

                        var start = index;
                        do
                        {
                            var current = span[index++];
                            atLineStart = current is (byte)'\r' or (byte)'\n';
                        }
                        while (index < span.Length && !(atLineStart && span[index] == (byte)'.'));

                        var slice = span[start..index];
                        if (written > maxBytes - slice.Length)
                        {
                            throw new InvalidOperationException("NNTP TAKETHIS article exceeds maximum size.");
                        }

                        if (written + slice.Length > rented.Length)
                        {
                            var grown = ArrayPool<byte>.Shared.Rent(
                                Math.Min(maxBytes, Math.Max(rented.Length * 2, written + slice.Length)));
                            rented.AsSpan(0, written).CopyTo(grown);
                            ArrayPool<byte>.Shared.Return(rented);
                            rented = grown;
                        }

                        slice.CopyTo(rented.AsSpan(written));
                        written += slice.Length;
                    }

                    inputOffset += index;
                    if (needMore)
                    {
                        var leftover = inputCount - inputOffset;
                        if (leftover > 0 && inputOffset > 0)
                        {
                            Buffer.BlockCopy(input, inputOffset, input, 0, leftover);
                        }

                        inputOffset = 0;
                        inputCount = leftover;
                        var read = await _transport
                            .ReadAsync(input.AsMemory(inputCount, input.Length - inputCount), cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            throw new EndOfStreamException("NNTP TAKETHIS ended before multiline terminator.");
                        }

                        inputCount += read;
                    }
                }
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(rented);
                ArrayPool<byte>.Shared.Return(input);
                throw;
            }
        }

        private void PreserveLeftover(byte[] input, int offset, int count)
        {
            if (count <= 0)
            {
                return;
            }

            // Pipelined TAKETHIS may leave the next command plus article bytes after the terminator.
            // Grow beyond the 512-octet command-line limit so residue is not discarded.
            if (count > _pending.Length)
            {
                var grown = GC.AllocateUninitializedArray<byte>(Math.Max(count, _pending.Length * 2));
                _pending.AsSpan(0, _pendingCount).CopyTo(grown);
                _pending = grown;
            }

            input.AsSpan(offset, count).CopyTo(_pending);
            _pendingCount = count;
        }

        private async Task<CommandRead> ReadCommandAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var span = _pending.AsSpan(0, _pendingCount);
                var crlf = IndexOfCrlf(span);
                if (crlf >= 0)
                {
                    // RFC 3977: command line including CRLF must not exceed 512 octets.
                    if (crlf + 2 > MaximumCommandOctets)
                    {
                        var consumedOverlong = crlf + 2;
                        if (consumedOverlong < _pendingCount)
                        {
                            Buffer.BlockCopy(_pending, consumedOverlong, _pending, 0, _pendingCount - consumedOverlong);
                        }

                        _pendingCount -= consumedOverlong;
                        return new CommandRead(CommandReadKind.Overlong, ReadOnlyMemory<byte>.Empty);
                    }

                    // Copy the command line before shifting residue. Line must not alias _pending:
                    // leftover article bytes after CRLF are moved to offset 0 and would overwrite it.
                    var line = _pending.AsSpan(0, crlf).ToArray();
                    var consumed = crlf + 2;
                    if (consumed < _pendingCount)
                    {
                        Buffer.BlockCopy(_pending, consumed, _pending, 0, _pendingCount - consumed);
                    }

                    _pendingCount -= consumed;
                    return new CommandRead(CommandReadKind.Complete, line);
                }

                // No CRLF yet: overlong only when the first MaximumCommandOctets lack a terminator.
                if (_pendingCount >= MaximumCommandOctets)
                {
                    // Keep bytes after the first MaximumCommandOctets only once DrainUntilCrlf finishes.
                    _pendingCount = 0;
                    await DrainUntilCrlfAsync(cancellationToken).ConfigureAwait(false);
                    return new CommandRead(CommandReadKind.Overlong, ReadOnlyMemory<byte>.Empty);
                }

                EnsurePendingCapacity(_pendingCount + 1);
                var read = await _transport
                    .ReadAsync(_pending.AsMemory(_pendingCount, _pending.Length - _pendingCount), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return new CommandRead(CommandReadKind.Disconnected, ReadOnlyMemory<byte>.Empty);
                }

                _pendingCount += read;
            }
        }

        private void EnsurePendingCapacity(int minimumLength)
        {
            if (minimumLength <= _pending.Length)
            {
                return;
            }

            var grown = GC.AllocateUninitializedArray<byte>(Math.Max(minimumLength, _pending.Length * 2));
            _pending.AsSpan(0, _pendingCount).CopyTo(grown);
            _pending = grown;
        }

        private async Task DrainUntilCrlfAsync(CancellationToken cancellationToken)
        {
            var seenCr = false;
            while (true)
            {
                var read = await _transport.ReadAsync(_readScratch, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                for (var i = 0; i < read; i++)
                {
                    var b = _readScratch[i];
                    if (seenCr && b == (byte)'\n')
                    {
                        var remaining = read - i - 1;
                        if (remaining > 0)
                        {
                            EnsurePendingCapacity(remaining);
                            _readScratch.AsSpan(i + 1, remaining).CopyTo(_pending);
                            _pendingCount = remaining;
                        }

                        return;
                    }

                    seenCr = b == (byte)'\r';
                }
            }
        }

        private async Task WriteStatusAsync(
            byte hundreds,
            byte tens,
            byte ones,
            byte[] messageId,
            CancellationToken cancellationToken)
        {
            var length = 4 + messageId.Length + 2;
            _statusScratch[0] = hundreds;
            _statusScratch[1] = tens;
            _statusScratch[2] = ones;
            _statusScratch[3] = (byte)' ';
            messageId.AsSpan().CopyTo(_statusScratch.AsSpan(4));
            _statusScratch[4 + messageId.Length] = (byte)'\r';
            _statusScratch[5 + messageId.Length] = (byte)'\n';
            await WriteFullyAsync(_statusScratch.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }

        private async Task WriteFullyAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            var remaining = buffer;
            while (!remaining.IsEmpty)
            {
                var written = await _transport.WriteAsync(remaining, cancellationToken).ConfigureAwait(false);
                if (written <= 0)
                {
                    throw new IOException("Storage NNTP transport write returned no progress.");
                }

                remaining = remaining[written..];
            }
        }

        private static bool TryParseMessageIdArgument(ReadOnlySpan<byte> argument, out ReadOnlySpan<byte> messageId)
        {
            argument = SkipSpaces(argument);
            if (argument.IsEmpty)
            {
                messageId = default;
                return false;
            }

            var end = 0;
            while (end < argument.Length && argument[end] is not ((byte)' ' or (byte)'\t'))
            {
                end++;
            }

            if (end < argument.Length)
            {
                var rest = SkipSpaces(argument[end..]);
                if (!rest.IsEmpty)
                {
                    messageId = default;
                    return false;
                }
            }

            messageId = argument[..end];
            return NntpMessageIdValidation.IsValidMessageId(messageId);
        }

        private static bool TryMatchModeStream(ReadOnlySpan<byte> line, out bool syntaxError)
        {
            syntaxError = false;
            if (!TryMatchKeyword(line, ModeKeyword, out var modeRest))
            {
                return false;
            }

            if (!TryMatchKeyword(SkipSpaces(modeRest), StreamKeyword, out var streamRest))
            {
                return false;
            }

            syntaxError = !streamRest.IsEmpty;
            return true;
        }

        private static bool TryMatchKeyword(
            ReadOnlySpan<byte> line,
            ReadOnlySpan<byte> keyword,
            out ReadOnlySpan<byte> rest)
        {
            if (line.Length < keyword.Length)
            {
                rest = default;
                return false;
            }

            for (var i = 0; i < keyword.Length; i++)
            {
                if (ToAsciiUpper(line[i]) != keyword[i])
                {
                    rest = default;
                    return false;
                }
            }

            if (line.Length == keyword.Length)
            {
                rest = ReadOnlySpan<byte>.Empty;
                return true;
            }

            var next = line[keyword.Length];
            if (next is not ((byte)' ' or (byte)'\t'))
            {
                rest = default;
                return false;
            }

            rest = line[(keyword.Length + 1)..];
            return true;
        }

        private static ReadOnlySpan<byte> SkipSpaces(ReadOnlySpan<byte> value)
        {
            var i = 0;
            while (i < value.Length && value[i] is (byte)' ' or (byte)'\t')
            {
                i++;
            }

            return value[i..];
        }

        private static int IndexOfCrlf(ReadOnlySpan<byte> span)
        {
            for (var i = 0; i < span.Length - 1; i++)
            {
                if (span[i] == (byte)'\r' && span[i + 1] == (byte)'\n')
                {
                    return i;
                }
            }

            return -1;
        }

        private static byte ToAsciiUpper(byte value) =>
            value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 32) : value;

        private enum CommandReadKind
        {
            Complete = 0,
            Overlong = 1,
            Disconnected = 2,
        }

        private readonly struct CommandRead
        {
            public CommandRead(CommandReadKind kind, ReadOnlyMemory<byte> line)
            {
                Kind = kind;
                Line = line;
            }

            public CommandReadKind Kind { get; }
            public ReadOnlyMemory<byte> Line { get; }
        }
    }

    /// <summary>Source-generated StorageServer NNTP feed session logs.</summary>
    internal static partial class StorageNntpFeedSessionLogMessages
    {
        [LoggerMessage(
            EventId = 5450,
            Level = LogLevel.Debug,
            Message = "Storage NNTP TAKETHIS accepted artId={ArtId}")]
        public static partial void ArticleAccepted(ILogger logger, string ArtId);
    }
}
