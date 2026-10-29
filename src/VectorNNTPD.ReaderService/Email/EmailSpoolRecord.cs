using System.Text;

namespace VectorNNTPD.ReaderService.Email
{
    /// <summary>
    /// Deterministic spool file: SMTP envelope preamble plus the RFC 5322/MIME message.
    /// </summary>
    /// <remarks>
    /// Envelope sender/recipients are stored separately because they are not always
    /// reconstructible from headers (Bcc, explicit <c>MAIL FROM</c>).
    /// Credentials and transport state are never stored.
    /// </remarks>
    internal static class EmailSpoolRecord
    {
        internal const string Magic = "VNNTP-SMTP-SPOOL/1";

        public static byte[] Serialize(EmailWorkItem item)
        {
            ArgumentNullException.ThrowIfNull(item);
            var builder = new StringBuilder(64 + item.EncodedMessage.Length);
            builder.Append(Magic).Append("\r\n");
            builder.Append("MAIL FROM:<").Append(item.EnvelopeSender.Address).Append(">\r\n");
            foreach (var recipient in item.Recipients)
            {
                builder.Append("RCPT TO:<").Append(recipient.Address).Append(">\r\n");
            }

            builder.Append("\r\n");
            var header = Encoding.ASCII.GetBytes(builder.ToString());
            var output = new byte[header.Length + item.EncodedMessage.Length];
            header.CopyTo(output, 0);
            item.EncodedMessage.Span.CopyTo(output.AsSpan(header.Length));
            return output;
        }

        public static bool TryParse(ReadOnlySpan<byte> data, out EmailWorkItem? item)
        {
            item = null;
            var text = Encoding.ASCII.GetString(data);
            var separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (separator < 0)
            {
                return false;
            }

            var preamble = text[..separator].Split("\r\n", StringSplitOptions.None);
            if (preamble.Length < 3 || !string.Equals(preamble[0], Magic, StringComparison.Ordinal))
            {
                return false;
            }

            if (!TryMailbox(preamble[1], "MAIL FROM:", out var sender))
            {
                return false;
            }

            var recipients = new List<EmailAddress>();
            for (var i = 2; i < preamble.Length; i++)
            {
                if (string.IsNullOrEmpty(preamble[i]))
                {
                    continue;
                }

                if (!TryMailbox(preamble[i], "RCPT TO:", out var recipient))
                {
                    return false;
                }

                recipients.Add(recipient);
            }

            if (recipients.Count == 0)
            {
                return false;
            }

            var message = data[(separator + 4)..].ToArray();
            item = new EmailWorkItem
            {
                EncodedMessage = message,
                EnvelopeSender = sender,
                Recipients = recipients,
            };
            return true;
        }

        private static bool TryMailbox(string line, string prefix, out EmailAddress address)
        {
            address = null!;
            if (!line.StartsWith(prefix, StringComparison.Ordinal)
                || line.Length < prefix.Length + 3
                || line[prefix.Length] != '<'
                || !line.EndsWith('>'))
            {
                return false;
            }

            var mailbox = line[(prefix.Length + 1)..^1];
            if (!EmailOptionsValidatorMailbox(mailbox))
            {
                return false;
            }

            address = new EmailAddress(mailbox);
            return true;
        }

        private static bool EmailOptionsValidatorMailbox(string mailbox) =>
            Configuration.EmailOptionsValidator.TryValidateMailbox(mailbox, out _);
    }
}
