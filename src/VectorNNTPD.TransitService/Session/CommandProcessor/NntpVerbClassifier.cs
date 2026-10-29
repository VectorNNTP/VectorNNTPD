namespace VectorNNTPD.TransitService.Session.CommandProcessor
{
    /// <summary>
    /// Zero-allocation verb classifier: length, then first byte, then remaining ASCII-folded compare.
    /// </summary>
    internal static class NntpVerbClassifier
    {
        public static NntpVerb Classify(ReadOnlySpan<byte> line, out int verbStart, out int verbEnd)
        {
            verbStart = NntpAscii.SkipWhitespace(line, 0);
            if (verbStart >= line.Length)
            {
                verbEnd = verbStart;
                return NntpVerb.None;
            }

            verbEnd = NntpAscii.SkipToken(line, verbStart);
            return ClassifyVerb(line.Slice(verbStart, verbEnd - verbStart));
        }

        private static NntpVerb ClassifyVerb(ReadOnlySpan<byte> verb)
        {
            if (verb.IsEmpty)
            {
                return NntpVerb.Unknown;
            }

            byte first = NntpAscii.FoldUpper(verb[0]);
            return verb.Length switch
            {
                3 => first == (byte)'H' && NntpAscii.EqualsFolded(verb, "HDR"u8) ? NntpVerb.Hdr : NntpVerb.Unknown,
                4 => ClassifyLen4(verb, first),
                5 => ClassifyLen5(verb, first),
                7 => ClassifyLen7(verb, first),
                8 => ClassifyLen8(verb, first),
                9 => ClassifyLen9(verb, first),
                12 => first == (byte)'C' && NntpAscii.EqualsFolded(verb, "CAPABILITIES"u8)
                    ? NntpVerb.Capabilities
                    : NntpVerb.Unknown,
                _ => NntpVerb.Unknown,
            };
        }

        private static NntpVerb ClassifyLen4(ReadOnlySpan<byte> verb, byte first)
        {
            return first switch
            {
                (byte)'B' => NntpAscii.EqualsFolded(verb, "BODY"u8) ? NntpVerb.Body : NntpVerb.Unknown,
                (byte)'D' => NntpAscii.EqualsFolded(verb, "DATE"u8) ? NntpVerb.Date : NntpVerb.Unknown,
                (byte)'H' => NntpAscii.EqualsFolded(verb, "HEAD"u8) ? NntpVerb.Head
                    : NntpAscii.EqualsFolded(verb, "HELP"u8) ? NntpVerb.Help
                    : NntpVerb.Unknown,
                (byte)'L' => NntpAscii.EqualsFolded(verb, "LIST"u8) ? NntpVerb.List
                    : NntpAscii.EqualsFolded(verb, "LAST"u8) ? NntpVerb.Last
                    : NntpVerb.Unknown,
                (byte)'M' => NntpAscii.EqualsFolded(verb, "MODE"u8) ? NntpVerb.Mode : NntpVerb.Unknown,
                (byte)'N' => NntpAscii.EqualsFolded(verb, "NEXT"u8) ? NntpVerb.Next : NntpVerb.Unknown,
                (byte)'O' => NntpAscii.EqualsFolded(verb, "OVER"u8) ? NntpVerb.Over : NntpVerb.Unknown,
                (byte)'P' => NntpAscii.EqualsFolded(verb, "POST"u8) ? NntpVerb.Post : NntpVerb.Unknown,
                (byte)'Q' => NntpAscii.EqualsFolded(verb, "QUIT"u8) ? NntpVerb.Quit : NntpVerb.Unknown,
                (byte)'S' => NntpAscii.EqualsFolded(verb, "STAT"u8) ? NntpVerb.Stat : NntpVerb.Unknown,
                _ => NntpVerb.Unknown,
            };
        }

        private static NntpVerb ClassifyLen5(ReadOnlySpan<byte> verb, byte first)
        {
            return first switch
            {
                (byte)'C' => NntpAscii.EqualsFolded(verb, "CHECK"u8) ? NntpVerb.Check : NntpVerb.Unknown,
                (byte)'G' => NntpAscii.EqualsFolded(verb, "GROUP"u8) ? NntpVerb.Group : NntpVerb.Unknown,
                (byte)'I' => NntpAscii.EqualsFolded(verb, "IHAVE"u8) ? NntpVerb.Ihave : NntpVerb.Unknown,
                _ => NntpVerb.Unknown,
            };
        }

        private static NntpVerb ClassifyLen7(ReadOnlySpan<byte> verb, byte first)
        {
            return first switch
            {
                (byte)'A' => NntpAscii.EqualsFolded(verb, "ARTICLE"u8) ? NntpVerb.Article : NntpVerb.Unknown,
                (byte)'B' => NntpAscii.EqualsFolded(verb, "BENCHIT"u8) ? NntpVerb.BenchIt : NntpVerb.Unknown,
                _ => NntpVerb.Unknown,
            };
        }

        private static NntpVerb ClassifyLen8(ReadOnlySpan<byte> verb, byte first)
        {
            return first switch
            {
                (byte)'A' => NntpAscii.EqualsFolded(verb, "AUTHINFO"u8) ? NntpVerb.AuthInfo : NntpVerb.Unknown,
                (byte)'C' => NntpAscii.EqualsFolded(verb, "COMPRESS"u8) ? NntpVerb.Compress : NntpVerb.Unknown,
                (byte)'S' => NntpAscii.EqualsFolded(verb, "STARTTLS"u8) ? NntpVerb.StartTls : NntpVerb.Unknown,
                (byte)'T' => NntpAscii.EqualsFolded(verb, "TAKETHIS"u8) ? NntpVerb.TakeThis : NntpVerb.Unknown,
                _ => NntpVerb.Unknown,
            };
        }

        private static NntpVerb ClassifyLen9(ReadOnlySpan<byte> verb, byte first)
        {
            return first switch
            {
                (byte)'L' => NntpAscii.EqualsFolded(verb, "LISTGROUP"u8) ? NntpVerb.ListGroup : NntpVerb.Unknown,
                (byte)'S' => NntpAscii.EqualsFolded(verb, "SPEEDTEST"u8) ? NntpVerb.SpeedTest : NntpVerb.Unknown,
                _ => NntpVerb.Unknown,
            };
        }
    }
}
