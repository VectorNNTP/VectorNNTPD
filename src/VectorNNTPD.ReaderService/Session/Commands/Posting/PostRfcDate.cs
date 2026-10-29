using System.Globalization;
using System.Text;

namespace VectorNNTPD.ReaderService.Session.Commands.Posting
{
    /// <summary>RFC 5322 / Netnews date-time format used for POST <c>Date:</c> and injection timestamps.</summary>
    internal static class PostRfcDate
    {
        private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
        private static readonly string[] MonthNames =
            ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

        /// <summary>Formats <paramref name="utc"/> as <c>Wed, 25 Sep 2026 10:49:00 +0000</c>.</summary>
        public static string Format(DateTimeOffset utc)
        {
            var value = utc.ToUniversalTime();
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{DayNames[(int)value.DayOfWeek]}, {value.Day:00} {MonthNames[value.Month - 1]} {value.Year:0000} {value.Hour:00}:{value.Minute:00}:{value.Second:00} +0000");
        }

        /// <summary>Parses an RFC-compatible date (numeric zone, GMT, UT, or UTC).</summary>
        public static bool TryParse(ReadOnlySpan<byte> value, out DateTimeOffset date)
        {
            date = default;
            if (value.IsEmpty || value.Length > 128)
            {
                return false;
            }

            Span<char> chars = stackalloc char[value.Length];
            if (Encoding.ASCII.GetChars(value, chars) != value.Length)
            {
                return false;
            }

            return TryParse(chars, out date);
        }

        /// <summary>Parses an RFC-compatible date from already-decoded ASCII.</summary>
        public static bool TryParse(ReadOnlySpan<char> value, out DateTimeOffset date)
        {
            date = default;
            var span = value.Trim();
            if (span.IsEmpty)
            {
                return false;
            }

            if (span.Length >= 5 && span[3] == ',')
            {
                if (!IsDayName(span[..3]))
                {
                    return false;
                }

                span = span[4..].TrimStart();
            }

            if (!TryReadNumber(ref span, minDigits: 1, maxDigits: 2, out var day) || day is < 1 or > 31)
            {
                return false;
            }

            if (!TrySkipWsp(ref span) || !TryReadMonth(ref span, out var month))
            {
                return false;
            }

            if (!TrySkipWsp(ref span) || !TryReadNumber(ref span, minDigits: 4, maxDigits: 4, out var year)
                || year < 1900)
            {
                return false;
            }

            if (!TrySkipWsp(ref span) || !TryReadNumber(ref span, minDigits: 2, maxDigits: 2, out var hour)
                || hour > 23)
            {
                return false;
            }

            if (span.IsEmpty || span[0] != ':')
            {
                return false;
            }

            span = span[1..];
            if (!TryReadNumber(ref span, minDigits: 2, maxDigits: 2, out var minute) || minute > 59)
            {
                return false;
            }

            var second = 0;
            if (!span.IsEmpty && span[0] == ':')
            {
                span = span[1..];
                if (!TryReadNumber(ref span, minDigits: 2, maxDigits: 2, out second) || second > 60)
                {
                    return false;
                }
            }

            if (!TrySkipWsp(ref span) || !TryReadZone(ref span, out var offset))
            {
                return false;
            }

            if (!span.IsEmpty)
            {
                return false;
            }

            try
            {
                date = new DateTimeOffset(year, month, day, hour, minute, Math.Min(second, 59), offset);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        /// <summary>Returns whether <paramref name="date"/> is inside the POST date window relative to <paramref name="nowUtc"/>.</summary>
        public static bool IsWithinPolicy(DateTimeOffset date, DateTimeOffset nowUtc)
        {
            var utc = date.ToUniversalTime();
            var now = nowUtc.ToUniversalTime();
            if (utc > now + PostingLimits.MaxDateSkewFuture)
            {
                return false;
            }

            return utc >= now - PostingLimits.MaxDateAge;
        }

        private static bool IsDayName(ReadOnlySpan<char> value)
        {
            foreach (var name in DayNames)
            {
                if (value.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadMonth(ref ReadOnlySpan<char> span, out int month)
        {
            month = 0;
            if (span.Length < 3)
            {
                return false;
            }

            var token = span[..3];
            for (var i = 0; i < MonthNames.Length; i++)
            {
                if (token.Equals(MonthNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    month = i + 1;
                    span = span[3..];
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadZone(ref ReadOnlySpan<char> span, out TimeSpan offset)
        {
            offset = TimeSpan.Zero;
            if (span.IsEmpty)
            {
                return false;
            }

            if (span.StartsWith("GMT", StringComparison.OrdinalIgnoreCase)
                || span.StartsWith("UTC", StringComparison.OrdinalIgnoreCase))
            {
                span = span[3..];
                return true;
            }

            if (span.StartsWith("UT", StringComparison.OrdinalIgnoreCase)
                && (span.Length == 2 || !char.IsAsciiLetter(span[2])))
            {
                span = span[2..];
                return true;
            }

            var sign = span[0];
            if (sign is not ('+' or '-') || span.Length < 5)
            {
                return false;
            }

            if (!char.IsAsciiDigit(span[1]) || !char.IsAsciiDigit(span[2])
                || !char.IsAsciiDigit(span[3]) || !char.IsAsciiDigit(span[4]))
            {
                return false;
            }

            var hours = ((span[1] - '0') * 10) + (span[2] - '0');
            var minutes = ((span[3] - '0') * 10) + (span[4] - '0');
            if (hours > 14 || minutes > 59)
            {
                return false;
            }

            var ticks = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);
            offset = sign == '-' ? -ticks : ticks;
            span = span[5..];
            return true;
        }

        private static bool TryReadNumber(ref ReadOnlySpan<char> span, int minDigits, int maxDigits, out int value)
        {
            value = 0;
            var taken = 0;
            while (taken < maxDigits && taken < span.Length && char.IsAsciiDigit(span[taken]))
            {
                value = (value * 10) + (span[taken] - '0');
                taken++;
            }

            if (taken < minDigits)
            {
                return false;
            }

            span = span[taken..];
            return true;
        }

        private static bool TrySkipWsp(ref ReadOnlySpan<char> span)
        {
            var i = 0;
            while (i < span.Length && (span[i] == ' ' || span[i] == '\t'))
            {
                i++;
            }

            if (i == 0)
            {
                return false;
            }

            span = span[i..];
            return true;
        }
    }
}
