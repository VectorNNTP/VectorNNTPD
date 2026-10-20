namespace VectorNNTPD.Common.Articles.OverviewDb
{
    /// <summary>
    /// Compact protobuf encoder/decoder for <see cref="OverviewArticleV1"/>.
    /// </summary>
    /// <remarks>
    /// Encodes from <see cref="ArticleRecord"/> field ranges without copying
    /// <see cref="ArticleRecord.ArtData"/> or reparsing the article. Header fields
    /// are the canonical value octets already stored in ArtData, written as protobuf
    /// length-delimited <c>bytes</c> (not UTF-8-decoded). Decode copies those octets
    /// unchanged so values with <c>0x80</c>–<c>0xFF</c> round-trip. Newsgroups are
    /// tokenized from the Newsgroups value range only (comma-separated, SP/HTAB/CR/LF
    /// skipped). RabbitMQ transport is not this type's responsibility.
    /// </remarks>
    internal static class OverviewArticleV1Codec
    {
        /// <summary>Protobuf wire type 0, used for schema version, bytes, and lines.</summary>
        private const int WireVarint = 0;

        /// <summary>Protobuf wire type 2, used for article id and the text fields.</summary>
        private const int WireLengthDelimited = 2;

        /// <summary>Field number 1. Varint schema version.</summary>
        private const int FieldSchemaVersion = 1;

        /// <summary>Field number 2. Length-delimited 32-byte <see cref="ArticleId"/> digest.</summary>
        private const int FieldArticleId = 2;

        /// <summary>Field number 3. Length-delimited Message-ID header value octets.</summary>
        private const int FieldMessageId = 3;

        /// <summary>Field number 4. Repeated length-delimited newsgroup token octets.</summary>
        private const int FieldNewsgroups = 4;

        /// <summary>Field number 5. Length-delimited Subject header value octets.</summary>
        private const int FieldSubject = 5;

        /// <summary>Field number 6. Length-delimited From header value octets.</summary>
        private const int FieldFrom = 6;

        /// <summary>Field number 7. Length-delimited winning Date header value octets.</summary>
        private const int FieldDate = 7;

        /// <summary>Field number 8. Length-delimited References header value octets.</summary>
        private const int FieldReferences = 8;

        /// <summary>Field number 9. Varint article size. Negative <see cref="ArticleRecord.ArtSize"/> is written as 0.</summary>
        private const int FieldBytes = 9;

        /// <summary>Field number 10. Varint body line count. Negative <see cref="ArticleRecord.ArtLines"/> is written as 0.</summary>
        private const int FieldLines = 10;

        /// <summary>
        /// Per comma-separated Newsgroups piece added by <see cref="GetMaxEncodedSize"/>.
        /// Slack for that token's key and length varint, not a measured tag size.
        /// </summary>
        private const int TagOverhead = 10;

        /// <summary>
        /// Fixed slack in <see cref="GetMaxEncodedSize"/> for the scalar keys, varints, and article-id framing.
        /// </summary>
        private const int FixedScalarOverhead = 64;

        /// <summary>
        /// Upper bound of the encoded payload for <paramref name="record"/>.
        /// </summary>
        /// <param name="record">Canonical article whose overview fields will be encoded.</param>
        /// <returns>A rent size large enough for <see cref="Encode(in ArticleRecord, Span{byte})"/>.</returns>
        internal static int GetMaxEncodedSize(in ArticleRecord record)
        {
            var newsgroups = record.Newsgroups;
            var groupTagOverhead = 0;
            var remaining = newsgroups;
            while (!remaining.IsEmpty)
            {
                groupTagOverhead += TagOverhead;
                var comma = remaining.IndexOf((byte)',');
                remaining = comma < 0 ? default : remaining[(comma + 1)..];
            }

            return FixedScalarOverhead
                + ArticleId.Length
                + record.MessageId.Length
                + newsgroups.Length
                + groupTagOverhead
                + record.Subject.Length
                + record.From.Length
                + record.Date.Length
                + record.References.Length;
        }

        /// <summary>
        /// Encodes overview metadata from <paramref name="record"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="record">CanonicalV1 article. The body is not copied.</param>
        /// <param name="destination">Caller-provided buffer; see <see cref="GetMaxEncodedSize"/>.</param>
        /// <returns>The number of bytes written.</returns>
        /// <exception cref="ArgumentException">Thrown when the destination is too small.</exception>
        internal static int Encode(in ArticleRecord record, Span<byte> destination)
        {
            var written = 0;
            written += WriteVarintField(destination[written..], FieldSchemaVersion, OverviewArticleV1.CurrentSchemaVersion);

            Span<byte> artId = stackalloc byte[ArticleId.Length];
            record.ArtId.CopyTo(artId);
            written += WriteBytesField(destination[written..], FieldArticleId, artId);

            written += WriteBytesField(destination[written..], FieldMessageId, record.MessageId);
            written += WriteNewsgroups(destination[written..], record.Newsgroups);
            written += WriteBytesField(destination[written..], FieldSubject, record.Subject);
            written += WriteBytesField(destination[written..], FieldFrom, record.From);
            written += WriteBytesField(destination[written..], FieldDate, record.Date);
            written += WriteBytesField(destination[written..], FieldReferences, record.References);
            written += WriteVarintField(destination[written..], FieldBytes, (uint)Math.Max(record.ArtSize, 0));
            written += WriteVarintField(destination[written..], FieldLines, (uint)Math.Max(record.ArtLines, 0));
            return written;
        }

        /// <summary>
        /// Allocates a compact encoded payload for tests and diagnostics.
        /// </summary>
        /// <param name="record">Canonical article to encode.</param>
        /// <returns>The protobuf bytes, sized exactly to the encoded length.</returns>
        internal static byte[] Encode(in ArticleRecord record)
        {
            var buffer = new byte[GetMaxEncodedSize(record)];
            var written = Encode(record, buffer);
            if (written == buffer.Length)
            {
                return buffer;
            }

            var exact = new byte[written];
            buffer.AsSpan(0, written).CopyTo(exact);
            return exact;
        }

        /// <summary>
        /// Decodes a protobuf payload produced by <see cref="Encode(in ArticleRecord, Span{byte})"/>.
        /// </summary>
        /// <param name="payload">OverviewDB handoff bytes.</param>
        /// <returns>The decoded message.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the payload is truncated or uses an unknown wire type.</exception>
        internal static OverviewArticleV1 Decode(ReadOnlySpan<byte> payload)
        {
            var message = new OverviewArticleV1();
            var remaining = payload;
            while (!remaining.IsEmpty)
            {
                remaining = ReadVarint(remaining, out var key);
                var field = (int)(key >> 3);
                var wire = (int)(key & 0x7);
                switch (wire)
                {
                    case WireVarint:
                        remaining = ReadVarint(remaining, out var number);
                        switch (field)
                        {
                            case FieldSchemaVersion:
                                message.SchemaVersion = (uint)number;
                                break;
                            case FieldBytes:
                                message.Bytes = (uint)number;
                                break;
                            case FieldLines:
                                message.Lines = (uint)number;
                                break;
                        }

                        break;
                    case WireLengthDelimited:
                        remaining = ReadVarint(remaining, out var length);
                        if (length > (ulong)remaining.Length)
                        {
                            throw new InvalidOperationException("OverviewDB protobuf payload is truncated.");
                        }

                        var slice = remaining[..(int)length];
                        remaining = remaining[(int)length..];
                        switch (field)
                        {
                            case FieldArticleId:
                                message.ArticleId = CopyFieldBytes(slice);
                                break;
                            case FieldMessageId:
                                message.MessageId = CopyFieldBytes(slice);
                                break;
                            case FieldNewsgroups:
                                message.Newsgroups.Add(CopyFieldBytes(slice));
                                break;
                            case FieldSubject:
                                message.Subject = CopyFieldBytes(slice);
                                break;
                            case FieldFrom:
                                message.From = CopyFieldBytes(slice);
                                break;
                            case FieldDate:
                                message.Date = CopyFieldBytes(slice);
                                break;
                            case FieldReferences:
                                message.References = CopyFieldBytes(slice);
                                break;
                        }

                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported protobuf wire type {wire}.");
                }
            }

            return message;
        }

        /// <summary>
        /// Returns whether <paramref name="payload"/> contains every byte of <paramref name="artData"/>.
        /// </summary>
        /// <param name="payload">Encoded overview message.</param>
        /// <param name="artData">Canonical article bytes.</param>
        /// <returns><see langword="true"/> when the full article is present as a contiguous span.</returns>
        internal static bool ContainsCompleteArticle(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> artData)
            => artData.Length > 0 && payload.IndexOf(artData) >= 0;

        /// <summary>
        /// Copies length-delimited overview field octets. Empty slices reuse <see cref="Array.Empty{T}"/>.
        /// </summary>
        /// <param name="slice">Field payload from the protobuf stream.</param>
        /// <returns>Owned copy of <paramref name="slice"/>, or an empty array when the field length is zero.</returns>
        private static byte[] CopyFieldBytes(ReadOnlySpan<byte> slice)
            => slice.IsEmpty ? [] : slice.ToArray();

        /// <summary>
        /// Writes each non-empty comma-separated token as a repeated <see cref="FieldNewsgroups"/> length-delimited field.
        /// </summary>
        /// <param name="destination">Remaining encode buffer.</param>
        /// <param name="newsgroups">Newsgroups header value bytes. SP, HTAB, CR, and LF around each token are omitted.</param>
        /// <returns>Bytes written. Empty tokens, including a trailing comma, are skipped.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> cannot hold a token.</exception>
        private static int WriteNewsgroups(Span<byte> destination, ReadOnlySpan<byte> newsgroups)
        {
            var written = 0;
            var remaining = newsgroups;
            while (!remaining.IsEmpty)
            {
                var comma = remaining.IndexOf((byte)',');
                var token = comma < 0 ? remaining : remaining[..comma];
                remaining = comma < 0 ? default : remaining[(comma + 1)..];
                token = TrimHeaderToken(token);
                if (token.IsEmpty)
                {
                    continue;
                }

                written += WriteBytesField(destination[written..], FieldNewsgroups, token);
            }

            return written;
        }

        /// <summary>Drops leading and trailing SP, HTAB, CR, and LF from one newsgroup token.</summary>
        /// <param name="value">Bytes between commas, or the whole value when there is no comma.</param>
        /// <returns>A slice of <paramref name="value"/>.</returns>
        private static ReadOnlySpan<byte> TrimHeaderToken(ReadOnlySpan<byte> value)
        {
            while (!value.IsEmpty && IsHeaderWhitespace(value[0]))
            {
                value = value[1..];
            }

            while (!value.IsEmpty && IsHeaderWhitespace(value[^1]))
            {
                value = value[..^1];
            }

            return value;
        }

        /// <summary>Returns whether <paramref name="value"/> is SP, HTAB, CR, or LF.</summary>
        /// <param name="value">Byte to test.</param>
        /// <returns><see langword="true"/> for those four bytes only.</returns>
        private static bool IsHeaderWhitespace(byte value) =>
            value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

        /// <summary>Writes a protobuf key with wire type <see cref="WireVarint"/> and then <paramref name="value"/>.</summary>
        /// <param name="destination">Remaining encode buffer.</param>
        /// <param name="fieldNumber">Protobuf field number.</param>
        /// <param name="value">Unsigned scalar.</param>
        /// <returns>Bytes written.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is too small.</exception>
        private static int WriteVarintField(Span<byte> destination, int fieldNumber, uint value)
        {
            var written = WriteKey(destination, fieldNumber, WireVarint);
            written += WriteVarint(destination[written..], value);
            return written;
        }

        /// <summary>
        /// Writes a length-delimited field by copying <paramref name="value"/> unchanged. Does not UTF-8-encode.
        /// </summary>
        /// <param name="destination">Remaining encode buffer.</param>
        /// <param name="fieldNumber">Protobuf field number.</param>
        /// <param name="value">Raw field bytes.</param>
        /// <returns>Bytes written, including key, length, and payload.</returns>
        /// <exception cref="ArgumentException">Thrown when the key, length, or payload does not fit.</exception>
        private static int WriteBytesField(Span<byte> destination, int fieldNumber, ReadOnlySpan<byte> value)
        {
            var written = WriteKey(destination, fieldNumber, WireLengthDelimited);
            written += WriteVarint(destination[written..], (uint)value.Length);
            if (destination.Length - written < value.Length)
            {
                throw new ArgumentException("OverviewDB protobuf destination is too small.", nameof(destination));
            }

            value.CopyTo(destination[written..]);
            return written + value.Length;
        }

        /// <summary>Writes <c>(fieldNumber &lt;&lt; 3) | wireType</c> as a varint.</summary>
        /// <param name="destination">Remaining encode buffer.</param>
        /// <param name="fieldNumber">Protobuf field number.</param>
        /// <param name="wireType"><see cref="WireVarint"/> or <see cref="WireLengthDelimited"/>.</param>
        /// <returns>Bytes written.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is too small.</exception>
        private static int WriteKey(Span<byte> destination, int fieldNumber, int wireType) =>
            WriteVarint(destination, ((uint)fieldNumber << 3) | (uint)wireType);

        /// <summary>
        /// Writes <paramref name="value"/> as a protobuf varint, 7 bits per byte with the continuation bit <c>0x80</c>.
        /// </summary>
        /// <param name="destination">Remaining encode buffer.</param>
        /// <param name="value">Unsigned value. At most five bytes are emitted.</param>
        /// <returns>Bytes written.</returns>
        /// <exception cref="ArgumentException">Thrown when a continuation or final byte would pass the end of <paramref name="destination"/>.</exception>
        private static int WriteVarint(Span<byte> destination, uint value)
        {
            var written = 0;
            while (value >= 0x80)
            {
                if (written >= destination.Length)
                {
                    throw new ArgumentException("OverviewDB protobuf destination is too small.", nameof(destination));
                }

                destination[written++] = (byte)(value | 0x80);
                value >>= 7;
            }

            if (written >= destination.Length)
            {
                throw new ArgumentException("OverviewDB protobuf destination is too small.", nameof(destination));
            }

            destination[written++] = (byte)value;
            return written;
        }

        /// <summary>
        /// Reads one protobuf varint from the front of <paramref name="source"/>.
        /// </summary>
        /// <param name="source">Remaining payload.</param>
        /// <param name="value">Decoded integer. Low 7 bits of each byte are accumulated until the continuation bit is clear.</param>
        /// <returns>The unread suffix after the varint.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the payload ends before a byte with bit <c>0x80</c> clear, or when the shift would exceed 63
        /// (<c>OverviewDB protobuf varint is too long.</c> / <c>OverviewDB protobuf payload is truncated.</c>).
        /// </exception>
        private static ReadOnlySpan<byte> ReadVarint(ReadOnlySpan<byte> source, out ulong value)
        {
            value = 0;
            var shift = 0;
            var index = 0;
            while (index < source.Length)
            {
                var b = source[index++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return source[index..];
                }

                shift += 7;
                if (shift > 63)
                {
                    throw new InvalidOperationException("OverviewDB protobuf varint is too long.");
                }
            }

            throw new InvalidOperationException("OverviewDB protobuf payload is truncated.");
        }
    }
}
