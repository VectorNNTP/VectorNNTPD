using System.Text;
using VectorNNTPD.Common.Articles.DateParser;

namespace VectorNNTPD.Common.Tests.Articles.DateParser
{
    /// <summary>
    /// <see cref="NewsDateParser.TryGetCanonicalUtc(ReadOnlySpan{byte}, out DateTime, out DateParseFailureReason)"/>
    /// accepts numeric offsets, GMT, and trailing comments, and reports empty, non-printable, unknown-zone, and too-long failures.
    /// </summary>
    public sealed class NewsDateParserTests
    {
        /// <summary>
        /// A date with numeric offset <c>+0200</c> is accepted with <see cref="DateParseFailureReason.None"/>
        /// and converted to 05:30:10 UTC, which formats as <c>Fri, 23 Aug 2024 05:30:10 +0000</c>.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenNumericOffsetPresent_ReturnsUtcWithoutFallback()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Fri, 23 Aug 2024 07:30:10 +0200"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2024, 8, 23, 5, 30, 10, DateTimeKind.Utc), utc);
            Assert.Equal("Fri, 23 Aug 2024 05:30:10 +0000", Format(utc));
        }

        /// <summary>
        /// The <c>GMT</c> abbreviation is accepted as a zero offset and yields the same UTC instant as the clock time in the input.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenGmtAbbreviationPresent_SubstitutesOffset()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Fri, 23 Aug 2024 05:30:10 GMT"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2024, 8, 23, 5, 30, 10, DateTimeKind.Utc), utc);
        }

        /// <summary>
        /// A trailing parenthetical comment such as <c>(UTC)</c> is ignored and does not change the parsed UTC instant.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenTrailingCommentPresent_StripsParenthetical()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Fri, 23 Aug 2024 05:30:10 +0000 (UTC)"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2024, 8, 23, 5, 30, 10, DateTimeKind.Utc), utc);
        }

        /// <summary>
        /// Whitespace-only input is rejected with <see cref="DateParseFailureReason.Empty"/>.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenEmpty_ReturnsEmptyFailure()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc("   "u8, out _, out var failure);
            Assert.False(accepted);
            Assert.Equal(DateParseFailureReason.Empty, failure);
        }

        /// <summary>
        /// An embedded NUL is rejected with <see cref="DateParseFailureReason.NonPrintableAscii"/>.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenNonPrintable_ReturnsNonPrintableFailure()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc("Fri, 23 Aug 2024 05:30:10 +\0"u8, out _, out var failure);
            Assert.False(accepted);
            Assert.Equal(DateParseFailureReason.NonPrintableAscii, failure);
        }

        /// <summary>
        /// With <see cref="DateParseOptions.RequireKnownTimezoneAbbreviation"/> set, an unrecognized zone abbreviation is rejected
        /// with <see cref="DateParseFailureReason.UnknownTimezoneAbbreviation"/>.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenUnknownAbbreviationRequired_Rejects()
        {
            var options = DateParseOptions.Default with { RequireKnownTimezoneAbbreviation = true };
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Fri, 23 Aug 2024 05:30:10 ZZQX"u8,
                options,
                out _,
                out var failure);

            Assert.False(accepted);
            Assert.Equal(DateParseFailureReason.UnknownTimezoneAbbreviation, failure);
        }

        /// <summary>
        /// Noon Central Standard Time is 18:00 UTC on the same calendar date.
        /// RFC 1036 names <c>CST</c> as a North American abbreviation. The <c>-06:00</c> mapping is the Issue #164 convention; the local RFC text does not state the numeric offset.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenCentralStandardTime_ReturnsEighteenHundredUtc()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Fri, 23 Aug 2024 12:00:00 CST"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2024, 8, 23, 18, 0, 0, DateTimeKind.Utc), utc);
        }

        /// <summary>
        /// Noon Central Daylight Time is 17:00 UTC on the same calendar date. <c>CDT</c> stays <c>-05:00</c>.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenCentralDaylightTime_ReturnsSeventeenHundredUtc()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Fri, 23 Aug 2024 12:00:00 CDT"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2024, 8, 23, 17, 0, 0, DateTimeKind.Utc), utc);
        }

        /// <summary>
        /// Each built-in letter abbreviation converts 12:00 on 23 Aug 2024 to the UTC instant of its documented offset.
        /// Minutes are east of UTC. A west-of-UTC abbreviation therefore moves the result later on the same date, or onto the next date.
        /// </summary>
        /// <param name="abbreviation">Trailing timezone token.</param>
        /// <param name="offsetMinutesEast">Documented offset in minutes east of UTC.</param>
        [Theory]
        [MemberData(nameof(KnownTimezoneAbbreviations))]
        public void TryGetCanonicalUtc_WhenKnownAbbreviation_ReturnsDocumentedUtcInstant(
            string abbreviation,
            int offsetMinutesEast)
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                Encoding.ASCII.GetBytes($"Fri, 23 Aug 2024 12:00:00 {abbreviation}"),
                out var utc,
                out var failure);

            var expected = new DateTime(2024, 8, 23, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-offsetMinutesEast);
            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(expected, utc);
        }

        /// <summary>
        /// Abbreviation matching folds ASCII case, so a lowercase North American token keeps the uppercase offset.
        /// </summary>
        /// <param name="abbreviation">Mixed-case or lowercase token.</param>
        /// <param name="offsetMinutesEast">Documented offset in minutes east of UTC.</param>
        [Theory]
        [InlineData("cst", -360)]
        [InlineData("Cdt", -300)]
        [InlineData("est", -300)]
        [InlineData("pst", -480)]
        [InlineData("aest", 600)]
        [InlineData("npt", 345)]
        [InlineData("chst", 600)]
        public void TryGetCanonicalUtc_WhenAbbreviationCaseDiffers_ReturnsSameUtcInstant(
            string abbreviation,
            int offsetMinutesEast)
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                Encoding.ASCII.GetBytes($"Fri, 23 Aug 2024 12:00:00 {abbreviation}"),
                out var utc,
                out var failure);

            var expected = new DateTime(2024, 8, 23, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-offsetMinutesEast);
            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(expected, utc);
        }

        /// <summary>
        /// A longer trailing token is not split into a shorter known abbreviation.
        /// <c>ACST</c> is <c>+09:30</c>, not Central <c>CST</c>. <c>AEST</c> is not <c>EST</c>. <c>WEST</c> and <c>CEST</c> are not <c>EST</c>.
        /// <c>WITA</c> is not <c>WIT</c>. <c>PETT</c> is not <c>PET</c>. <c>CHAST</c> is not <c>ChST</c>.
        /// </summary>
        /// <param name="abbreviation">Longer trailing token.</param>
        /// <param name="offsetMinutesEast">Offset of that complete token, in minutes east of UTC.</param>
        [Theory]
        [InlineData("ACST", 570)]
        [InlineData("AEST", 600)]
        [InlineData("WEST", 60)]
        [InlineData("CEST", 120)]
        [InlineData("WITA", 480)]
        [InlineData("WIT", 540)]
        [InlineData("WIB", 420)]
        [InlineData("PETT", 720)]
        [InlineData("PET", -300)]
        [InlineData("CHAST", 765)]
        [InlineData("CHADT", 825)]
        [InlineData("ChST", 600)]
        [InlineData("PHT", 480)]
        [InlineData("PST", -480)]
        [InlineData("BJT", 480)]
        [InlineData("CST", -360)]
        public void TryGetCanonicalUtc_WhenTokensShareLetters_UsesTheCompleteToken(
            string abbreviation,
            int offsetMinutesEast)
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                Encoding.ASCII.GetBytes($"Fri, 23 Aug 2024 12:00:00 {abbreviation}"),
                out var utc,
                out var failure);

            var expected = new DateTime(2024, 8, 23, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-offsetMinutesEast);
            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(expected, utc);
        }

        /// <summary>
        /// An abbreviation is only a trailing letter token after SP or HTAB. Letters glued to the time, or a word that merely ends in a known abbreviation, stay unknown.
        /// </summary>
        /// <param name="input">Date text that must not be rewritten as Central Standard Time.</param>
        [Theory]
        [InlineData("Fri, 23 Aug 2024 12:00:00CST")]
        [InlineData("Fri, 23 Aug 2024 12:00:00 FORECAST")]
        public void TryGetCanonicalUtc_WhenAbbreviationIsNotATrailingToken_DoesNotApplyCst(string input)
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                Encoding.ASCII.GetBytes(input),
                out var utc,
                out var failure);

            Assert.False(accepted);
            Assert.Equal(DateParseFailureReason.ParseFailed, failure);
            Assert.Equal(default, utc);
        }

        /// <summary>
        /// Without the strict option, an unknown letter token is left in place and exact parsing fails.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenUnknownAbbreviationIsTolerated_ReturnsParseFailed()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Fri, 23 Aug 2024 05:30:10 ZZQX"u8,
                out _,
                out var failure);

            Assert.False(accepted);
            Assert.Equal(DateParseFailureReason.ParseFailed, failure);
        }

        /// <summary>
        /// Colliding abbreviations that this table refuses are unknown in strict mode, not silently assigned one region's offset.
        /// </summary>
        /// <param name="abbreviation">Omitted trailing token.</param>
        [Theory]
        [InlineData("AST")]
        [InlineData("ADT")]
        [InlineData("GST")]
        [InlineData("NST")]
        [InlineData("NDT")]
        [InlineData("AMT")]
        [InlineData("ECT")]
        [InlineData("WST")]
        [InlineData("SST")]
        public void TryGetCanonicalUtc_WhenOmittedCollisionIsStrict_RejectsUnknownAbbreviation(string abbreviation)
        {
            var options = DateParseOptions.Default with { RequireKnownTimezoneAbbreviation = true };
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                Encoding.ASCII.GetBytes($"Fri, 23 Aug 2024 12:00:00 {abbreviation}"),
                options,
                out _,
                out var failure);

            Assert.False(accepted);
            Assert.Equal(DateParseFailureReason.UnknownTimezoneAbbreviation, failure);
        }

        /// <summary>
        /// Each documented <c>UTC±N</c> form converts 12:00 on 23 Aug 2024 to the UTC instant of its table offset.
        /// </summary>
        /// <param name="token">Trailing <c>UTC+0</c>..<c>UTC+9</c> or <c>UTC-1</c>..<c>UTC-9</c>.</param>
        /// <param name="offsetMinutesEast">Documented offset in minutes east of UTC.</param>
        [Theory]
        [MemberData(nameof(DocumentedUtcNumericForms))]
        public void TryGetCanonicalUtc_WhenUtcNumericForm_ReturnsDocumentedUtcInstant(
            string token,
            int offsetMinutesEast)
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                Encoding.ASCII.GetBytes($"Fri, 23 Aug 2024 12:00:00 {token}"),
                out var utc,
                out var failure);

            var expected = new DateTime(2024, 8, 23, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-offsetMinutesEast);
            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(expected, utc);
        }

        /// <summary>
        /// Noon <c>UTC+5</c> is 07:00 UTC and noon <c>UTC-5</c> is 17:00 UTC. Zero offset keeps noon.
        /// </summary>
        [Theory]
        [InlineData("UTC+5", 7, 0)]
        [InlineData("UTC-5", 17, 0)]
        [InlineData("UTC+0", 12, 0)]
        public void TryGetCanonicalUtc_WhenUtcNumericSignExamples_ReturnsExpectedClock(
            string token,
            int utcHour,
            int utcMinute)
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                Encoding.ASCII.GetBytes($"Fri, 23 Aug 2024 12:00:00 {token}"),
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2024, 8, 23, utcHour, utcMinute, 0, DateTimeKind.Utc), utc);
        }

        /// <summary>
        /// The <c>UTC</c> prefix of a numeric form folds ASCII case the same way letter abbreviations do.
        /// </summary>
        /// <param name="token">Mixed-case or lowercase documented form.</param>
        /// <param name="offsetMinutesEast">Offset implied by the digit in <paramref name="token"/>.</param>
        [Theory]
        [InlineData("utc+5", 300)]
        [InlineData("Utc-3", -180)]
        [InlineData("uTc+0", 0)]
        public void TryGetCanonicalUtc_WhenUtcNumericFormCaseDiffers_ReturnsSameUtcInstant(
            string token,
            int offsetMinutesEast)
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                Encoding.ASCII.GetBytes($"Fri, 23 Aug 2024 12:00:00 {token}"),
                out var utc,
                out var failure);

            var expected = new DateTime(2024, 8, 23, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-offsetMinutesEast);
            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(expected, utc);
        }

        /// <summary>
        /// Strict mode still accepts a documented <c>UTC±N</c> form because it is a known table token, not an unknown letter abbreviation.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenUtcNumericFormIsStrict_AcceptsKnownToken()
        {
            var options = DateParseOptions.Default with { RequireKnownTimezoneAbbreviation = true };
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Fri, 23 Aug 2024 12:00:00 UTC+1"u8,
                options,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2024, 8, 23, 11, 0, 0, DateTimeKind.Utc), utc);
        }

        /// <summary>
        /// Malformed or undocumented numeric UTC spellings are not rewritten and fail exact parsing.
        /// </summary>
        /// <param name="token">Trailing token that must not match the documented single-digit forms.</param>
        [Theory]
        [InlineData("UTC+")]
        [InlineData("UTC-")]
        [InlineData("UTC+10")]
        [InlineData("UTC-0")]
        [InlineData("UTC--5")]
        [InlineData("UTC++5")]
        [InlineData("UTC+05")]
        public void TryGetCanonicalUtc_WhenUtcNumericFormIsMalformed_Rejects(string token)
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                Encoding.ASCII.GetBytes($"Fri, 23 Aug 2024 12:00:00 {token}"),
                out var utc,
                out var failure);

            Assert.False(accepted);
            Assert.Equal(DateParseFailureReason.ParseFailed, failure);
            Assert.Equal(default, utc);
        }

        /// <summary>
        /// A fractional numeric offset is still parsed on the numeric path and is not confused with an abbreviation.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenFractionalNumericOffsetPresent_ReturnsUtc()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Fri, 23 Aug 2024 12:00:00 +0530"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2024, 8, 23, 6, 30, 0, DateTimeKind.Utc), utc);
        }

        /// <summary>
        /// Input one byte longer than <see cref="DateParseOptions.MaxInputLength"/> is rejected with <see cref="DateParseFailureReason.TooLong"/>.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenTooLong_Rejects()
        {
            var padded = new byte[DateParseOptions.Default.MaxInputLength + 1];
            padded.AsSpan().Fill((byte)'A');
            var accepted = NewsDateParser.TryGetCanonicalUtc(padded, out _, out var failure);
            Assert.False(accepted);
            Assert.Equal(DateParseFailureReason.TooLong, failure);
        }

        /// <summary>
        /// RFC 5322 leap second <c>23:59:60 +0000</c> is accepted and canonicalized to the following UTC minute's second <c>00</c>.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenLeapSecondAtUtc_RollsForwardToNextMinute()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Sat, 31 Dec 2016 23:59:60 +0000"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2017, 1, 1, 0, 0, 0, DateTimeKind.Utc), utc);
            Assert.Equal("Sun, 01 Jan 2017 00:00:00 +0000", Format(utc));
        }

        /// <summary>
        /// A leap second with a non-zero numeric offset converts to UTC before the one-minute roll-forward.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenLeapSecondWithPositiveOffset_ConvertsThenRollsForward()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Sat, 31 Dec 2016 23:59:60 +0200"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2016, 12, 31, 22, 0, 0, DateTimeKind.Utc), utc);
            Assert.Equal("Sat, 31 Dec 2016 22:00:00 +0000", Format(utc));
        }

        /// <summary>
        /// Ordinary seconds immediately before the leap-second instant remain unchanged.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenSecondIs59_DoesNotRollForward()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Sat, 31 Dec 2016 23:59:59 +0000"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2016, 12, 31, 23, 59, 59, DateTimeKind.Utc), utc);
        }

        /// <summary>
        /// The UTC minute after the leap second parses as an ordinary second <c>00</c>.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenSecondIs00AfterLeap_ParsesNormally()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Sun, 01 Jan 2017 00:00:00 +0000"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2017, 1, 1, 0, 0, 0, DateTimeKind.Utc), utc);
        }

        /// <summary>
        /// An obsolete two-digit-year leap second is accepted with the same roll-forward canonicalization.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenObsoleteLeapSecond_RollsForwardToNextMinute()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Sat, 31 Dec 16 23:59:60 +0000"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2017, 1, 1, 0, 0, 0, DateTimeKind.Utc), utc);
        }

        /// <summary>
        /// A leap second expressed with a <c>GMT</c> abbreviation rolls forward after zero-offset substitution.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenLeapSecondWithGmt_RollsForwardToNextMinute()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Sat, 31 Dec 2016 23:59:60 GMT"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2017, 1, 1, 0, 0, 0, DateTimeKind.Utc), utc);
        }

        /// <summary>
        /// Minute <c>60</c>, second greater than <c>60</c>, an invalid hour, and a malformed offset stay parse failures.
        /// </summary>
        /// <param name="value">Malformed date text.</param>
        [Theory]
        [InlineData("Fri, 23 Aug 2024 23:60:00 +0000")]
        [InlineData("Fri, 23 Aug 2024 12:34:61 +0000")]
        [InlineData("Fri, 23 Aug 2024 24:00:00 +0000")]
        [InlineData("Fri, 23 Aug 2024 12:00:00 +2400")]
        public void TryGetCanonicalUtc_WhenTimeOrOffsetOutOfRange_Rejects(string value)
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                Encoding.ASCII.GetBytes(value),
                out _,
                out var failure);

            Assert.False(accepted);
            Assert.Equal(DateParseFailureReason.ParseFailed, failure);
        }

        /// <summary>
        /// Documented single-digit <c>UTC±N</c> forms and their minutes east of UTC.
        /// </summary>
        /// <returns>Pairs of token and offset minutes.</returns>
        public static IEnumerable<object[]> DocumentedUtcNumericForms()
        {
            for (var hour = 0; hour <= 9; hour++)
            {
                yield return [$"UTC+{hour}", hour * 60];
            }

            for (var hour = 1; hour <= 9; hour++)
            {
                yield return [$"UTC-{hour}", -hour * 60];
            }
        }

        /// <summary>
        /// Built-in letter abbreviations and their documented minutes east of UTC.
        /// </summary>
        /// <returns>Pairs of abbreviation and offset minutes.</returns>
        public static IEnumerable<object[]> KnownTimezoneAbbreviations()
        {
            (string Abbreviation, int OffsetMinutesEast)[] rows =
            [
                ("UT", 0),
                ("UTC", 0),
                ("GMT", 0),
                ("EST", -300),
                ("EDT", -240),
                ("CST", -360),
                ("CDT", -300),
                ("MST", -420),
                ("MDT", -360),
                ("PST", -480),
                ("PDT", -420),
                ("AKST", -540),
                ("AKDT", -480),
                ("HST", -600),
                ("WET", 0),
                ("WEST", 60),
                ("CET", 60),
                ("CEST", 120),
                ("MET", 60),
                ("MEST", 120),
                ("EET", 120),
                ("EEST", 180),
                ("BST", 60),
                ("MSK", 180),
                ("MSD", 240),
                ("TRT", 180),
                ("ART", -180),
                ("BRT", -180),
                ("BRST", -120),
                ("CLT", -240),
                ("CLST", -180),
                ("COT", -300),
                ("PET", -300),
                ("BOT", -240),
                ("GYT", -240),
                ("SRT", -180),
                ("UYT", -180),
                ("GFT", -180),
                ("PMST", -180),
                ("PMDT", -120),
                ("WAT", 60),
                ("WAST", 120),
                ("CAT", 120),
                ("EAT", 180),
                ("SAST", 120),
                ("IRST", 210),
                ("IRDT", 270),
                ("AFT", 270),
                ("PKT", 300),
                ("IST", 330),
                ("NPT", 345),
                ("LKT", 330),
                ("MMT", 390),
                ("BTT", 360),
                ("IDT", 180),
                ("AZT", 240),
                ("AZST", 300),
                ("GET", 240),
                ("ICT", 420),
                ("WIB", 420),
                ("WITA", 480),
                ("WIT", 540),
                ("HKT", 480),
                ("SGT", 480),
                ("BJT", 480),
                ("PHT", 480),
                ("MYT", 480),
                ("BNT", 480),
                ("JST", 540),
                ("KST", 540),
                ("ULAT", 480),
                ("HOVT", 420),
                ("KGT", 360),
                ("UZT", 300),
                ("TMT", 300),
                ("TJT", 300),
                ("YEKT", 300),
                ("OMST", 360),
                ("NOVT", 420),
                ("KRAT", 420),
                ("IRKT", 480),
                ("YAKT", 540),
                ("VLAT", 600),
                ("PETT", 720),
                ("AWST", 480),
                ("AWDT", 540),
                ("ACST", 570),
                ("ACDT", 630),
                ("AEST", 600),
                ("AEDT", 660),
                ("LHST", 630),
                ("LHDT", 660),
                ("NZST", 720),
                ("NZDT", 780),
                ("CHAST", 765),
                ("CHADT", 825),
                ("ChST", 600),
                ("TAHT", -600),
                ("MART", -570),
                ("CKT", -600),
                ("FJT", 720),
                ("TOT", 780),
                ("GILT", 720),
                ("PHOT", 780),
                ("LINT", 840),
                ("NRT", 720),
            ];

            foreach (var row in rows)
            {
                yield return new object[] { row.Abbreviation, row.OffsetMinutesEast };
            }
        }

        /// <summary>
        /// Formats <paramref name="utc"/> with <see cref="NewsDateParser.TryFormatCanonicalRfc5322Utc"/> and returns the ASCII text.
        /// </summary>
        /// <param name="utc">UTC instant to format. The test asserts formatting succeeds.</param>
        /// <returns>The canonical RFC 5322 UTC date text written by the parser.</returns>
        private static string Format(DateTime utc)
        {
            Span<byte> destination = stackalloc byte[40];
            Assert.True(NewsDateParser.TryFormatCanonicalRfc5322Utc(utc, destination, out var written));
            return Encoding.ASCII.GetString(destination[..written]);
        }
    }
}
