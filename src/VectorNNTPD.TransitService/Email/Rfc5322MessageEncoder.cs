using System.Globalization;
using System.Text;

namespace VectorNNTPD.TransitService.Email
{
    /// <summary>RFC 5322 / MIME encoder with header-injection rejection.</summary>
    public sealed class Rfc5322MessageEncoder : IEmailMessageEncoder
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <inheritdoc />
        public ReadOnlyMemory<byte> Encode(EmailMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            Validate(message);

            var builder = new StringBuilder(512 + message.Body.Length);
            WriteHeader(builder, "From", FormatMailbox(message.From));
            WriteHeader(builder, "To", FormatMailboxList(message.To));
            if (message.Cc.Count > 0)
            {
                WriteHeader(builder, "Cc", FormatMailboxList(message.Cc));
            }

            if (message.ReplyTo.Count > 0)
            {
                WriteHeader(builder, "Reply-To", FormatMailboxList(message.ReplyTo));
            }

            WriteHeader(builder, "Subject", EncodeHeaderValue(message.Subject));
            WriteHeader(builder, "Date", DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture));
            WriteHeader(builder, "MIME-Version", "1.0");
            WriteHeader(builder, "Message-ID", CreateMessageId(message.From.Address));

            foreach (var header in message.Headers)
            {
                header.Validate();
                RejectReservedHeader(header.Name);
                WriteHeader(builder, header.Name, EncodeHeaderValue(header.Value));
            }

            if (message.Attachments.Count == 0)
            {
                WriteSinglePart(builder, message);
            }
            else
            {
                WriteMultipart(builder, message);
            }

            return Encoding.ASCII.GetBytes(builder.ToString());
        }

        internal static void Validate(EmailMessage message)
        {
            ArgumentNullException.ThrowIfNull(message.From);
            if (message.To is null || message.To.Count == 0)
            {
                throw new ArgumentException("At least one To recipient is required.", nameof(message));
            }

            if (message.Subject is null || EmailAddress.ContainsLineBreak(message.Subject))
            {
                throw new ArgumentException("Subject must not contain CR or LF.", nameof(message));
            }

            if (string.IsNullOrWhiteSpace(message.ContentType)
                || EmailAddress.ContainsLineBreak(message.ContentType))
            {
                throw new ArgumentException("ContentType must be a media type without CR or LF.", nameof(message));
            }

            if (string.IsNullOrWhiteSpace(message.Charset) || EmailAddress.ContainsLineBreak(message.Charset))
            {
                throw new ArgumentException("Charset is invalid.", nameof(message));
            }

            foreach (var header in message.Headers)
            {
                header.Validate();
            }

            foreach (var attachment in message.Attachments)
            {
                attachment.Validate();
            }

            if (message.EnvelopeRecipients().Count == 0)
            {
                throw new ArgumentException("The message has no envelope recipients.", nameof(message));
            }
        }

        private static void WriteSinglePart(StringBuilder builder, EmailMessage message)
        {
            var body = NormalizeBody(message.Body);
            var needsQuotedPrintable = RequiresQuotedPrintable(body);
            var contentType = message.ContentType;
            if ((contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase)
                    || contentType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
                && contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase) < 0)
            {
                contentType = $"{contentType}; charset={message.Charset}";
            }

            WriteHeader(builder, "Content-Type", contentType);
            WriteHeader(builder, "Content-Transfer-Encoding", needsQuotedPrintable ? "quoted-printable" : "7bit");
            builder.Append("\r\n");
            builder.Append(needsQuotedPrintable ? EncodeQuotedPrintable(body) : Encoding.ASCII.GetString(body));
            if (body.Length == 0 || body[^1] != (byte)'\n')
            {
                builder.Append("\r\n");
            }
        }

        private static void WriteMultipart(StringBuilder builder, EmailMessage message)
        {
            var boundary = "vnntp-" + Guid.NewGuid().ToString("N");
            WriteHeader(builder, "Content-Type", $"multipart/mixed; boundary=\"{boundary}\"");
            builder.Append("\r\n");
            builder.Append("This is a multi-part message in MIME format.\r\n\r\n");
            builder.Append("--").Append(boundary).Append("\r\n");

            var bodyMessage = new EmailMessage
            {
                From = message.From,
                To = message.To,
                Subject = message.Subject,
                Body = message.Body,
                ContentType = message.ContentType,
                Charset = message.Charset,
            };
            WriteSinglePart(builder, bodyMessage);

            foreach (var attachment in message.Attachments)
            {
                builder.Append("--").Append(boundary).Append("\r\n");
                WriteHeader(builder, "Content-Type", attachment.ContentType);
                WriteHeader(builder, "Content-Transfer-Encoding", "base64");
                WriteHeader(
                    builder,
                    "Content-Disposition",
                    $"attachment; filename=\"{attachment.FileName}\"");
                builder.Append("\r\n");
                builder.Append(
                    Convert.ToBase64String(attachment.Content.Span, Base64FormattingOptions.InsertLineBreaks)
                        .Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace("\n", "\r\n", StringComparison.Ordinal));
                if (!builder.ToString().EndsWith("\r\n", StringComparison.Ordinal))
                {
                    builder.Append("\r\n");
                }
            }

            builder.Append("--").Append(boundary).Append("--\r\n");
        }

        private static void WriteHeader(StringBuilder builder, string name, string value)
        {
            builder.Append(name).Append(": ").Append(Fold(name, value)).Append("\r\n");
        }

        private static string Fold(string name, string value)
        {
            if (name.Length + 2 + value.Length <= 78)
            {
                return value;
            }

            var folded = new StringBuilder();
            var budget = 78 - name.Length - 2;
            var offset = 0;
            while (offset < value.Length)
            {
                var take = Math.Min(budget, value.Length - offset);
                if (offset > 0)
                {
                    folded.Append("\r\n ");
                }

                folded.Append(value, offset, take);
                offset += take;
                budget = 77;
            }

            return folded.ToString();
        }

        internal static string FormatMailbox(EmailAddress address)
        {
            if (string.IsNullOrEmpty(address.DisplayName))
            {
                return address.Address;
            }

            return $"{QuoteDisplayName(address.DisplayName)} <{address.Address}>";
        }

        private static string FormatMailboxList(IReadOnlyList<EmailAddress> addresses)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < addresses.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(FormatMailbox(addresses[i]));
            }

            return builder.ToString();
        }

        private static string QuoteDisplayName(string displayName)
        {
            var escaped = displayName.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
            return "\"" + escaped + "\"";
        }

        internal static string EncodeHeaderValue(string value)
        {
            var ascii = true;
            // codeql[cs/linq/missed-all] Foreach over string avoids CharEnumerator allocation from All().
            foreach (var ch in value)
            {
                if (ch > 127)
                {
                    ascii = false;
                    break;
                }
            }

            if (ascii)
            {
                return value;
            }

            var bytes = Utf8.GetBytes(value);
            return "=?UTF-8?B?" + Convert.ToBase64String(bytes) + "?=";
        }

        private static string CreateMessageId(string from)
        {
            var at = from.LastIndexOf('@');
            var domain = at >= 0 ? from[(at + 1)..] : "localhost";
            return "<" + Guid.NewGuid().ToString("N") + "@" + domain + ">";
        }

        private static void RejectReservedHeader(string name)
        {
            if (name.Equals("From", StringComparison.OrdinalIgnoreCase)
                || name.Equals("To", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Cc", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Bcc", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Subject", StringComparison.OrdinalIgnoreCase)
                || name.Equals("MIME-Version", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Header '{name}' is reserved and must be set on EmailMessage.");
            }
        }

        internal static byte[] NormalizeBody(ReadOnlyMemory<byte> body)
        {
            if (body.Length == 0)
            {
                return [];
            }

            var span = body.Span;
            var output = new List<byte>(span.Length + 8);
            for (var i = 0; i < span.Length; i++)
            {
                var b = span[i];
                if (b == (byte)'\r')
                {
                    output.Add((byte)'\r');
                    output.Add((byte)'\n');
                    if (i + 1 < span.Length && span[i + 1] == (byte)'\n')
                    {
                        i++;
                    }
                }
                else if (b == (byte)'\n')
                {
                    output.Add((byte)'\r');
                    output.Add((byte)'\n');
                }
                else
                {
                    output.Add(b);
                }
            }

            return [.. output];
        }

        internal static bool RequiresQuotedPrintable(ReadOnlySpan<byte> body)
        {
            var lineLength = 0;
            foreach (var b in body)
            {
                if (b > 127 || b == 0)
                {
                    return true;
                }

                if (b == (byte)'\n')
                {
                    lineLength = 0;
                }
                else if (b != (byte)'\r')
                {
                    lineLength++;
                    if (lineLength > 998)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static string EncodeQuotedPrintable(ReadOnlySpan<byte> body)
        {
            var builder = new StringBuilder(body.Length + (body.Length / 8));
            var line = 0;
            for (var i = 0; i < body.Length; i++)
            {
                var b = body[i];
                if (b == (byte)'\r' && i + 1 < body.Length && body[i + 1] == (byte)'\n')
                {
                    builder.Append("\r\n");
                    line = 0;
                    i++;
                    continue;
                }

                var encode = b > 127 || b < 32 || b == (byte)'=';
                var token = encode
                    ? "=" + b.ToString("X2", CultureInfo.InvariantCulture)
                    : ((char)b).ToString();
                if (line + token.Length > 75)
                {
                    builder.Append("=\r\n");
                    line = 0;
                }

                builder.Append(token);
                line += token.Length;
            }

            return builder.ToString();
        }
    }
}
