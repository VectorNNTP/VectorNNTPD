using System.Runtime.CompilerServices;

namespace VectorNNTPD.Common.Articles.DateParser
{
    /// <content>
    /// Trailing timezone-abbreviation detection and numeric-offset substitution on byte buffers.
    /// </content>
    internal static partial class NewsDateParser
    {
        /// <summary>Returns whether <paramref name="value"/> is ASCII <c>A-Z</c> or <c>a-z</c>.</summary>
        /// <param name="value">Byte to test.</param>
        /// <returns><see langword="true"/> only for those 52 bytes.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsAsciiLetter(byte value) => (uint)((value | 0x20) - (byte)'a') <= 'z' - 'a';

        /// <summary>
        /// Finds a trailing timezone token: either <c>UTC+0</c>..<c>UTC+9</c> / <c>UTC-1</c>..<c>UTC-9</c>,
        /// or an ASCII letter run of length 2 through 8. Both forms require a preceding SP or HTAB.
        /// </summary>
        /// <param name="input">Date bytes.</param>
        /// <param name="abbrStart">Index of the first token byte on success; otherwise 0.</param>
        /// <param name="abbrLength">Token length on success; otherwise 0. The preceding whitespace is not included.</param>
        /// <returns><see langword="false"/> when neither form matches.</returns>
        private static bool TryGetTrailingAbbreviation(ReadOnlySpan<byte> input, out int abbrStart, out int abbrLength)
        {
            if (TryGetTrailingUtcNumericForm(input, out abbrStart, out abbrLength))
            {
                return true;
            }

            abbrStart = 0;
            abbrLength = 0;
            if (input.IsEmpty || !IsAsciiLetter(input[^1]))
            {
                return false;
            }

            var end = input.Length;
            var start = end - 1;
            while (start > 0 && IsAsciiLetter(input[start - 1]))
            {
                start--;
            }

            abbrLength = end - start;
            if (abbrLength is < 2 or > 8)
            {
                return false;
            }

            if (start == 0 || input[start - 1] is not ((byte)' ' or (byte)'\t'))
            {
                return false;
            }

            abbrStart = start;
            return true;
        }

        /// <summary>
        /// Finds a trailing <c>UTC+N</c> or <c>UTC-N</c> token of the documented single-digit forms.
        /// </summary>
        /// <param name="input">Date bytes.</param>
        /// <param name="tokenStart">Index of <c>U</c>/<c>u</c> on success; otherwise 0.</param>
        /// <param name="tokenLength">Always 5 on success; otherwise 0.</param>
        /// <returns>
        /// <see langword="true"/> for <c>UTC+0</c>..<c>UTC+9</c> and <c>UTC-1</c>..<c>UTC-9</c> (ASCII case-insensitive <c>UTC</c>),
        /// preceded by SP or HTAB. Rejects <c>UTC-0</c>, multi-digit forms, and missing signs or digits.
        /// </returns>
        private static bool TryGetTrailingUtcNumericForm(
            ReadOnlySpan<byte> input,
            out int tokenStart,
            out int tokenLength)
        {
            tokenStart = 0;
            tokenLength = 0;
            if (input.Length < 5)
            {
                return false;
            }

            var digit = input[^1];
            var sign = input[^2];
            if (sign == (byte)'+')
            {
                if (digit is < (byte)'0' or > (byte)'9')
                {
                    return false;
                }
            }
            else if (sign == (byte)'-')
            {
                if (digit is < (byte)'1' or > (byte)'9')
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            var start = input.Length - 5;
            if (!IsUtcPrefix(input.Slice(start, 3)))
            {
                return false;
            }

            if (start == 0 || input[start - 1] is not ((byte)' ' or (byte)'\t'))
            {
                return false;
            }

            tokenStart = start;
            tokenLength = 5;
            return true;
        }

        /// <summary>Returns whether <paramref name="value"/> is ASCII <c>UTC</c> with case folding.</summary>
        /// <param name="value">Three candidate bytes.</param>
        /// <returns><see langword="true"/> only for those three letters.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsUtcPrefix(ReadOnlySpan<byte> value)
            => value.Length == 3
               && ToLowerAscii(value[0]) == (byte)'u'
               && ToLowerAscii(value[1]) == (byte)'t'
               && ToLowerAscii(value[2]) == (byte)'c';

        /// <summary>
        /// Reports a trailing alphabetic abbreviation that is structurally valid and absent from <c>TimezoneMappings</c>.
        /// </summary>
        /// <param name="cleaned">Date bytes after parenthetical stripping and whitespace normalization.</param>
        /// <param name="abbreviationLength">Letter-run length when the method returns <see langword="true"/>; otherwise 0.</param>
        /// <returns>
        /// <see langword="false"/> when there is no trailing letter abbreviation or the abbreviation has a table offset.
        /// Documented <c>UTC±N</c> forms are known tokens and are not reported here. Malformed numeric UTC spellings are left for exact parse failure.
        /// </returns>
        private static bool TryGetUnknownTrailingAbbreviation(ReadOnlySpan<byte> cleaned, out int abbreviationLength)
        {
            abbreviationLength = 0;
            if (cleaned.IsEmpty || !IsAsciiLetter(cleaned[^1]))
            {
                return false;
            }

            if (!TryGetTrailingAbbreviation(cleaned, out var start, out var length))
            {
                return false;
            }

            if (TryFindTimezoneOffset(cleaned.Slice(start, length), out _))
            {
                return false;
            }

            abbreviationLength = length;
            return true;
        }

        /// <summary>
        /// Replaces a known trailing abbreviation or <c>UTC±N</c> token with its table offset when the replacement fits.
        /// </summary>
        /// <param name="buffer">Working date bytes. The offset is copied over the token, not over the preceding whitespace.</param>
        /// <param name="length">Current significant length.</param>
        /// <param name="maxLength">Maximum length the substituted text may occupy.</param>
        /// <returns>
        /// <paramref name="length"/> when there is no trailing token, it is unknown, or
        /// <c>start + offset.Length</c> exceeds <paramref name="maxLength"/>. Otherwise the index just after the copied offset.
        /// Bytes past the returned length are not cleared.
        /// </returns>
        private static int SubstituteTimezoneAbbreviation(Span<byte> buffer, int length, int maxLength)
        {
            if (!TryGetTrailingAbbreviation(buffer[..length], out var start, out var abbrLength))
            {
                return length;
            }

            if (!TryFindTimezoneOffset(buffer.Slice(start, abbrLength), out var offset))
            {
                return length;
            }

            if (start + offset.Length > maxLength)
            {
                return length;
            }

            offset.CopyTo(buffer[start..]);
            return start + offset.Length;
        }

        /// <summary>
        /// Looks up <paramref name="abbreviation"/> in <c>TimezoneMappings</c> using ASCII case folding.
        /// </summary>
        /// <param name="abbreviation">Trailing letter run, without the preceding whitespace.</param>
        /// <param name="offset">Offset bytes of the first match; otherwise empty.</param>
        /// <returns><see langword="true"/> when a table entry matches.</returns>
        private static bool TryFindTimezoneOffset(ReadOnlySpan<byte> abbreviation, out ReadOnlySpan<byte> offset)
        {
            var table = TimezoneMappings;
            for (var i = 0; i < table.Length; i++)
            {
                var entry = table[i];
                if (AsciiEqualsIgnoreCase(abbreviation, entry.Abbreviation))
                {
                    offset = entry.Offset;
                    return true;
                }
            }

            offset = default;
            return false;
        }

        /// <summary>
        /// Compares two byte spans after folding ASCII <c>A-Z</c> to <c>a-z</c>. Other bytes are compared unchanged.
        /// </summary>
        /// <param name="left">Left span.</param>
        /// <param name="right">Right span.</param>
        /// <returns><see langword="false"/> when the lengths differ or any folded byte differs.</returns>
        private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (var i = 0; i < left.Length; i++)
            {
                if (ToLowerAscii(left[i]) != ToLowerAscii(right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Folds ASCII <c>A-Z</c> to <c>a-z</c>. Every other byte is returned unchanged.</summary>
        /// <param name="value">Byte to fold.</param>
        /// <returns>The folded byte.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte ToLowerAscii(byte value)
            => (uint)(value - (byte)'A') <= 'Z' - 'A' ? (byte)(value + 32) : value;
    }
}
