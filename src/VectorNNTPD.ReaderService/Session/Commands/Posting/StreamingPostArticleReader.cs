using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VectorNNTPD.ReaderService.Moderation;
using VectorNNTPD.ReaderService.Session.Framing;

namespace VectorNNTPD.ReaderService.Session.Commands.Posting
{
    /// <summary>
    /// Streams a POST multiline article from a <see cref="PipeReader"/> into one destuffed
    /// output buffer for <see cref="VectorNNTPD.ReaderService.ArticleIngestion.ArticleRecordIngress"/>.
    /// </summary>
    /// <remarks>
    /// Header lines are destuffed for validation and written destuffed (client Path /
    /// injection metadata discarded). Body lines drop one NNTP stuffing layer
    /// (RFC 3977 §3.1.1). Destuffed byte counting enforces <c>Nntpd:MaxArticleSize</c>.
    /// History Peek is not performed here; the caller peeks after the terminator.
    /// </remarks>
    internal static class StreamingPostArticleReader
    {
        /// <summary>Initial capacity of the single destuffed output buffer.</summary>
        public const int InitialCapacity = 64 * 1024;

        /// <summary>
        /// Reads one POST article from <paramref name="reader"/> into one owned destuffed buffer.
        /// </summary>
        public static async ValueTask<StreamingPostReadResult> ReadAsync(
            PipeReader reader,
            StreamingPostReadOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(reader);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxArticleSize, 1);
            ArgumentNullException.ThrowIfNull(options.Time);
            ArgumentNullException.ThrowIfNull(options.NewsgroupPolicy);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.InjectionIdentity);
            ArgumentNullException.ThrowIfNull(options.ClientIdentity);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.MailComplaintsTo);

            var session = new ReadSession(options);
            return await session.RunAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        private sealed class ReadSession(StreamingPostReadOptions options)
        {
            private readonly GrowingArticleBuffer _output = new(InitialCapacity);
            private readonly List<ParsedPostHeader> _headers = [];
            private readonly ArrayBufferWriter<byte> _unfolded = new(256);
            private bool _inHeaders = true;
            private bool _hasField;
            private bool _writeCurrentField;
            private bool _drain;
            private byte[]? _currentName;
            private int _destuffedBytes;
            private int _headerBlockBytes;
            private StreamingPostReadStatus _status = StreamingPostReadStatus.Completed;
            private PostingFailure _failure;
            private ParsedPostArticle? _parsed;
            private DateTimeOffset _injectionUtc;
            private StreamingPostDisposition _disposition = StreamingPostDisposition.Inject;
            private string? _moderatorAddress;
            private string? _targetModeratedGroup;

            public async ValueTask<StreamingPostReadResult> RunAsync(
                PipeReader reader,
                CancellationToken cancellationToken)
            {
                while (true)
                {
                    var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    var buffer = result.Buffer;
                    var examined = buffer.End;

                    while (NntpDelimiterSearch.TryReadLine(ref buffer, out var line))
                    {
                        var finished = HandleLine(line);
                        if (finished)
                        {
                            reader.AdvanceTo(buffer.Start, buffer.Start);
                            return Finish();
                        }
                    }

                    reader.AdvanceTo(buffer.Start, examined);
                    if (result.IsCompleted)
                    {
                        return Incomplete();
                    }
                }
            }

            private bool HandleLine(ReadOnlySequence<byte> stuffedLine)
            {
                if (NntpDelimiterSearch.IsTerminatorLine(stuffedLine))
                {
                    return HandleTerminator();
                }

                if (_drain)
                {
                    return false;
                }

                var leadingDot = NntpDelimiterSearch.FirstByte(stuffedLine) == (byte)'.';
                var destuffedLength = (int)stuffedLine.Length - (leadingDot ? 1 : 0);
                var destuffedWithCrlf = destuffedLength + 2;
                if (_destuffedBytes + destuffedWithCrlf > options.MaxArticleSize)
                {
                    MarkTooLarge();
                    return false;
                }

                if (_inHeaders)
                {
                    return HandleHeaderLine(stuffedLine, destuffedLength, destuffedWithCrlf);
                }

                _destuffedBytes += destuffedWithCrlf;
                WriteDestuffedLine(_output, stuffedLine, destuffedLength);
                return false;
            }

            private bool HandleHeaderLine(
                ReadOnlySequence<byte> stuffedLine,
                int destuffedLength,
                int destuffedWithCrlf)
            {
                var rented = ArrayPool<byte>.Shared.Rent(Math.Max(destuffedLength, 1));
                try
                {
                    CopyDestuffed(stuffedLine, rented.AsSpan(0, destuffedLength));
                    var destuffed = rented.AsSpan(0, destuffedLength);
                    if (!TryValidateLineOctets(destuffed, out _failure))
                    {
                        MarkRejected();
                        return false;
                    }

                    if (destuffed.IsEmpty)
                    {
                        return CompleteHeaderSection(destuffedWithCrlf);
                    }

                    if (_headerBlockBytes + destuffedWithCrlf > PostingLimits.MaxHeaderBlockBytes)
                    {
                        _failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "header block too large");
                        MarkRejected();
                        return false;
                    }

                    _destuffedBytes += destuffedWithCrlf;
                    _headerBlockBytes += destuffedWithCrlf;

                    var isFold = destuffed.Length > 0 && PostFieldSyntax.IsWsp(destuffed[0]);
                    if (isFold)
                    {
                        return HandleFold(destuffed);
                    }

                    if (_hasField && !FlushCurrentField())
                    {
                        MarkRejected();
                        return false;
                    }

                    if (!PostHeaderParser.TryReadFieldName(destuffed, out var nameLength, out _failure))
                    {
                        MarkRejected();
                        return false;
                    }

                    _currentName = destuffed[..nameLength].ToArray();
                    _writeCurrentField = !PostHeaderNormalizer.IsServerOwned(_currentName);
                    _hasField = true;
                    _unfolded.Clear();
                    _unfolded.Write(destuffed[(nameLength + 2)..]);
                    if (_writeCurrentField)
                    {
                        _output.Write(destuffed);
                        _output.Write(NntpDelimiterSearch.Crlf);
                    }

                    return false;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }

            private bool HandleFold(ReadOnlySpan<byte> destuffed)
            {
                if (!_hasField)
                {
                    _failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "leading fold");
                    MarkRejected();
                    return false;
                }

                if (destuffed.Length < 2)
                {
                    _failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "empty fold");
                    MarkRejected();
                    return false;
                }

                _unfolded.Write(destuffed);
                if (_writeCurrentField)
                {
                    _output.Write(destuffed);
                    _output.Write(NntpDelimiterSearch.Crlf);
                }

                return false;
            }

            private bool CompleteHeaderSection(int separatorBytes)
            {
                if (_headerBlockBytes + separatorBytes > PostingLimits.MaxHeaderBlockBytes)
                {
                    _failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "header block too large");
                    MarkRejected();
                    return false;
                }

                if (!_hasField)
                {
                    _failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "empty header block");
                    MarkRejected();
                    return false;
                }

                if (!FlushCurrentField())
                {
                    MarkRejected();
                    return false;
                }

                if (_headers.Count > PostingLimits.MaxHeaderCount)
                {
                    _failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "too many headers");
                    MarkRejected();
                    return false;
                }

                _destuffedBytes += separatorBytes;
                _headerBlockBytes += separatorBytes;

                _injectionUtc = options.Time.GetUtcNow();
                _parsed = new ParsedPostArticle(
                    ReadOnlyMemory<byte>.Empty,
                    _headers,
                    ReadOnlyMemory<byte>.Empty,
                    _headerBlockBytes);

                if (!PostArticleValidator.TryValidate(
                        _parsed,
                        _injectionUtc,
                        options.NewsgroupPolicy,
                        out _failure,
                        options.ControlCancelPermitted))
                {
                    MarkRejected();
                    return false;
                }

                if (!TryCompletePostingDecision(_parsed))
                {
                    MarkRejected();
                    return false;
                }

                _inHeaders = false;
                return false;
            }

            private bool TryCompletePostingDecision(ParsedPostArticle article)
            {
                if (article.CatalogStatus == NewsgroupCatalogStatus.RequiresModeration)
                {
                    if (!article.ApprovedPresent)
                    {
                        return TryBeginModerationSubmission(article);
                    }

                    var authorization = options.ModeratorAuthorization ?? EmptyModeratorAuthorization.Instance;
                    if (!authorization.TryAuthorizeApproval(
                            options.AuthenticatedUsername,
                            article.ApprovedIdentities,
                            article.ModeratedGroups,
                            out var detail))
                    {
                        _failure = new PostingFailure(
                            detail == "unresolved moderator"
                                ? PostingFailureCategory.ModerationForwardingFailed
                                : PostingFailureCategory.UnauthorizedApproval,
                            detail ?? "unauthorized approval");
                        return false;
                    }
                }

                return TryWriteInjectionHeaders(article);
            }

            private bool TryBeginModerationSubmission(ParsedPostArticle article)
            {
                if (article.ModeratedGroups.Length == 0)
                {
                    _failure = new PostingFailure(PostingFailureCategory.PolicyRejected, "moderated newsgroup");
                    return false;
                }

                var target = article.ModeratedGroups[0];
                if (target.Length is < 1 or > 128)
                {
                    _failure = new PostingFailure(PostingFailureCategory.InvalidNewsgroups, "malformed newsgroup name");
                    return false;
                }

                Span<byte> nameBytes = stackalloc byte[128];
                var written = Encoding.ASCII.GetBytes(target, nameBytes);
                var authorization = options.ModeratorAuthorization ?? EmptyModeratorAuthorization.Instance;
                if (!authorization.TryResolve(nameBytes[..written], out var identity))
                {
                    _failure = new PostingFailure(
                        PostingFailureCategory.ModerationForwardingFailed,
                        "unresolved moderator");
                    return false;
                }

                try
                {
                    PostHeaderNormalizer.WriteProtoArticleBoundary(
                        _output,
                        article.MessageIdSynthesized,
                        article.MessageId!,
                        article.DateSynthesized,
                        _injectionUtc);
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception)
                {
                    _failure = new PostingFailure(PostingFailureCategory.PersistenceFailure, "proto-article header generation failed");
                    return false;
                }

                _disposition = StreamingPostDisposition.SubmitForModeration;
                _targetModeratedGroup = target;
                _moderatorAddress = identity.Address;
                return true;
            }

            private bool TryWriteInjectionHeaders(ParsedPostArticle article)
            {
                if (options.TraceProtector is null)
                {
                    _failure = new PostingFailure(PostingFailureCategory.PersistenceFailure, "trace protector unavailable");
                    return false;
                }

                try
                {
                    PostHeaderNormalizer.WriteServerOwnedHeaders(
                        _output,
                        article.MessageIdSynthesized,
                        article.MessageId!,
                        article.DateSynthesized,
                        _injectionUtc,
                        options.InjectionIdentity,
                        options.ClientIdentity,
                        options.MailComplaintsTo,
                        options.TraceProtector,
                        options.AuthenticatedUsername);
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception)
                {
                    _failure = new PostingFailure(PostingFailureCategory.PersistenceFailure, "server header generation failed");
                    return false;
                }

                _disposition = StreamingPostDisposition.Inject;
                return true;
            }

            private bool HandleTerminator()
            {
                if (_drain)
                {
                    return true;
                }

                if (_inHeaders)
                {
                    _failure = _destuffedBytes == 0
                        ? new PostingFailure(PostingFailureCategory.MalformedHeader, "empty article")
                        : new PostingFailure(PostingFailureCategory.MalformedHeader, "missing header/body separator");
                    _status = StreamingPostReadStatus.Rejected;
                    return true;
                }

                _status = StreamingPostReadStatus.Completed;
                return true;
            }

            private bool FlushCurrentField()
            {
                var name = _currentName is null
                    ? ReadOnlyMemory<byte>.Empty
                    : _currentName.AsMemory();
                var value = _unfolded.WrittenCount == 0
                    ? ReadOnlyMemory<byte>.Empty
                    : _unfolded.WrittenMemory.ToArray();
                if (!PostHeaderParser.TryCreateHeader(name, value, ReadOnlyMemory<byte>.Empty, out var header, out _failure))
                {
                    return false;
                }

                _headers.Add(header);
                _hasField = false;
                _currentName = null;
                _unfolded.Clear();
                return true;
            }

            private void MarkTooLarge()
            {
                _status = StreamingPostReadStatus.TooLarge;
                _failure = new PostingFailure(PostingFailureCategory.ArticleTooLarge, "max article size exceeded");
                _drain = true;
            }

            private void MarkRejected()
            {
                _status = StreamingPostReadStatus.Rejected;
                _drain = true;
            }

            private StreamingPostReadResult Finish()
            {
                if (_status != StreamingPostReadStatus.Completed)
                {
                    return new StreamingPostReadResult(
                        _status,
                        _failure,
                        ReadOnlyMemory<byte>.Empty,
                        _parsed?.MessageId,
                        _parsed?.Newsgroups ?? [],
                        _destuffedBytes,
                        _injectionUtc,
                        _disposition,
                        _moderatorAddress,
                        _targetModeratedGroup,
                        _parsed?.ApprovedIdentities);
                }

                return new StreamingPostReadResult(
                    StreamingPostReadStatus.Completed,
                    default,
                    _output.Take(),
                    _parsed!.MessageId,
                    _parsed.Newsgroups,
                    _destuffedBytes,
                    _injectionUtc,
                    _disposition,
                    _moderatorAddress,
                    _targetModeratedGroup,
                    _parsed.ApprovedIdentities);
            }

            private StreamingPostReadResult Incomplete() =>
                new(
                    StreamingPostReadStatus.Incomplete,
                    default,
                    ReadOnlyMemory<byte>.Empty,
                    _parsed?.MessageId,
                    _parsed?.Newsgroups ?? [],
                    _destuffedBytes,
                    _injectionUtc,
                    _disposition,
                    _moderatorAddress,
                    _targetModeratedGroup,
                    _parsed?.ApprovedIdentities);
        }

        private static bool TryValidateLineOctets(ReadOnlySpan<byte> destuffed, out PostingFailure failure)
        {
            for (var i = 0; i < destuffed.Length; i++)
            {
                var b = destuffed[i];
                if (b == 0)
                {
                    failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "embedded NUL");
                    return false;
                }

                if (b == (byte)'\n')
                {
                    failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "LF without CR");
                    return false;
                }

                if (b == (byte)'\r')
                {
                    failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "CR without LF");
                    return false;
                }
            }

            failure = default;
            return true;
        }

        private static void CopyDestuffed(ReadOnlySequence<byte> stuffed, Span<byte> destuffed)
        {
            if (destuffed.IsEmpty)
            {
                return;
            }

            var leadingDot = NntpDelimiterSearch.FirstByte(stuffed) == (byte)'.';
            if (stuffed.IsSingleSegment)
            {
                var src = stuffed.FirstSpan;
                if (leadingDot)
                {
                    src = src[1..];
                }

                src.CopyTo(destuffed);
                return;
            }

            var rented = ArrayPool<byte>.Shared.Rent((int)stuffed.Length);
            try
            {
                stuffed.CopyTo(rented);
                var src = leadingDot
                    ? rented.AsSpan(1, destuffed.Length)
                    : rented.AsSpan(0, destuffed.Length);
                src.CopyTo(destuffed);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        /// <summary>
        /// Writes one destuffed content line plus CRLF (one NNTP stuffing layer removed).
        /// </summary>
        private static void WriteDestuffedLine(
            GrowingArticleBuffer output,
            ReadOnlySequence<byte> stuffedLine,
            int destuffedLength)
        {
            if (destuffedLength > 0)
            {
                if (stuffedLine.IsSingleSegment)
                {
                    var src = stuffedLine.FirstSpan;
                    if (src.Length > 0 && src[0] == (byte)'.')
                    {
                        src = src[1..];
                    }

                    output.Write(src);
                }
                else
                {
                    var rented = ArrayPool<byte>.Shared.Rent(destuffedLength);
                    try
                    {
                        CopyDestuffed(stuffedLine, rented.AsSpan(0, destuffedLength));
                        output.Write(rented.AsSpan(0, destuffedLength));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }
            }

            output.Write(NntpDelimiterSearch.Crlf);
        }

        /// <summary>One owned destuffed output buffer transferred to the caller on success.</summary>
        internal sealed class GrowingArticleBuffer : IBufferWriter<byte>
        {
            private byte[] _buffer;
            private int _written;

            public GrowingArticleBuffer(int initialCapacity)
            {
                _buffer = new byte[initialCapacity];
            }

            public int Written => _written;

            public void Advance(int count)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(count);
                if (_written + count > _buffer.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(count));
                }

                _written += count;
            }

            public Memory<byte> GetMemory(int sizeHint = 0)
            {
                Ensure(_written + Math.Max(sizeHint, 1));
                return _buffer.AsMemory(_written);
            }

            public Span<byte> GetSpan(int sizeHint = 0)
            {
                Ensure(_written + Math.Max(sizeHint, 1));
                return _buffer.AsSpan(_written);
            }

            public void Write(ReadOnlySpan<byte> bytes)
            {
                if (bytes.IsEmpty)
                {
                    return;
                }

                Ensure(_written + bytes.Length);
                bytes.CopyTo(_buffer.AsSpan(_written));
                _written += bytes.Length;
            }

            public void Write(ReadOnlySequence<byte> bytes)
            {
                if (bytes.IsEmpty)
                {
                    return;
                }

                if (bytes.IsSingleSegment)
                {
                    Write(bytes.FirstSpan);
                    return;
                }

                foreach (var segment in bytes)
                {
                    Write(segment.Span);
                }
            }

            public ReadOnlyMemory<byte> Take()
            {
                var buffer = _buffer;
                var length = _written;
                _buffer = [];
                _written = 0;
                return buffer.AsMemory(0, length);
            }

            private void Ensure(int needed)
            {
                if (_buffer.Length >= needed)
                {
                    return;
                }

                var next = _buffer.Length < 256 ? 256 : _buffer.Length * 2;
                if (next < needed)
                {
                    next = needed;
                }

                var grown = new byte[next];
                if (_written > 0)
                {
                    _buffer.AsSpan(0, _written).CopyTo(grown);
                }

                _buffer = grown;
            }
        }
    }
}
