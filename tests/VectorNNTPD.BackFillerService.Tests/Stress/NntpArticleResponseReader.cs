namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Classifies and consumes one BackFiller listener <c>ARTICLE</c> response.
    /// </summary>
    /// <remarks>
    /// Payload accounting excludes the status line and the terminating <c>.CRLF</c>.
    /// Logical payload bytes are the multiline article image with leading stuffing dots removed.
    /// Wire payload bytes are the article image octets as received before destuffing.
    /// </remarks>
    public static class NntpArticleResponseReader
    {
        /// <summary>
        /// Reads one status line (through CRLF) from <paramref name="stream"/> into <paramref name="lineBuffer"/>.
        /// </summary>
        public static async Task<NntpStatusLine> ReadStatusLineAsync(
            Stream stream,
            byte[] lineBuffer,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(lineBuffer);
            var length = 0;
            while (true)
            {
                if (length >= lineBuffer.Length)
                {
                    return new NntpStatusLine(NntpResponseKind.ProtocolError, 0, "status line exceeded buffer");
                }

                var read = await stream.ReadAsync(lineBuffer.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return new NntpStatusLine(NntpResponseKind.Disconnect, 0, "peer closed before status line");
                }

                length += read;
                if (length >= 2
                    && lineBuffer[length - 2] == (byte)'\r'
                    && lineBuffer[length - 1] == (byte)'\n')
                {
                    break;
                }
            }

            var contentLength = length - 2;
            if (contentLength < 3
                || !IsDigit(lineBuffer[0])
                || !IsDigit(lineBuffer[1])
                || !IsDigit(lineBuffer[2]))
            {
                return new NntpStatusLine(NntpResponseKind.ProtocolError, 0, "status line missing three-digit code");
            }

            var code = ((lineBuffer[0] - '0') * 100)
                + ((lineBuffer[1] - '0') * 10)
                + (lineBuffer[2] - '0');
            return new NntpStatusLine(ClassifyStatus(code), code, null);
        }

        /// <summary>
        /// After a <c>220</c> status line, consumes the multiline article and terminator.
        /// Article octets are discarded after accounting; the corpus is not retained.
        /// </summary>
        public static async Task<NntpArticleConsumeResult> ConsumeArticleBodyAsync(
            Stream stream,
            byte[] readBuffer,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(readBuffer);
            if (readBuffer.Length < 4)
            {
                throw new ArgumentException("readBuffer must be at least 4 bytes.", nameof(readBuffer));
            }

            long wireBytes = 0;
            long logicalBytes = 0;
            // Line state: 0 = start of line, 1 = seen '.', 2 = seen '.\r', else mid-line.
            var state = 0;
            // When state==1 and next byte is not CR, the '.' was stuffing and must be counted on wire only.

            while (true)
            {
                var read = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return new NntpArticleConsumeResult(
                        NntpResponseKind.Incomplete,
                        wireBytes,
                        logicalBytes,
                        "peer closed before multiline terminator");
                }

                for (var i = 0; i < read; i++)
                {
                    var b = readBuffer[i];
                    switch (state)
                    {
                        case 0: // start of line
                            if (b == (byte)'.')
                            {
                                state = 1;
                            }
                            else
                            {
                                wireBytes++;
                                logicalBytes++;
                                state = b == (byte)'\n' ? 0 : 3;
                            }

                            break;

                        case 1: // seen '.' at line start
                            if (b == (byte)'\r')
                            {
                                state = 2;
                            }
                            else
                            {
                                // Stuffing: wire includes the '.', logical does not.
                                wireBytes++;
                                wireBytes++;
                                logicalBytes++;
                                state = b == (byte)'\n' ? 0 : 3;
                            }

                            break;

                        case 2: // seen '.\r'
                            if (b == (byte)'\n')
                            {
                                return new NntpArticleConsumeResult(
                                    NntpResponseKind.ArticleDelivered,
                                    wireBytes,
                                    logicalBytes,
                                    null);
                            }

                            // Not a terminator; emit '.\r' as body ('.' is stuffing if we treat as line —
                            // protocol-wise ".\rX" is malformed; count as wire/logical for diagnosis).
                            wireBytes += 2;
                            logicalBytes += 1; // destuff leading dot
                            wireBytes++;
                            logicalBytes++;
                            // b is not LF here (LF returned above); stay mid-line after the malformed ".\r" pair.
                            state = 3;
                            break;

                        default: // mid-line
                            wireBytes++;
                            logicalBytes++;
                            if (b == (byte)'\n')
                            {
                                state = 0;
                            }

                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Full ARTICLE exchange helper for in-memory test streams: status then optional body.
        /// </summary>
        public static async Task<NntpArticleExchangeResult> ReadArticleExchangeAsync(
            Stream stream,
            byte[] lineBuffer,
            byte[] readBuffer,
            CancellationToken cancellationToken)
        {
            var status = await ReadStatusLineAsync(stream, lineBuffer, cancellationToken).ConfigureAwait(false);
            if (status.Kind != NntpResponseKind.ArticleFollows)
            {
                return new NntpArticleExchangeResult(
                    status.Kind,
                    status.Code,
                    WireArticleBytes: 0,
                    LogicalArticleBytes: 0,
                    status.Error);
            }

            var body = await ConsumeArticleBodyAsync(stream, readBuffer, cancellationToken).ConfigureAwait(false);
            return new NntpArticleExchangeResult(
                body.Kind,
                status.Code,
                body.WireArticleBytes,
                body.LogicalArticleBytes,
                body.Error);
        }

        /// <summary>Maps a three-digit NNTP code to a stress-test outcome kind.</summary>
        public static NntpResponseKind ClassifyStatus(int code) =>
            code switch
            {
                220 => NntpResponseKind.ArticleFollows,
                430 => NntpResponseKind.NotFound,
                403 => NntpResponseKind.NotAvailable,
                412 => NntpResponseKind.NoNewsgroup,
                500 => NntpResponseKind.UnknownCommand,
                501 => NntpResponseKind.SyntaxError,
                _ => NntpResponseKind.UnexpectedStatus,
            };

        /// <summary>
        /// Classifies an end-to-end Article Work outcome plus NNTP delivery into a stable failure bucket.
        /// </summary>
        public static StressFailureClass ClassifyFailure(
            ArticleWorkStressOutcome? workOutcome,
            NntpResponseKind? nntpKind,
            bool timedOut,
            bool disconnected)
        {
            if (timedOut)
            {
                return StressFailureClass.Timeout;
            }

            if (disconnected)
            {
                return StressFailureClass.Disconnect;
            }

            if (workOutcome is { } work)
            {
                switch (work)
                {
                    case ArticleWorkStressOutcome.ArticleNotFound:
                        return StressFailureClass.MissingOrExpired;
                    case ArticleWorkStressOutcome.InvalidArticle:
                        return StressFailureClass.UpstreamOrValidation;
                    case ArticleWorkStressOutcome.InvalidRequest:
                        return StressFailureClass.ProtocolError;
                    case ArticleWorkStressOutcome.ProviderFailure:
                    case ArticleWorkStressOutcome.RetentionRejected:
                        return StressFailureClass.UpstreamOrValidation;
                    case ArticleWorkStressOutcome.UnexpectedFailure:
                        return StressFailureClass.Other;
                    case ArticleWorkStressOutcome.BrokerError:
                        return StressFailureClass.RabbitMqError;
                    case ArticleWorkStressOutcome.NoResponse:
                        return StressFailureClass.Timeout;
                    case ArticleWorkStressOutcome.Success:
                        break;
                    default:
                        return StressFailureClass.Other;
                }
            }

            return nntpKind switch
            {
                null => StressFailureClass.Other,
                NntpResponseKind.ArticleDelivered => StressFailureClass.None,
                NntpResponseKind.NotFound => StressFailureClass.MissingOrExpired,
                NntpResponseKind.NotAvailable => StressFailureClass.NotAvailable,
                NntpResponseKind.Incomplete => StressFailureClass.IncompleteResponse,
                NntpResponseKind.ProtocolError => StressFailureClass.ProtocolError,
                NntpResponseKind.UnexpectedStatus => StressFailureClass.ProtocolError,
                NntpResponseKind.Disconnect => StressFailureClass.Disconnect,
                _ => StressFailureClass.Other,
            };
        }

        private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';
    }

    /// <summary>Listener ARTICLE / multiline outcome kinds used by the stress harness.</summary>
    public enum NntpResponseKind
    {
        ArticleFollows,
        ArticleDelivered,
        NotFound,
        NotAvailable,
        NoNewsgroup,
        UnknownCommand,
        SyntaxError,
        UnexpectedStatus,
        ProtocolError,
        Incomplete,
        Disconnect,
    }

    /// <summary>Article Work reply outcomes observable by the stress publisher (wire names).</summary>
    public enum ArticleWorkStressOutcome
    {
        Success,
        ArticleNotFound,
        InvalidArticle,
        InvalidRequest,
        ProviderFailure,
        UnexpectedFailure,
        RetentionRejected,
        NoResponse,
        BrokerError,
    }

    /// <summary>Stable failure buckets for reports.</summary>
    public enum StressFailureClass
    {
        None,
        MissingOrExpired,
        NotAvailable,
        Timeout,
        Disconnect,
        IncompleteResponse,
        ProtocolError,
        UpstreamOrValidation,
        RabbitMqError,
        Other,
    }

    /// <summary>One NNTP status line.</summary>
    public readonly record struct NntpStatusLine(NntpResponseKind Kind, int Code, string? Error);

    /// <summary>Result of consuming a multiline article body.</summary>
    public readonly record struct NntpArticleConsumeResult(
        NntpResponseKind Kind,
        long WireArticleBytes,
        long LogicalArticleBytes,
        string? Error);

    /// <summary>Combined status + body consume result.</summary>
    public readonly record struct NntpArticleExchangeResult(
        NntpResponseKind Kind,
        int StatusCode,
        long WireArticleBytes,
        long LogicalArticleBytes,
        string? Error);
}
