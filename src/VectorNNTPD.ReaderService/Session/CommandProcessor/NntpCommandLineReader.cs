using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace VectorNNTPD.ReaderService.Session.CommandProcessor
{
    /// <summary>Reads CRLF-delimited NNTP command lines from a <see cref="PipeReader"/>.</summary>
    public static class NntpCommandLineReader
    {
        /// <summary>Maximum command-line content before CRLF. Longer lines are rejected, not parsed.</summary>
        public const int MaxCommandLineBytes = 2048;

        /// <summary>
        /// <see cref="ReadLineBytesAsync"/> result when the line before CRLF exceeds the destination
        /// scratch. The line has been consumed. <c>-1</c> remains EOF.
        /// </summary>
        public const int OverlongLine = -2;

        private static readonly byte[] Crlf = "\r\n"u8.ToArray();

        /// <summary>
        /// Reads the next command line (without trailing CRLF). Returns <see langword="null"/> on EOF.
        /// </summary>
        public static async ValueTask<string?> ReadLineAsync(
            PipeReader reader,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(reader);

            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                if (TryReadLine(ref buffer, out var line))
                {
                    reader.AdvanceTo(buffer.Start, buffer.Start);
                    return line;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    return null;
                }
            }
        }

        private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out string? line)
        {
            var reader = new SequenceReader<byte>(buffer);
            if (!reader.TryReadTo(out ReadOnlySequence<byte> lineBytes, Crlf))
            {
                line = null;
                return false;
            }

            line = Encoding.ASCII.GetString(lineBytes);
            buffer = buffer.Slice(reader.Position);
            return true;
        }

        /// <summary>
        /// Reads the next command line into <paramref name="scratch"/> (without CRLF).
        /// Returns the number of bytes written, or <c>-1</c> on EOF.
        /// </summary>
        public static async ValueTask<int> ReadLineBytesAsync(
            PipeReader reader,
            byte[] scratch,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(reader);
            ArgumentNullException.ThrowIfNull(scratch);

            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                if (TryCopyLine(ref buffer, scratch, out var length))
                {
                    reader.AdvanceTo(buffer.Start, buffer.Start);
                    return length;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    return -1;
                }
            }
        }

        private static bool TryCopyLine(ref ReadOnlySequence<byte> buffer, byte[] scratch, out int length)
        {
            var reader = new SequenceReader<byte>(buffer);
            if (!reader.TryReadTo(out ReadOnlySequence<byte> lineBytes, Crlf))
            {
                length = 0;
                return false;
            }

            buffer = buffer.Slice(reader.Position);
            if (lineBytes.Length > scratch.Length)
            {
                length = OverlongLine;
                return true;
            }

            length = checked((int)lineBytes.Length);
            lineBytes.CopyTo(scratch);
            return true;
        }
    }
}
