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
    /// Storage NNTP session over an established TLS transport.
    /// Accepts <c>ARTICLE</c>, <c>TAKETHIS</c>, and <c>QUIT</c> only. Other verbs receive <c>500</c>.
    /// A successful <c>TAKETHIS</c> commits into <see cref="IAcceptedArticleQueue"/> before <c>239</c> is written.
    /// <c>ARTICLE</c> serves indexed CanonicalV1 spool files immediately; no mode negotiation is required.
    /// </summary>
    public sealed partial class StorageNntpFeedSession : IAsyncDisposable
    {
        private const int MaximumCommandOctets = 512;

        /// <summary>
        /// Initial destuffed-assembly rental. Grows on demand up to
        /// <see cref="ArticleResourceLimits.MaxArticleBytes"/>. Avoids a 1 MiB pool rent for typical articles.
        /// </summary>
        private const int InitialAssemblyRentBytes = 64 * 1024;

        private static readonly byte[] Greeting = "200 VectorNNTP StorageServer ready\r\n"u8.ToArray();
        private static readonly byte[] Closing = "205 Connection closing\r\n"u8.ToArray();
        private static readonly byte[] SyntaxError = "501 Syntax error\r\n"u8.ToArray();
        private static readonly byte[] UnknownCommand = "500 Unknown command\r\n"u8.ToArray();
        private static readonly byte[] TemporaryFailure = "400 Temporary failure\r\n"u8.ToArray();
        private static readonly byte[] NoSuchArticle = "430 No Such Article Found\r\n"u8.ToArray();
        private static readonly byte[] ArticleNotAvailable = "403 Article not available\r\n"u8.ToArray();
        private static readonly byte[] ArticlePrefix = "220 0 "u8.ToArray();
        private static readonly byte[] ArticleFollowsSuffix = " article follows\r\n"u8.ToArray();
        private static readonly byte[] ArticleTerminator = ".\r\n"u8.ToArray();

        private static readonly byte[] TakeThisKeyword = "TAKETHIS"u8.ToArray();
        private static readonly byte[] ArticleKeyword = "ARTICLE"u8.ToArray();
        private static readonly byte[] QuitKeyword = "QUIT"u8.ToArray();

        private readonly IStorageListenerTransport _transport;
        private readonly StorageServerListenerRuntimeOptions _listener;
        private readonly ILogger _logger;
        private readonly IAcceptedArticleQueue? _acceptedArticles;
        private readonly StoreAssemblyAdmission? _assemblyAdmission;
        private readonly Func<bool>? _allowsArticleAdmission;
        private readonly Func<ArticleId, bool>? _articlePresent;
        private readonly Func<ArticleId, string?>? _resolveRetrievableArticle;
        private readonly Func<ArticleId, CancellationToken, ValueTask>? _afterArticleDelivered;
        private readonly Func<ArticleId, IDisposable>? _acquireArticleReadLease;
        private readonly NntpArticleParser _parser;
        private byte[] _pending;
        private readonly byte[] _readScratch;
        private readonly byte[] _statusScratch;
        private readonly byte[] _articleFollows;
        private int _pendingCount;
        private int _disposed;

        /// <summary>Initializes a Storage NNTP session over an established transport.</summary>
        public StorageNntpFeedSession(
            IStorageListenerTransport transport,
            StorageServerListenerRuntimeOptions listener,
            ILogger logger,
            string pathIdentity,
            IAcceptedArticleQueue? acceptedArticles = null,
            StoreAssemblyAdmission? assemblyAdmission = null,
            Func<bool>? allowsArticleAdmission = null,
            Func<ArticleId, bool>? articlePresent = null,
            Func<ArticleId, string?>? resolveRetrievableArticle = null,
            Func<ArticleId, CancellationToken, ValueTask>? afterArticleDelivered = null,
            Func<ArticleId, IDisposable>? acquireArticleReadLease = null)
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
            _allowsArticleAdmission = allowsArticleAdmission;
            _articlePresent = articlePresent;
            _resolveRetrievableArticle = resolveRetrievableArticle;
            _afterArticleDelivered = afterArticleDelivered;
            _acquireArticleReadLease = acquireArticleReadLease;
            _parser = new NntpArticleParser(pathIdentity.Trim());
            var capacity = Math.Max(listener.ParserAccumulationMaxBytes, MaximumCommandOctets);
            _pending = new byte[MaximumCommandOctets];
            _readScratch = new byte[Math.Min(capacity, 64 * 1024)];
            _statusScratch = new byte[4 + 1 + NntpMessageIdValidation.MaxMessageIdLength + 2];
            _articleFollows = new byte[
                ArticlePrefix.Length + NntpMessageIdValidation.MaxMessageIdLength + ArticleFollowsSuffix.Length];
        }

        /// <summary>
        /// Sends the greeting, then reads commands until <c>QUIT</c>, peer close, or cancellation.
        /// </summary>
        /// <returns>
        /// <see cref="StorageNntpFeedSessionEndReason.PeerDisconnected"/> when a command read observes
        /// EOF; otherwise <see cref="StorageNntpFeedSessionEndReason.Closed"/>.
        /// </returns>
        public async Task<StorageNntpFeedSessionEndReason> RunAsync(CancellationToken cancellationToken)
        {
            await WriteFullyAsync(Greeting, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                var command = await ReadCommandAsync(cancellationToken).ConfigureAwait(false);
                if (command.Kind == CommandReadKind.Disconnected)
                {
                    return StorageNntpFeedSessionEndReason.PeerDisconnected;
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
                    return StorageNntpFeedSessionEndReason.Closed;
                }

                if (TryMatchKeyword(line, TakeThisKeyword, out var takeRest))
                {
                    // One heap copy of the message-id argument; spans cannot cross the TAKETHIS awaits.
                    var takeArg = SkipSpaces(takeRest).ToArray();
                    var keepOpen = await HandleTakeThisAsync(takeArg, cancellationToken).ConfigureAwait(false);
                    if (!keepOpen)
                    {
                        return StorageNntpFeedSessionEndReason.Closed;
                    }

                    continue;
                }

                if (TryMatchKeyword(line, ArticleKeyword, out var articleRest))
                {
                    // Message-ID octets must outlive the awaits used while reading the spool file.
                    var articleArg = SkipSpaces(articleRest).ToArray();
                    await HandleArticleAsync(articleArg, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await WriteFullyAsync(UnknownCommand, cancellationToken).ConfigureAwait(false);
            }

            return StorageNntpFeedSessionEndReason.Closed;
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

        /// <summary>
        /// Serves <c>ARTICLE &lt;message-id&gt;</c> from the indexed final CanonicalV1 spool file.
        /// The published file is read and framed fully before any <c>220</c> is written, so a read
        /// failure never injects a second status line into a partial article response. A write failure
        /// after <c>220</c> propagates and ends the session without emitting another NNTP status.
        /// </summary>
        private async Task HandleArticleAsync(byte[] argument, CancellationToken cancellationToken)
        {
            if (!TryParseMessageIdArgument(argument, out var messageIdSpan))
            {
                await WriteFullyAsync(SyntaxError, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Heap copy: ReadOnlySpan cannot live across the spool-read awaits below.
            var messageId = messageIdSpan.ToArray();
            var articleId = ArticleId.FromMessageId(messageId);
            // Lease before path resolve so eviction cannot delete between lookup and open.
            using var readLease = _acquireArticleReadLease?.Invoke(articleId);
            string? articlePath;
            try
            {
                articlePath = _resolveRetrievableArticle?.Invoke(articleId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                await WriteFullyAsync(NoSuchArticle, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrEmpty(articlePath))
            {
                await WriteFullyAsync(NoSuchArticle, cancellationToken).ConfigureAwait(false);
                return;
            }

            byte[] artData;
            try
            {
                artData = await ReadPublishedArticleAsync(articlePath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FileNotFoundException)
            {
                await WriteFullyAsync(NoSuchArticle, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                await WriteFullyAsync(NoSuchArticle, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
            {
                StorageNntpFeedSessionLogMessages.ArticleRetrievalFailed(
                    _logger,
                    articleId.ToLowerHexString(),
                    ex.GetType().Name);
                await WriteFullyAsync(TemporaryFailure, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (artData.Length == 0 || artData.Length > ArticleResourceLimits.MaxArticleBytes)
            {
                await WriteFullyAsync(ArticleNotAvailable, cancellationToken).ConfigureAwait(false);
                return;
            }

            var framing = CanonicalArticleFraming.Analyze(artData);
            if (!framing.IsFrameable)
            {
                await WriteFullyAsync(ArticleNotAvailable, cancellationToken).ConfigureAwait(false);
                return;
            }

            byte[]? stuffedHeader = null;
            var splitBody = framing.BodyStart >= 0
                && CanonicalArticleDeliveryFraming.TryCreateStuffedHeader(
                    artData,
                    framing.BodyStart,
                    out stuffedHeader);

            // Response phase: do not catch write failures — a mid-response I/O error must not emit 4xx into the body.
            await WriteFullyAsync(BuildArticleFollowsLine(messageId), cancellationToken).ConfigureAwait(false);
            if (splitBody)
            {
                await WriteFullyAsync(stuffedHeader, cancellationToken).ConfigureAwait(false);
                await WriteFullyAsync(artData.AsMemory(framing.BodyStart), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteFullyAsync(artData, cancellationToken).ConfigureAwait(false);
            }

            await WriteFullyAsync(ArticleTerminator, cancellationToken).ConfigureAwait(false);
            await NotifyArticleDeliveredBestEffortAsync(articleId, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads only the published final path. Temporary <c>.writing-*</c> names are never opened.
        /// </summary>
        private static async Task<byte[]> ReadPublishedArticleAsync(
            string articlePath,
            CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(
                articlePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (stream.Length <= 0 || stream.Length > ArticleResourceLimits.MaxArticleBytes)
            {
                return [];
            }

            var length = (int)stream.Length;
            var buffer = GC.AllocateUninitializedArray<byte>(length);
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                offset += read;
            }

            return buffer;
        }

        /// <summary>
        /// Best-effort post-delivery hook (typically last-access touch). Failures never change the already-written <c>220</c>.
        /// </summary>
        private async Task NotifyArticleDeliveredBestEffortAsync(
            ArticleId articleId,
            CancellationToken cancellationToken)
        {
            var notify = _afterArticleDelivered;
            if (notify is null)
            {
                return;
            }

            try
            {
                await notify(articleId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                StorageNntpFeedSessionLogMessages.ArticleTouchFailed(
                    _logger,
                    articleId.ToLowerHexString(),
                    ex.GetType().Name);
            }
        }

        /// <summary>Writes <c>220 0 &lt;message-id&gt; article follows</c> ending in CRLF into <see cref="_articleFollows"/>.</summary>
        private ReadOnlyMemory<byte> BuildArticleFollowsLine(ReadOnlySpan<byte> messageId)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(messageId.Length, NntpMessageIdValidation.MaxMessageIdLength);
            var length = ArticlePrefix.Length + messageId.Length + ArticleFollowsSuffix.Length;
            ArticlePrefix.CopyTo(_articleFollows);
            messageId.CopyTo(_articleFollows.AsSpan(ArticlePrefix.Length));
            ArticleFollowsSuffix.CopyTo(_articleFollows.AsSpan(ArticlePrefix.Length + messageId.Length));
            return _articleFollows.AsMemory(0, length);
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

            // argument holds the message-id octets for the rest of this method (copied once in RunAsync).
            var requestId = ArticleId.FromMessageId(requestMessageId);

            // Critical / unknown capacity: reject before assembly or queue admission (no 239).
            if (_allowsArticleAdmission is not null && !_allowsArticleAdmission())
            {
                await DrainArticleAsync(cancellationToken).ConfigureAwait(false);
                await WriteStatusAsync(
                        (byte)'4',
                        (byte)'3',
                        (byte)'9',
                        argument,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            StoreAssemblyAdmission? admission = null;
            var gate = _assemblyAdmission;
            if (gate is not null)
            {
                if (!gate.TryEnter())
                {
                    // No assembly permit: discard without renting a growing article buffer.
                    await DrainArticleAsync(cancellationToken).ConfigureAwait(false);
                    await WriteStatusAsync(
                            (byte)'4',
                            (byte)'3',
                            (byte)'9',
                            argument,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

                admission = gate;
            }

            byte[]? rented = null;
            QueueReservation? reservation = null;
            // Canonical buffer owned until queue commit transfers ArtData; release on any pre-commit exit.
            ArticleRecord? assembled = null;
            try
            {
                var destuffed = await ReadDestuffedArticleAsync(cancellationToken).ConfigureAwait(false);
                rented = destuffed.Array;
                var payload = destuffed.Memory;

                if (_articlePresent?.Invoke(requestId) == true)
                {
                    await WriteStatusAsync((byte)'4', (byte)'3', (byte)'9', argument, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

                var created = ArticleRecordFactory.TryCreate(
                    _parser,
                    payload,
                    ArticlePathMode.Normalize);

                // Destuffed wire buffer is no longer required; free it before any admission wait so
                // blocked queue reservation cannot retain both wire and canonical copies.
                ReturnAssemblyBuffer(ref rented);

                if (!created.IsAccepted)
                {
                    await WriteStatusAsync((byte)'4', (byte)'3', (byte)'9', argument, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

                assembled = created.Record;
                if (!assembled.Value.ArtId.Equals(requestId))
                {
                    await WriteStatusAsync((byte)'4', (byte)'3', (byte)'9', argument, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

                var queue = _acceptedArticles;
                if (queue is null || queue.IsCompleted)
                {
                    await WriteFullyAsync(TemporaryFailure, cancellationToken).ConfigureAwait(false);
                    return false;
                }

                // Re-check after assembly so a critical sample published during the read cannot slip a 239.
                if (_allowsArticleAdmission is not null && !_allowsArticleAdmission())
                {
                    await WriteStatusAsync((byte)'4', (byte)'3', (byte)'9', argument, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

                var record = assembled.Value;
                if (record.ArtSize > queue.MaxQueuedBytes)
                {
                    await WriteStatusAsync((byte)'4', (byte)'3', (byte)'9', argument, cancellationToken)
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
                        await WriteStatusAsync((byte)'4', (byte)'3', (byte)'9', argument, cancellationToken)
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
                    throw;
                }

                reservation = null;
                if (!committed)
                {
                    await WriteFullyAsync(TemporaryFailure, cancellationToken).ConfigureAwait(false);
                    return false;
                }

                // Queue owns ArtData; do not release the canonical creator on this path.
                assembled = null;

                // ArtData remains reachable via the queued Memory; Exact buffers are not pooled.
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    StorageNntpFeedSessionLogMessages.ArticleAccepted(
                        _logger,
                        record.ArtId.ToLowerHexString());
                }

                await WriteStatusAsync((byte)'2', (byte)'3', (byte)'9', argument, cancellationToken)
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
            catch (OperationCanceledException)
            {
                if (reservation is { } held)
                {
                    _acceptedArticles?.Release(held);
                }

                throw;
            }
            catch (TimeoutException)
            {
                if (reservation is { } held)
                {
                    _acceptedArticles?.Release(held);
                }

                await WriteFullyAsync(TemporaryFailure, cancellationToken).ConfigureAwait(false);
                return false;
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
                if (assembled is { } leftover)
                {
                    leftover.ReleaseUnusedCanonicalBuffer();
                }

                ReturnAssemblyBuffer(ref rented);
                admission?.Exit();
            }
        }

        /// <summary>
        /// Discards a framed TAKETHIS body without renting a growing assembly buffer.
        /// Used when no <see cref="StoreAssemblyAdmission"/> permit is held. Still enforces
        /// <see cref="ArticleResourceLimits.MaxArticleBytes"/> and the overall receive deadline.
        /// </summary>
        private async Task DrainArticleAsync(CancellationToken cancellationToken)
        {
            try
            {
                await DiscardFramedArticleAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidOperationException or TimeoutException)
            {
                // Peer closed, oversized body, or receive deadline — stop draining; caller decides response.
                ExpectedException.Ignore(ex);
            }
        }

        private async Task DiscardFramedArticleAsync(CancellationToken cancellationToken)
        {
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            overall.CancelAfter(_listener.ArticleReceiveTimeout);
            try
            {
                await DiscardFramedCoreAsync(ArticleResourceLimits.MaxArticleBytes, overall.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                && overall.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"NNTP TAKETHIS article receive exceeded overall timeout of {_listener.ArticleReceiveTimeout}.");
            }
        }

        /// <summary>
        /// Reads through the multiline terminator while counting destuffed bytes only.
        /// Does not retain article payload beyond the fixed session read scratch.
        /// </summary>
        private async Task DiscardFramedCoreAsync(int maxBytes, CancellationToken cancellationToken)
        {
            var written = 0L;
            var atLineStart = true;
            byte[]? rentedInput = null;
            byte[] input;
            if (_pendingCount > _readScratch.Length)
            {
                rentedInput = ArrayPool<byte>.Shared.Rent(_pendingCount);
                input = rentedInput;
            }
            else
            {
                input = _readScratch;
            }

            var inputCount = 0;
            var inputOffset = 0;
            try
            {
                if (_pendingCount > 0)
                {
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
                                return;
                            }

                            if (next == (byte)'\r' && span[index + 2] == (byte)'\n')
                            {
                                inputOffset += index + 3;
                                PreserveLeftover(input, inputOffset, inputCount - inputOffset);
                                return;
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

                        var sliceLength = index - start;
                        if (written + sliceLength > maxBytes)
                        {
                            throw new InvalidOperationException("NNTP TAKETHIS article exceeds maximum size.");
                        }

                        written += sliceLength;
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
            finally
            {
                ReturnRentedInput(rentedInput);
            }
        }

        private async Task<(byte[]? Array, ReadOnlyMemory<byte> Memory)> ReadDestuffedArticleAsync(
            CancellationToken cancellationToken)
        {
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            overall.CancelAfter(_listener.ArticleReceiveTimeout);
            try
            {
                return await ReadDestuffedCoreAsync(ArticleResourceLimits.MaxArticleBytes, overall.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                && overall.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"NNTP TAKETHIS article receive exceeded overall timeout of {_listener.ArticleReceiveTimeout}.");
            }
        }

        private static void ReturnAssemblyBuffer(ref byte[]? rented)
        {
            if (rented is null)
            {
                return;
            }

            ArrayPool<byte>.Shared.Return(rented);
            rented = null;
        }

        private async Task<(byte[]? Array, ReadOnlyMemory<byte> Memory)> ReadDestuffedCoreAsync(
            int maxBytes,
            CancellationToken cancellationToken)
        {
            var rented = ArrayPool<byte>.Shared.Rent(Math.Min(maxBytes, InitialAssemblyRentBytes));
            var written = 0;
            var atLineStart = true;
            // Prefer the session scratch buffer so typical articles do not rent a second read array.
            byte[]? rentedInput = null;
            byte[] input;
            if (_pendingCount > _readScratch.Length)
            {
                rentedInput = ArrayPool<byte>.Shared.Rent(_pendingCount);
                input = rentedInput;
            }
            else
            {
                input = _readScratch;
            }

            var inputCount = 0;
            var inputOffset = 0;
            try
            {
                // Seed from any leftover command-buffer bytes that follow the TAKETHIS line.
                if (_pendingCount > 0)
                {
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
                                ReturnRentedInput(rentedInput);
                                return (rented, rented.AsMemory(0, written));
                            }

                            if (next == (byte)'\r' && span[index + 2] == (byte)'\n')
                            {
                                inputOffset += index + 3;
                                PreserveLeftover(input, inputOffset, inputCount - inputOffset);
                                ReturnRentedInput(rentedInput);
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
                        if ((long)written + slice.Length > maxBytes)
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
                ReturnRentedInput(rentedInput);
                throw;
            }
        }

        private static void ReturnRentedInput(byte[]? rentedInput)
        {
            if (rentedInput is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedInput);
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

                    var consumed = crlf + 2;
                    ReadOnlyMemory<byte> line;
                    if (consumed < _pendingCount)
                    {
                        // Residue follows the CRLF (pipelined article bytes). Copy the command line
                        // before BlockCopy shifts that residue over offset 0.
                        line = _pending.AsSpan(0, crlf).ToArray();
                        Buffer.BlockCopy(_pending, consumed, _pending, 0, _pendingCount - consumed);
                        _pendingCount -= consumed;
                    }
                    else
                    {
                        // No residue: caller finishes using Line before the next read or PreserveLeftover.
                        line = _pending.AsMemory(0, crlf);
                        _pendingCount = 0;
                    }

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

        private Task WriteStatusAsync(
            byte hundreds,
            byte tens,
            byte ones,
            byte[] messageIdArgument,
            CancellationToken cancellationToken)
        {
            if (!TryParseMessageIdArgument(messageIdArgument, out var messageId))
            {
                return WriteFullyAsync(SyntaxError, cancellationToken);
            }

            return WriteStatusAsync(hundreds, tens, ones, messageId, cancellationToken);
        }

        private Task WriteStatusAsync(
            byte hundreds,
            byte tens,
            byte ones,
            ReadOnlySpan<byte> messageId,
            CancellationToken cancellationToken)
        {
            var length = 4 + messageId.Length + 2;
            if ((uint)length > (uint)_statusScratch.Length)
            {
                return WriteFullyAsync(SyntaxError, cancellationToken);
            }

            _statusScratch[0] = hundreds;
            _statusScratch[1] = tens;
            _statusScratch[2] = ones;
            _statusScratch[3] = (byte)' ';
            messageId.CopyTo(_statusScratch.AsSpan(4));
            _statusScratch[4 + messageId.Length] = (byte)'\r';
            _statusScratch[5 + messageId.Length] = (byte)'\n';
            return WriteFullyAsync(_statusScratch.AsMemory(0, length), cancellationToken);
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
