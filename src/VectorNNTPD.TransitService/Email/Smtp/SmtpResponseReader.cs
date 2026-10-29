using System.Buffers;
using System.Text;

namespace VectorNNTPD.TransitService.Email.Smtp
{
    /// <summary>Reads RFC 5321 multiline SMTP replies from a stream.</summary>
    internal sealed class SmtpResponseReader
    {
        internal const int MaxLineLength = 512;
        internal const int MaxReplyLines = 64;

        private readonly Stream _stream;
        private readonly byte[] _buffer;
        private int _buffered;
        private int _consumed;

        public SmtpResponseReader(Stream stream, int bufferSize = 1024)
        {
            ArgumentNullException.ThrowIfNull(stream);
            _stream = stream;
            _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        }

        public async Task<SmtpResponse> ReadAsync(CancellationToken cancellationToken)
        {
            var lines = new List<string>(4);
            int? code = null;
            while (true)
            {
                var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (!TryParseReplyLine(line.Span, out var lineCode, out var continuation, out var text))
                {
                    throw new SmtpException(SmtpFailureKind.Protocol, "Malformed SMTP reply line.");
                }

                if (code is null)
                {
                    code = lineCode;
                }
                else if (code.Value != lineCode)
                {
                    throw new SmtpException(SmtpFailureKind.Protocol, "SMTP reply code changed during continuation.");
                }

                lines.Add(text);
                if (lines.Count > MaxReplyLines)
                {
                    throw new SmtpException(SmtpFailureKind.Protocol, "SMTP reply exceeded the maximum number of lines.");
                }

                if (!continuation)
                {
                    var last = lines[^1];
                    TrySplitEnhanced(last, out var enhanced, out var remainder);
                    var joined = string.Join("\n", lines);
                    return new SmtpResponse(code.Value, lines, enhanced, joined);
                }
            }
        }

        internal static bool TryParseReplyLine(
            ReadOnlySpan<byte> line,
            out int code,
            out bool continuation,
            out string text)
        {
            code = 0;
            continuation = false;
            text = string.Empty;
            if (line.Length < 3)
            {
                return false;
            }

            for (var i = 0; i < 3; i++)
            {
                var digit = line[i];
                if (digit is < (byte)'0' or > (byte)'9')
                {
                    return false;
                }

                code = (code * 10) + (digit - (byte)'0');
            }

            if (line.Length == 3)
            {
                return true;
            }

            var separator = line[3];
            if (separator == (byte)'-')
            {
                continuation = true;
            }
            else if (separator != (byte)' ')
            {
                return false;
            }

            text = line.Length > 4
                ? Encoding.ASCII.GetString(line[4..])
                : string.Empty;
            return true;
        }

        internal static bool TrySplitEnhanced(string text, out string? enhanced, out string remainder)
        {
            enhanced = null;
            remainder = text;
            var span = text.AsSpan().TrimStart();
            if (span.Length < 5)
            {
                return false;
            }

            var classDigit = span[0];
            if (classDigit is < '2' or > '5' || span[1] != '.')
            {
                return false;
            }

            var end = 2;
            while (end < span.Length && (char.IsAsciiDigit(span[end]) || span[end] == '.'))
            {
                end++;
            }

            if (end >= span.Length || span[end] != ' ')
            {
                return false;
            }

            enhanced = span[..end].ToString();
            remainder = span[(end + 1)..].ToString();
            return true;
        }

        private async Task<ReadOnlyMemory<byte>> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new ArrayBufferWriter<byte>(64);
            while (true)
            {
                if (_consumed >= _buffered)
                {
                    _consumed = 0;
                    _buffered = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                    if (_buffered == 0)
                    {
                        throw new SmtpException(SmtpFailureKind.Network, "SMTP server closed the connection.");
                    }
                }

                var span = _buffer.AsSpan(_consumed, _buffered - _consumed);
                var cr = span.IndexOf((byte)'\r');
                var lf = span.IndexOf((byte)'\n');
                if (cr >= 0 && (lf < 0 || cr < lf))
                {
                    Append(line, span[..cr]);
                    _consumed += cr + 1;
                    if (_consumed < _buffered && _buffer[_consumed] == (byte)'\n')
                    {
                        _consumed++;
                    }

                    return FinishLine(line);
                }

                if (lf >= 0)
                {
                    Append(line, span[..lf]);
                    _consumed += lf + 1;
                    return FinishLine(line);
                }

                Append(line, span);
                _consumed = _buffered;
            }
        }

        private static void Append(ArrayBufferWriter<byte> line, ReadOnlySpan<byte> span)
        {
            if (line.WrittenCount + span.Length > MaxLineLength)
            {
                throw new SmtpException(SmtpFailureKind.Protocol, "SMTP reply line exceeded 512 octets.");
            }

            line.Write(span);
        }

        private static ReadOnlyMemory<byte> FinishLine(ArrayBufferWriter<byte> line) =>
            line.WrittenMemory.ToArray();

        public void ReturnBuffer()
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }
    }
}
