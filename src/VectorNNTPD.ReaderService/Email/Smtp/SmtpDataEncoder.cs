namespace VectorNNTPD.ReaderService.Email.Smtp
{
    /// <summary>Applies RFC 5321 transparency (dot-stuffing) and the DATA terminator.</summary>
    internal static class SmtpDataEncoder
    {
        /// <summary>Writes <paramref name="message"/> with dot-stuffing and a final <c>CRLF.CRLF</c>.</summary>
        public static async Task WriteStuffedAsync(
            Stream stream,
            ReadOnlyMemory<byte> message,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stream);
            var stuffed = Stuff(message.Span);
            await stream.WriteAsync(stuffed, cancellationToken).ConfigureAwait(false);
        }

        internal static byte[] Stuff(ReadOnlySpan<byte> span)
        {
            if (span.Length == 0)
            {
                return ".\r\n"u8.ToArray();
            }

            var output = new List<byte>(span.Length + 8);
            var lineStart = true;
            var lastWasCr = false;
            var offset = 0;
            while (offset < span.Length)
            {
                var remaining = span[offset..];
                if (lineStart && remaining[0] == (byte)'.')
                {
                    output.Add((byte)'.');
                }

                var cr = remaining.IndexOf((byte)'\r');
                var lf = remaining.IndexOf((byte)'\n');
                int take;
                var nextLineStart = false;
                if (cr >= 0 && (lf < 0 || cr <= lf))
                {
                    take = cr + 1;
                    if (cr + 1 < remaining.Length && remaining[cr + 1] == (byte)'\n')
                    {
                        take++;
                        nextLineStart = true;
                    }

                    lastWasCr = remaining[cr] == (byte)'\r' && !nextLineStart;
                }
                else if (lf >= 0)
                {
                    take = lf + 1;
                    nextLineStart = true;
                    lastWasCr = false;
                }
                else
                {
                    take = remaining.Length;
                    lastWasCr = remaining[^1] == (byte)'\r';
                }

                output.AddRange(remaining[..take].ToArray());
                offset += take;
                lineStart = nextLineStart;
            }

            if (!lastWasCr && !(span.Length >= 2 && span[^2] == (byte)'\r' && span[^1] == (byte)'\n'))
            {
                output.Add((byte)'\r');
                output.Add((byte)'\n');
            }

            output.Add((byte)'.');
            output.Add((byte)'\r');
            output.Add((byte)'\n');
            return [.. output];
        }
    }
}
