namespace VectorNNTPD.ReaderService.Session.CommandProcessor
{
    /// <summary>
    /// Builds one owned wire buffer from static prefixes and dynamic protocol bytes.
    /// </summary>
    /// <remarks>
    /// The returned array is the caller's until it is passed to
    /// <see cref="NntpResponseWriter.WriteLineAsync(ReadOnlyMemory{byte}, CancellationToken)"/>
    /// or <see cref="NntpResponseWriter.EnqueueLineAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.
    /// The TX pump only reads it. Session scratch and Pipe memory must not be enqueued;
    /// those spans are copied into this buffer first.
    /// </remarks>
    internal static class NntpResponseCompose
    {
        /// <summary>Allocates <c>prefix + dynamic + suffix</c> as one owned array.</summary>
        internal static byte[] Concat(
            ReadOnlySpan<byte> prefix,
            ReadOnlySpan<byte> dynamic,
            ReadOnlySpan<byte> suffix)
        {
            var result = new byte[checked(prefix.Length + dynamic.Length + suffix.Length)];
            prefix.CopyTo(result);
            dynamic.CopyTo(result.AsSpan(prefix.Length));
            suffix.CopyTo(result.AsSpan(prefix.Length + dynamic.Length));
            return result;
        }

        /// <summary>Allocates the concatenation of <paramref name="parts"/> as one owned array.</summary>
        internal static byte[] Concatenate(ReadOnlySpan<ReadOnlyMemory<byte>> parts)
        {
            var total = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                total = checked(total + parts[i].Length);
            }

            var result = new byte[total];
            var offset = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Span;
                part.CopyTo(result.AsSpan(offset));
                offset += part.Length;
            }

            return result;
        }
    }
}
