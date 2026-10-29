using System.Buffers;

namespace VectorNNTPD.ReaderService.Session.Commands.Posting
{
    /// <summary>Structural destuffed-article header parser. Does not use <c>string.Split</c>.</summary>
    internal static class PostHeaderParser
    {
        /// <summary>
        /// Parses destuffed article bytes (headers + blank separator + body).
        /// Size has already been enforced by the multiline reader.
        /// </summary>
        public static bool TryParse(
            ReadOnlyMemory<byte> payload,
            out ParsedPostArticle? article,
            out PostingFailure failure)
        {
            article = null;
            var span = payload.Span;
            if (span.IsEmpty)
            {
                failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "empty article");
                return false;
            }

            for (var i = 0; i < span.Length; i++)
            {
                if (span[i] == 0)
                {
                    failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "embedded NUL");
                    return false;
                }
            }

            if (!TryFindSeparator(span, out var separator, out failure))
            {
                return false;
            }

            if (separator + 2 > PostingLimits.MaxHeaderBlockBytes)
            {
                failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "header block too large");
                return false;
            }

            var headerBlock = span[..separator];
            var body = payload[(separator + 2)..];
            if (!TryParseHeaderBlock(payload, headerBlock, out var headers, out failure))
            {
                return false;
            }

            article = new ParsedPostArticle(payload, headers, body, separator + 2);
            return true;
        }

        private static bool TryFindSeparator(
            ReadOnlySpan<byte> span,
            out int separator,
            out PostingFailure failure)
        {
            separator = -1;
            var i = 0;
            while (i < span.Length)
            {
                if (span[i] == (byte)'\n' && (i == 0 || span[i - 1] != (byte)'\r'))
                {
                    failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "LF without CR");
                    return false;
                }

                if (span[i] == (byte)'\r')
                {
                    if (i + 1 >= span.Length || span[i + 1] != (byte)'\n')
                    {
                        failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "CR without LF");
                        return false;
                    }

                    if (i + 3 < span.Length && span[i + 2] == (byte)'\r' && span[i + 3] == (byte)'\n')
                    {
                        separator = i + 2;
                        failure = default;
                        return true;
                    }

                    i += 2;
                    continue;
                }

                i++;
            }

            failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "missing header/body separator");
            return false;
        }

        private static bool TryParseHeaderBlock(
            ReadOnlyMemory<byte> payload,
            ReadOnlySpan<byte> headerBlock,
            out List<ParsedPostHeader> headers,
            out PostingFailure failure)
        {
            headers = [];
            failure = default;
            if (headerBlock.IsEmpty)
            {
                failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "empty header block");
                return false;
            }

            if (headerBlock.Length < 2
                || headerBlock[^2] != (byte)'\r'
                || headerBlock[^1] != (byte)'\n')
            {
                failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "header block is not CRLF terminated");
                return false;
            }

            var unfolded = new ArrayBufferWriter<byte>(256);
            var fieldStart = 0;
            var nameStart = -1;
            var nameLength = 0;
            var hasField = false;
            var lineStart = 0;
            var i = 0;
            while (i < headerBlock.Length)
            {
                if (i + 1 >= headerBlock.Length
                    || headerBlock[i] != (byte)'\r'
                    || headerBlock[i + 1] != (byte)'\n')
                {
                    i++;
                    continue;
                }

                var line = headerBlock[lineStart..i];
                var isFold = line.Length > 0 && PostFieldSyntax.IsWsp(line[0]);
                if (isFold)
                {
                    if (!hasField)
                    {
                        failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "leading fold");
                        return false;
                    }

                    if (line.Length < 2)
                    {
                        failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "empty fold");
                        return false;
                    }

                    unfolded.Write(line);
                }
                else
                {
                    if (hasField
                        && !FlushHeader(payload, fieldStart, lineStart, nameStart, nameLength, unfolded, headers, out failure))
                    {
                        return false;
                    }

                    if (line.IsEmpty)
                    {
                        failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "empty header line");
                        return false;
                    }

                    if (!TryReadFieldName(line, out nameLength, out failure))
                    {
                        return false;
                    }

                    fieldStart = lineStart;
                    nameStart = lineStart;
                    hasField = true;
                    unfolded.Clear();
                    unfolded.Write(line[(nameLength + 2)..]);
                }

                i += 2;
                lineStart = i;
            }

            if (lineStart != headerBlock.Length)
            {
                failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "truncated header line");
                return false;
            }

            if (!hasField)
            {
                failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "no header fields");
                return false;
            }

            if (!FlushHeader(payload, fieldStart, headerBlock.Length, nameStart, nameLength, unfolded, headers, out failure))
            {
                return false;
            }

            if (headers.Count > PostingLimits.MaxHeaderCount)
            {
                failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "too many headers");
                return false;
            }

            return true;
        }

        internal static bool TryReadFieldName(
            ReadOnlySpan<byte> line,
            out int nameLength,
            out PostingFailure failure)
        {
            nameLength = 0;
            var colon = line.IndexOf((byte)':');
            if (colon <= 0)
            {
                failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "header line missing field name");
                return false;
            }

            if (colon + 1 >= line.Length || line[colon + 1] != (byte)' ')
            {
                failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "header requires colon-space");
                return false;
            }

            for (var i = 0; i < colon; i++)
            {
                var b = line[i];
                if (b is < 33 or > 126 || b == (byte)':')
                {
                    failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "malformed field name");
                    return false;
                }
            }

            nameLength = colon;
            failure = default;
            return true;
        }

        private static bool FlushHeader(
            ReadOnlyMemory<byte> payload,
            int fieldStart,
            int fieldEnd,
            int nameStart,
            int nameLength,
            ArrayBufferWriter<byte> unfolded,
            List<ParsedPostHeader> headers,
            out PostingFailure failure)
        {
            failure = default;
            var raw = payload.Slice(fieldStart, fieldEnd - fieldStart);
            var name = payload.Slice(nameStart, nameLength);
            var value = unfolded.WrittenCount == 0
                ? ReadOnlyMemory<byte>.Empty
                : unfolded.WrittenMemory.ToArray();

            if (!TryCreateHeader(name, value, raw, out var header, out failure))
            {
                return false;
            }

            headers.Add(header);
            return true;
        }

        /// <summary>Validates unfolded field length/control characters and builds a parsed header.</summary>
        internal static bool TryCreateHeader(
            ReadOnlyMemory<byte> name,
            ReadOnlyMemory<byte> unfoldedValue,
            ReadOnlyMemory<byte> rawField,
            out ParsedPostHeader header,
            out PostingFailure failure)
        {
            header = default;
            if (name.Length + 2 + unfoldedValue.Length > PostingLimits.MaxSingleHeaderBytes)
            {
                failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "header too long");
                return false;
            }

            var valueSpan = unfoldedValue.Span;
            for (var i = 0; i < valueSpan.Length; i++)
            {
                var b = valueSpan[i];
                if (b == 0 || (b < 32 && b != (byte)'\t'))
                {
                    failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "forbidden control character");
                    return false;
                }
            }

            header = new ParsedPostHeader(name, unfoldedValue, rawField);
            failure = default;
            return true;
        }
    }
}
