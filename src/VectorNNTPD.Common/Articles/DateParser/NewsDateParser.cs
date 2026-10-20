using System.Globalization;

namespace VectorNNTPD.Common.Articles.DateParser
{
    /// <summary>
    /// Parses raw article date-header values into a canonical UTC instant without heap strings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Input is inspected as bytes. The BCL <see cref="DateTimeOffset"/> parsers require UTF-16,
    /// so accepted printable-ASCII bytes are copied into a stackalloc char buffer. That copy is
    /// not <c>Encoding.GetString</c> and does not allocate.
    /// </para>
    /// <para>
    /// RFC 5322 allows second <c>60</c> for a leap second (time-of-day through <c>23:59:60</c>).
    /// The BCL rejects that value, so a matching <c>:59:60</c> is rewritten to <c>:59:00</c>,
    /// parsed, then advanced by one minute to the following minute's second <c>00</c>.
    /// Minute <c>60</c> and second greater than <c>60</c> are not leap seconds and stay rejected.
    /// </para>
    /// </remarks>
    internal static partial class NewsDateParser
    {
        /// <summary>
        /// <see cref="DateTimeStyles.AllowWhiteSpaces"/>, <see cref="DateTimeStyles.AssumeUniversal"/>, and <see cref="DateTimeStyles.AdjustToUniversal"/>.
        /// Offset-less values are treated as UTC and the parsed result is UTC.
        /// </summary>
        private const DateTimeStyles ParseStyles =
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        /// <summary>
        /// Invariant format written by <see cref="TryFormatCanonicalRfc5322Utc"/> before the literal ASCII suffix <c> +0000</c>.
        /// </summary>
        private const string CanonicalFormat = "ddd, dd MMM yyyy HH:mm:ss";

        /// <summary>
        /// Exact invariant-culture patterns passed to <see cref="DateTimeOffset.TryParseExact(ReadOnlySpan{char}, string[], IFormatProvider, DateTimeStyles, out DateTimeOffset)"/>
        /// after abbreviation substitution. <see cref="TryQuickParse"/> may accept a value before this list is used.
        /// </summary>
        private static readonly string[] DateFormats =
        [
            "ddd, dd MMM yyyy HH:mm:ss zzz",
            "ddd, d MMM yyyy HH:mm:ss zzz",
            "ddd, dd MMM yyyy H:mm:ss zzz",
            "ddd, d MMM yyyy H:mm:ss zzz",
            "ddd, dd MMM yyyy HH:mm:ss",
            "ddd, d MMM yyyy HH:mm:ss",
            "ddd, dd MMM yyyy H:mm:ss",
            "ddd, d MMM yyyy H:mm:ss",
            "dd MMM yyyy HH:mm:ss zzz",
            "d MMM yyyy HH:mm:ss zzz",
            "dd MMM yyyy H:mm:ss zzz",
            "d MMM yyyy H:mm:ss zzz",
            "dd MMM yyyy HH:mm:ss",
            "d MMM yyyy HH:mm:ss",
            "dd MMM yyyy H:mm:ss",
            "d MMM yyyy H:mm:ss",
            "yyyy-MM-dd HH:mm:ss zzz",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd H:mm:ss",
            "yyyy-MM-ddTHH:mm:ss zzz",
            "yyyy-MM-ddTHH:mm:sszzz",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy/MM/dd H:mm:ss",
            "MM/dd/yyyy HH:mm:ss",
            "MM/dd/yyyy H:mm:ss",
            "dd/MM/yyyy HH:mm:ss",
            "dd/MM/yyyy H:mm:ss",
            "dd MMM yyyy",
            "d MMM yyyy",
            "yyyy-MM-dd",
            "dd/MM/yyyy",
            "MM/dd/yyyy",
            "ddd, dd MMM yy HH:mm:ss zzz",
            "ddd, d MMM yy HH:mm:ss zzz",
            "ddd, dd MMM yy H:mm:ss zzz",
            "ddd, d MMM yy H:mm:ss zzz",
            "ddd, dd MMM yy HH:mm:ss",
            "ddd, d MMM yy HH:mm:ss",
            "ddd, dd MMM yy H:mm:ss",
            "ddd, d MMM yy H:mm:ss",
            "dd MMM yy HH:mm:ss zzz",
            "d MMM yy HH:mm:ss zzz",
            "dd MMM yy H:mm:ss zzz",
            "d MMM yy H:mm:ss zzz",
            "dd MMM yy HH:mm:ss",
            "d MMM yy HH:mm:ss",
            "dd MMM yy H:mm:ss",
            "d MMM yy H:mm:ss",
        ];

        /// <summary>
        /// Tries to parse one NNTP date value using the repository default options.
        /// </summary>
        /// <param name="input">Raw date-header value bytes.</param>
        /// <param name="utc">Canonical UTC instant when parsing succeeds.</param>
        /// <param name="failure">Failure reason when parsing fails.</param>
        /// <returns><see langword="true"/> when <paramref name="input"/> produced a UTC instant.</returns>
        internal static bool TryGetCanonicalUtc(ReadOnlySpan<byte> input, out DateTime utc, out DateParseFailureReason failure)
            => TryGetCanonicalUtc(input, DateParseOptions.Default, out utc, out failure);

        /// <summary>
        /// Tries to parse one NNTP date value using explicit normalization options.
        /// </summary>
        /// <param name="input">Raw candidate date value bytes.</param>
        /// <param name="options">Guardrails and normalization toggles applied before exact parsing.</param>
        /// <param name="utc">Canonical UTC instant when parsing succeeds.</param>
        /// <param name="failure">Failure reason describing the stage that rejected <paramref name="input"/>.</param>
        /// <returns><see langword="true"/> when parsing succeeds.</returns>
        internal static bool TryGetCanonicalUtc(
            ReadOnlySpan<byte> input,
            DateParseOptions options,
            out DateTime utc,
            out DateParseFailureReason failure)
        {
            utc = default;
            failure = DateParseFailureReason.None;

            var trimmed = TrimAsciiWhitespace(input);
            if (trimmed.IsEmpty)
            {
                failure = DateParseFailureReason.Empty;
                return false;
            }

            if (trimmed.Length > options.MaxInputLength)
            {
                failure = DateParseFailureReason.TooLong;
                return false;
            }

            if (!PrintableAsciiSimd.IsAllPrintableAscii(trimmed))
            {
                failure = DateParseFailureReason.NonPrintableAscii;
                return false;
            }

            Span<char> parseChars = stackalloc char[options.MaxInputLength];
            CopyAsciiBytesToChars(trimmed, parseChars);
            if (TryQuickParse(parseChars[..trimmed.Length], out utc))
            {
                return true;
            }

            Span<byte> working = stackalloc byte[options.MaxInputLength];
            trimmed.CopyTo(working);
            var workingLength = trimmed.Length;
            workingLength = StripTrailingParenthetical(working, workingLength);
            workingLength = TrimAsciiWhitespaceInPlace(working, workingLength);

            if (options.NormalizeInteriorWhitespace)
            {
                workingLength = CollapseInteriorSpaces(working, workingLength);
            }

            if (options.RequireKnownTimezoneAbbreviation
                && TryGetUnknownTrailingAbbreviation(working[..workingLength], out _))
            {
                failure = DateParseFailureReason.UnknownTimezoneAbbreviation;
                return false;
            }

            workingLength = SubstituteTimezoneAbbreviation(working, workingLength, options.MaxInputLength);
            CopyAsciiBytesToChars(working[..workingLength], parseChars);
            if (TryExactParse(parseChars[..workingLength], out utc))
            {
                return true;
            }

            if (TryRewriteLeapSecondSixty(working[..workingLength]))
            {
                CopyAsciiBytesToChars(working[..workingLength], parseChars);
                // Numeric ±hhmm offsets often succeed only on TryQuickParse; abbreviations are already ±hh:mm.
                if (TryExactParse(parseChars[..workingLength], out utc)
                    || TryQuickParse(parseChars[..workingLength], out utc))
                {
                    utc = utc.AddMinutes(1);
                    return true;
                }
            }

            failure = DateParseFailureReason.ParseFailed;
            return false;
        }

        /// <summary>
        /// When the time-of-day contains a leap-second <c>:59:60</c> with hour <c>00</c>–<c>23</c>,
        /// rewrites the seconds field to <c>00</c> in place so BCL parsing can accept the value.
        /// </summary>
        /// <param name="buffer">Normalized date bytes after timezone abbreviation substitution.</param>
        /// <returns>
        /// <see langword="true"/> when <c>:59:60</c> was rewritten to <c>:59:00</c>.
        /// The caller must add one minute after a successful parse.
        /// </returns>
        /// <remarks>
        /// Does not rewrite minute <c>60</c> (<c>23:60:00</c>) or second values greater than <c>60</c>.
        /// </remarks>
        private static bool TryRewriteLeapSecondSixty(Span<byte> buffer)
        {
            // Minimum shape: one hour digit + ":59:60".
            ReadOnlySpan<byte> marker = ":59:60"u8;
            if (buffer.Length < marker.Length + 1)
            {
                return false;
            }

            var idx = buffer.LastIndexOf(marker);
            if (idx < 0)
            {
                return false;
            }

            var hourEnd = idx;
            if (hourEnd == 0 || !IsAsciiDigit(buffer[hourEnd - 1]))
            {
                return false;
            }

            var hourStart = hourEnd - 1;
            if (hourStart > 0 && IsAsciiDigit(buffer[hourStart - 1]))
            {
                hourStart--;
            }

            // Reject three or more digits before ":59:60" (not an hour field).
            if (hourStart > 0 && IsAsciiDigit(buffer[hourStart - 1]))
            {
                return false;
            }

            if (hourStart > 0)
            {
                var previous = buffer[hourStart - 1];
                if (previous is not ((byte)' ' or (byte)'\t' or (byte)'T' or (byte)'t'))
                {
                    return false;
                }
            }

            var hour = buffer[hourStart] - (byte)'0';
            if (hourEnd - hourStart == 2)
            {
                hour = (hour * 10) + (buffer[hourStart + 1] - (byte)'0');
            }

            if (hour is < 0 or > 23)
            {
                return false;
            }

            var after = idx + marker.Length;
            if (after < buffer.Length)
            {
                var next = buffer[after];
                if (next is not ((byte)' ' or (byte)'\t' or (byte)'+' or (byte)'-')
                    && !IsAsciiLetter(next))
                {
                    return false;
                }
            }

            buffer[idx + 4] = (byte)'0';
            buffer[idx + 5] = (byte)'0';
            return true;
        }

        /// <summary>Returns whether <paramref name="value"/> is an ASCII decimal digit.</summary>
        /// <param name="value">Byte to test.</param>
        /// <returns><see langword="true"/> for <c>0x30</c>–<c>0x39</c>.</returns>
        private static bool IsAsciiDigit(byte value) => (uint)(value - (byte)'0') <= 9;

        /// <summary>
        /// Writes the canonical UTC RFC 5322 form <c>ddd, dd MMM yyyy HH:mm:ss +0000</c> as ASCII bytes.
        /// </summary>
        /// <param name="utc">Date-time value interpreted as UTC output.</param>
        /// <param name="destination">Destination receiving ASCII bytes.</param>
        /// <param name="bytesWritten">Bytes written on success.</param>
        /// <returns><see langword="true"/> when <paramref name="destination"/> was large enough.</returns>
        internal static bool TryFormatCanonicalRfc5322Utc(DateTime utc, Span<byte> destination, out int bytesWritten)
        {
            utc = NormalizeUtcKind(utc);
            Span<char> chars = stackalloc char[32];
            if (!utc.TryFormat(chars, out var charCount, CanonicalFormat, CultureInfo.InvariantCulture))
            {
                bytesWritten = 0;
                return false;
            }

            const string Suffix = " +0000";
            var total = charCount + Suffix.Length;
            if (destination.Length < total)
            {
                bytesWritten = 0;
                return false;
            }

            for (var i = 0; i < charCount; i++)
            {
                destination[i] = (byte)chars[i];
            }

            for (var i = 0; i < Suffix.Length; i++)
            {
                destination[charCount + i] = (byte)Suffix[i];
            }

            bytesWritten = total;
            return true;
        }

        /// <summary>
        /// Normalizes <paramref name="utc"/> so local values convert to UTC and unspecified values are treated as UTC.
        /// </summary>
        /// <param name="utc">Input instant.</param>
        /// <returns>UTC <see cref="DateTime"/>.</returns>
        private static DateTime NormalizeUtcKind(DateTime utc)
        {
            if (utc.Kind == DateTimeKind.Local)
            {
                return utc.ToUniversalTime();
            }

            return utc.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(utc, DateTimeKind.Utc)
                : utc;
        }

        /// <summary>
        /// Tries <see cref="DateTimeOffset.TryParse(ReadOnlySpan{char}, IFormatProvider, DateTimeStyles, out DateTimeOffset)"/> with <see cref="ParseStyles"/>.
        /// </summary>
        /// <param name="input">Printable-ASCII date text already copied to UTF-16.</param>
        /// <param name="result"><see cref="DateTimeOffset.UtcDateTime"/> on success; otherwise <see langword="default"/>.</param>
        /// <returns><see langword="true"/> when the BCL parser accepts <paramref name="input"/>.</returns>
        private static bool TryQuickParse(ReadOnlySpan<char> input, out DateTime result)
        {
            if (DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture, ParseStyles, out var dto))
            {
                result = dto.UtcDateTime;
                return true;
            }

            result = default;
            return false;
        }

        /// <summary>
        /// Tries <see cref="DateFormats"/> with <see cref="ParseStyles"/> after timezone substitution.
        /// </summary>
        /// <param name="input">Normalized date text.</param>
        /// <param name="result"><see cref="DateTimeOffset.UtcDateTime"/> on success; otherwise <see langword="default"/>.</param>
        /// <returns><see langword="true"/> when one pattern matches.</returns>
        private static bool TryExactParse(ReadOnlySpan<char> input, out DateTime result)
        {
            if (DateTimeOffset.TryParseExact(input, DateFormats, CultureInfo.InvariantCulture, ParseStyles, out var dto))
            {
                result = dto.UtcDateTime;
                return true;
            }

            result = default;
            return false;
        }

        /// <summary>
        /// Drops leading and trailing SP and HTAB. CR and LF are not whitespace here.
        /// </summary>
        /// <param name="value">Raw date bytes.</param>
        /// <returns>A slice of <paramref name="value"/>, not a copy.</returns>
        private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> value)
        {
            var start = 0;
            var end = value.Length;
            while (start < end && IsAsciiWhitespace(value[start]))
            {
                start++;
            }

            while (end > start && IsAsciiWhitespace(value[end - 1]))
            {
                end--;
            }

            return value[start..end];
        }

        /// <summary>
        /// Moves the SP/HTAB-trimmed prefix of <paramref name="buffer"/> to index 0.
        /// </summary>
        /// <param name="buffer">Working date bytes. Bytes past the returned length are left unchanged.</param>
        /// <param name="length">Count of significant bytes currently in <paramref name="buffer"/>.</param>
        /// <returns>Length after leading and trailing SP and HTAB are removed.</returns>
        private static int TrimAsciiWhitespaceInPlace(Span<byte> buffer, int length)
        {
            var start = 0;
            var end = length;
            while (start < end && IsAsciiWhitespace(buffer[start]))
            {
                start++;
            }

            while (end > start && IsAsciiWhitespace(buffer[end - 1]))
            {
                end--;
            }

            var remaining = end - start;
            if (start > 0 && remaining > 0)
            {
                buffer.Slice(start, remaining).CopyTo(buffer);
            }

            return remaining;
        }

        /// <summary>Returns whether <paramref name="value"/> is SP or HTAB.</summary>
        /// <param name="value">Byte to test.</param>
        /// <returns><see langword="true"/> for <c>0x20</c> and <c>0x09</c> only.</returns>
        private static bool IsAsciiWhitespace(byte value) => value is (byte)' ' or (byte)'\t';

        /// <summary>
        /// Zero-extends each ASCII byte into <paramref name="destination"/>. Does not transcode and does not check destination length.
        /// </summary>
        /// <param name="source">Bytes already restricted to printable ASCII by the caller.</param>
        /// <param name="destination">UTF-16 buffer at least as long as <paramref name="source"/>.</param>
        private static void CopyAsciiBytesToChars(ReadOnlySpan<byte> source, Span<char> destination)
        {
            for (var i = 0; i < source.Length; i++)
            {
                destination[i] = (char)source[i];
            }
        }

        /// <summary>
        /// Collapses consecutive SP (<c>0x20</c>) to a single SP. HTAB is preserved.
        /// </summary>
        /// <param name="buffer">Working date bytes, overwritten from the start.</param>
        /// <param name="length">Count of significant bytes.</param>
        /// <returns>Compacted length. Bytes past that length are left unchanged.</returns>
        private static int CollapseInteriorSpaces(Span<byte> buffer, int length)
        {
            var write = 0;
            var lastWasSpace = false;
            for (var i = 0; i < length; i++)
            {
                var b = buffer[i];
                if (b == (byte)' ')
                {
                    if (lastWasSpace)
                    {
                        continue;
                    }

                    lastWasSpace = true;
                }
                else
                {
                    lastWasSpace = false;
                }

                buffer[write++] = b;
            }

            return write;
        }

        /// <summary>
        /// Drops one trailing parenthetical comment and the SP/HTAB before its <c>(</c>.
        /// </summary>
        /// <param name="buffer">Working date bytes. Not rewritten; only the returned length changes.</param>
        /// <param name="length">Count of significant bytes.</param>
        /// <returns>
        /// Index of the last byte kept, after trailing SP/HTAB before <c>(</c> is excluded.
        /// Returns <paramref name="length"/> when the trimmed text does not end in <c>)</c>,
        /// when a <c>)</c> appears before the matching <c>(</c>, or when no <c>(</c> exists.
        /// Nested comments are left intact.
        /// </returns>
        private static int StripTrailingParenthetical(Span<byte> buffer, int length)
        {
            var end = length;
            while (end > 0 && IsAsciiWhitespace(buffer[end - 1]))
            {
                end--;
            }

            if (end == 0 || buffer[end - 1] != (byte)')')
            {
                return length;
            }

            var open = -1;
            for (var i = end - 2; i >= 0; i--)
            {
                if (buffer[i] == (byte)'(')
                {
                    open = i;
                    break;
                }

                if (buffer[i] == (byte)')')
                {
                    return length;
                }
            }

            if (open < 0)
            {
                return length;
            }

            var prefixEnd = open;
            while (prefixEnd > 0 && IsAsciiWhitespace(buffer[prefixEnd - 1]))
            {
                prefixEnd--;
            }

            return prefixEnd;
        }
    }
}
