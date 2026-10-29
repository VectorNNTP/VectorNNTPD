using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.ReaderService.PostFilter
{
    /// <summary>
    /// Builds a disposable email-like SPAMD CHECK payload from a canonical article.
    /// </summary>
    /// <remarks>
    /// Does not mutate <see cref="ArticleRecord.ArtData"/> or any other article field.
    /// The returned buffer is independent of the article and is only the bytes sent to SPAMD.
    /// </remarks>
    internal static class SpamdScanArticleBuilder
    {
        private static readonly byte[] Crlf = "\r\n"u8.ToArray();
        private static readonly byte[] HeaderSeparator = "\r\n\r\n"u8.ToArray();

        /// <summary>Constructs the SPAMD scan representation.</summary>
        /// <param name="article">Canonical article. Read-only.</param>
        /// <param name="context">Peer address, server FQDN, and evaluation clock.</param>
        /// <returns>Independent scan bytes. Never aliases <see cref="ArticleRecord.ArtData"/>.</returns>
        public static byte[] Build(in ArticleRecord article, in SpamdScanContext context)
        {
            ArgumentNullException.ThrowIfNull(context.ClientAddress);
            var fqdn = SanitizeToken(context.ServerFqdn);
            var artData = article.ArtData.Span;
            SplitArticle(artData, out var headerBlock, out var body);
            ScanOriginalHeaders(
                headerBlock,
                out var hasDate,
                out var messageId,
                out var newsgroups,
                out var preservedHeaders);

            if (messageId.Length == 0 && article.Fields.MessageId.IsPresent)
            {
                messageId = article.MessageId.ToArray();
            }

            if (newsgroups.Length == 0 && article.Fields.Newsgroups.IsPresent)
            {
                newsgroups = article.Newsgroups.ToArray();
            }

            var writer = new ArrayBufferWriter<byte>(256 + artData.Length);
            WriteReceived(writer, context.ClientAddress, fqdn, messageId, context.Now);
            WriteAscii(writer, "To: usenet@");
            WriteAscii(writer, fqdn);
            writer.Write(Crlf);
            if (newsgroups.Length > 0)
            {
                WriteAscii(writer, "X-Usenet-Newsgroups: ");
                writer.Write(newsgroups);
                writer.Write(Crlf);
            }

            if (!hasDate)
            {
                WriteAscii(writer, "Date: ");
                WriteAscii(writer, FormatDate(context.Now));
                writer.Write(Crlf);
            }

            writer.Write(preservedHeaders);
            writer.Write(Crlf);
            writer.Write(body);
            return writer.WrittenSpan.ToArray();
        }

        private static void WriteReceived(
            ArrayBufferWriter<byte> writer,
            IPAddress client,
            string fqdn,
            ReadOnlySpan<byte> messageId,
            DateTimeOffset now)
        {
            WriteAscii(writer, "Received: from [");
            WriteAscii(writer, client.ToString());
            WriteAscii(writer, "] by ");
            WriteAscii(writer, fqdn);
            WriteAscii(writer, " with NNTP");
            if (!messageId.IsEmpty)
            {
                WriteAscii(writer, " id ");
                writer.Write(messageId);
            }

            WriteAscii(writer, "; ");
            WriteAscii(writer, FormatDate(now));
            writer.Write(Crlf);
        }

        private static void SplitArticle(
            ReadOnlySpan<byte> artData,
            out ReadOnlySpan<byte> headers,
            out ReadOnlySpan<byte> body)
        {
            var separator = artData.IndexOf(HeaderSeparator);
            if (separator < 0)
            {
                headers = artData;
                body = ReadOnlySpan<byte>.Empty;
                return;
            }

            headers = artData[..separator];
            body = artData[(separator + HeaderSeparator.Length)..];
        }

        private static void ScanOriginalHeaders(
            ReadOnlySpan<byte> headerBlock,
            out bool hasDate,
            out byte[] messageId,
            out byte[] newsgroups,
            out byte[] preservedHeaders)
        {
            hasDate = false;
            messageId = [];
            newsgroups = [];
            if (headerBlock.IsEmpty)
            {
                preservedHeaders = [];
                return;
            }

            var preserved = new ArrayBufferWriter<byte>(headerBlock.Length);
            var offset = 0;
            while (offset < headerBlock.Length)
            {
                var blockStart = offset;
                if (!TryReadLine(headerBlock, ref offset, out var line))
                {
                    break;
                }

                while (offset < headerBlock.Length
                       && headerBlock[offset] is (byte)' ' or (byte)'\t')
                {
                    if (!TryReadLine(headerBlock, ref offset, out _))
                    {
                        break;
                    }
                }

                var block = headerBlock[blockStart..offset];
                var colon = line.IndexOf((byte)':');
                var name = colon < 0 ? line : line[..colon];
                if (IsStripped(name))
                {
                    continue;
                }

                if (AsciiEqualsIgnoreCase(name, "Date"u8))
                {
                    hasDate = true;
                }
                else if (AsciiEqualsIgnoreCase(name, "Message-ID"u8) && messageId.Length == 0)
                {
                    messageId = ExtractValue(line, colon);
                }
                else if (AsciiEqualsIgnoreCase(name, "Newsgroups"u8) && newsgroups.Length == 0)
                {
                    newsgroups = ExtractValue(line, colon);
                }

                preserved.Write(block);
                if (block.Length < 2
                    || block[^2] != (byte)'\r'
                    || block[^1] != (byte)'\n')
                {
                    preserved.Write(Crlf);
                }
            }

            preservedHeaders = preserved.WrittenSpan.ToArray();
        }

        private static bool TryReadLine(ReadOnlySpan<byte> headers, ref int offset, out ReadOnlySpan<byte> line)
        {
            var remaining = headers[offset..];
            var eol = remaining.IndexOf("\r\n"u8);
            if (eol < 0)
            {
                line = remaining;
                offset = headers.Length;
                return !line.IsEmpty;
            }

            line = remaining[..eol];
            offset += eol + 2;
            return true;
        }

        private static byte[] ExtractValue(ReadOnlySpan<byte> line, int colon)
        {
            if (colon < 0 || colon + 1 >= line.Length)
            {
                return [];
            }

            var value = line[(colon + 1)..];
            while (!value.IsEmpty && value[0] is (byte)' ' or (byte)'\t')
            {
                value = value[1..];
            }

            return value.ToArray();
        }

        private static bool IsStripped(ReadOnlySpan<byte> name) =>
            AsciiEqualsIgnoreCase(name, "Path"u8)
            || AsciiEqualsIgnoreCase(name, "Xref"u8)
            || AsciiEqualsIgnoreCase(name, "Injection-Info"u8)
            || AsciiEqualsIgnoreCase(name, "X-Trace"u8)
            || AsciiEqualsIgnoreCase(name, "X-Complaints-To"u8)
            || AsciiEqualsIgnoreCase(name, "NNTP-Posting-Host"u8);

        private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (var i = 0; i < left.Length; i++)
            {
                if (ToLower(left[i]) != ToLower(right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static byte ToLower(byte value) =>
            value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;

        private static string SanitizeToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "localhost";
            }

            var trimmed = value.Trim();
            foreach (var ch in trimmed)
            {
                if (ch is '\r' or '\n' or '\0')
                {
                    return "localhost";
                }
            }

            return trimmed;
        }

        private static string FormatDate(DateTimeOffset now) =>
            now.UtcDateTime.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture);

        private static void WriteAscii(ArrayBufferWriter<byte> writer, string value) =>
            writer.Write(Encoding.ASCII.GetBytes(value));
    }
}
