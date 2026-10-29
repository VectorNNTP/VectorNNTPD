namespace VectorNNTPD.TransitService.ArticleIngestion
{
    /// <summary>
    /// Serializes one INN <c>news</c> line per innd(8) LOGGING / <c>ARTlog</c>.
    /// </summary>
    /// <remarks>
    /// Verified INN field order (innd.pod LOGGING and <c>innd/art.c</c> <c>ARTlog</c>):
    /// <c>mon dd hh:mm:ss.mmm disposition feedsite message-id size [sites|reason]</c>.
    /// Timestamp uses English month abbreviations and local wall time at millisecond
    /// resolution. Empty inbound <c>Feed</c> is INN's unavailable token <c>?</c>.
    /// Outbound <c>Sites</c> are written only for <see cref="NewsLogDisposition.Accepted"/>
    /// (<c>+</c>); empty Sites then emit <c>?</c> until routing exists. Junk, rejected,
    /// and moderated lines omit Sites and append the already-decided reason after size
    /// when present. The NNTP response code is not a news field. Disposition characters
    /// and field order are hard-coded.
    /// </remarks>
    internal static class NewsLogLineFormatter
    {
        private static ReadOnlySpan<byte> Months => "JanFebMarAprMayJunJulAugSepOctNovDec"u8;

        private static ReadOnlySpan<byte> UnknownFeed => "?"u8;

        /// <summary>Formats <paramref name="evt"/> into <paramref name="destination"/> as one LF-terminated line.</summary>
        /// <returns>Bytes written.</returns>
        public static int Write(Span<byte> destination, in NewsLogEvent evt, DateTimeOffset timestamp)
        {
            var local = timestamp.ToOffset(timestamp.Offset);
            var dt = local.DateTime;
            var written = 0;
            written += WriteMonth(destination[written..], dt.Month);
            destination[written++] = (byte)' ';
            written += WriteSpacePaddedDay(destination[written..], dt.Day);
            destination[written++] = (byte)' ';
            written += WriteTwoDigits(destination[written..], dt.Hour);
            destination[written++] = (byte)':';
            written += WriteTwoDigits(destination[written..], dt.Minute);
            destination[written++] = (byte)':';
            written += WriteTwoDigits(destination[written..], dt.Second);
            destination[written++] = (byte)'.';
            written += WriteThreeDigits(destination[written..], dt.Millisecond);
            destination[written++] = (byte)' ';
            destination[written++] = (byte)evt.Disposition;
            destination[written++] = (byte)' ';
            var feed = evt.Feed.IsEmpty ? UnknownFeed : evt.Feed.Span;
            feed.CopyTo(destination[written..]);
            written += feed.Length;
            destination[written++] = (byte)' ';
            evt.MessageId.Span.CopyTo(destination[written..]);
            written += evt.MessageId.Length;
            destination[written++] = (byte)' ';
            written += WriteUnsignedDecimal(destination[written..], evt.Size);
            if (evt.HasOutboundSiteField)
            {
                destination[written++] = (byte)' ';
                var sites = evt.Sites.IsEmpty ? UnknownFeed : evt.Sites.Span;
                sites.CopyTo(destination[written..]);
                written += sites.Length;
            }

            if (WritesReason(evt.Disposition) && !evt.Reason.IsEmpty)
            {
                destination[written++] = (byte)' ';
                evt.Reason.Span.CopyTo(destination[written..]);
                written += evt.Reason.Length;
            }

            destination[written++] = (byte)'\n';
            return written;
        }

        /// <summary>Byte count required to format <paramref name="evt"/> including the trailing LF.</summary>
        public static int RequiredLength(in NewsLogEvent evt)
        {
            var feedLength = evt.Feed.IsEmpty ? 1 : evt.Feed.Length;
            var sites = evt.HasOutboundSiteField
                ? 1 + (evt.Sites.IsEmpty ? 1 : evt.Sites.Length)
                : 0;
            var reason = WritesReason(evt.Disposition) && !evt.Reason.IsEmpty
                ? 1 + evt.Reason.Length
                : 0;
            return 24 + feedLength + evt.MessageId.Length + 1 + DecimalDigitCount(evt.Size)
                + sites + reason;
        }

        private static bool WritesReason(NewsLogDisposition disposition) =>
            disposition is NewsLogDisposition.Rejected or NewsLogDisposition.Junk;

        private static int WriteMonth(Span<byte> dest, int month)
        {
            var index = (month - 1) * 3;
            Months.Slice(index, 3).CopyTo(dest);
            return 3;
        }

        private static int WriteSpacePaddedDay(Span<byte> dest, int day)
        {
            if (day < 10)
            {
                dest[0] = (byte)' ';
                dest[1] = (byte)('0' + day);
            }
            else
            {
                dest[0] = (byte)('0' + (day / 10));
                dest[1] = (byte)('0' + (day % 10));
            }

            return 2;
        }

        private static int WriteTwoDigits(Span<byte> dest, int value)
        {
            dest[0] = (byte)('0' + (value / 10));
            dest[1] = (byte)('0' + (value % 10));
            return 2;
        }

        private static int WriteThreeDigits(Span<byte> dest, int value)
        {
            dest[0] = (byte)('0' + (value / 100));
            dest[1] = (byte)('0' + ((value / 10) % 10));
            dest[2] = (byte)('0' + (value % 10));
            return 3;
        }

        private static int WriteUnsignedDecimal(Span<byte> dest, int value)
        {
            var digits = DecimalDigitCount(value);
            var remaining = value;
            for (var i = digits - 1; i >= 0; i--)
            {
                dest[i] = (byte)('0' + (remaining % 10));
                remaining /= 10;
            }

            return digits;
        }

        private static int DecimalDigitCount(int value)
        {
            if (value < 10)
            {
                return 1;
            }

            if (value < 100)
            {
                return 2;
            }

            if (value < 1_000)
            {
                return 3;
            }

            if (value < 10_000)
            {
                return 4;
            }

            if (value < 100_000)
            {
                return 5;
            }

            if (value < 1_000_000)
            {
                return 6;
            }

            if (value < 10_000_000)
            {
                return 7;
            }

            if (value < 100_000_000)
            {
                return 8;
            }

            if (value < 1_000_000_000)
            {
                return 9;
            }

            return 10;
        }
    }
}
