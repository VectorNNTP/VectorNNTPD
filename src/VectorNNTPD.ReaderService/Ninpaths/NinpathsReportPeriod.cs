using System.Globalization;

namespace VectorNNTPD.ReaderService.Ninpaths
{
    /// <summary>
    /// Derives INN dump timestamps from a completed Path-survey file and <see cref="TimeProvider"/>.
    /// </summary>
    /// <remarks>
    /// INN <c>starttime</c> is process start and <c>endtime</c> is <c>time(NULL)</c> at dump.
    /// For a completed daily <c>inpaths-yyyyMMdd.log</c> the reporting period start is local
    /// midnight of that date. Articles are aggregated as a batch at dump time, so
    /// <c>avgtime = (atimes / total) + starttime</c> equals dump time (INN would add
    /// <c>time(0) - starttime</c> once per article during a same-moment batch).
    /// </remarks>
    internal static class NinpathsReportPeriod
    {
        /// <summary>
        /// Resolves <c>starttime</c>, <c>endtime</c>, and <c>avgtime</c> Unix seconds for a dump.
        /// </summary>
        public static (long StartUnix, long EndUnix, long AverageUnix) Resolve(
            string completedFilePath,
            TimeProvider time)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(completedFilePath);
            ArgumentNullException.ThrowIfNull(time);

            var now = time.GetUtcNow();
            var end = now.ToUnixTimeSeconds();
            if (!TryParseSurveyDate(completedFilePath, out var date))
            {
                return (end, end, end);
            }

            var unspecified = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
            var offset = time.LocalTimeZone.GetUtcOffset(unspecified);
            var start = new DateTimeOffset(unspecified, offset).ToUnixTimeSeconds();
            return (start, end, end);
        }

        /// <summary>Parses <c>inpaths-yyyyMMdd.log</c> from a file name or path.</summary>
        internal static bool TryParseSurveyDate(string completedFilePath, out DateOnly date)
        {
            date = default;
            var name = Path.GetFileName(completedFilePath.AsSpan());
            const string prefix = "inpaths-";
            const string suffix = ".log";
            if (name.Length != prefix.Length + 8 + suffix.Length)
            {
                return false;
            }

            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return DateOnly.TryParseExact(
                name.Slice(prefix.Length, 8),
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date);
        }
    }
}
