using VectorNNTPD.Common.Articles.Validation;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Newsgroups;

namespace VectorNNTPD.TransitService.Session.CommandProcessor
{
    /// <summary>
    /// Authoritative byte-oriented NNTP command parser and syntactic validation boundary.
    /// </summary>
    /// <remarks>
    /// Only <see cref="NntpParseStatus.Ok"/> may reach a command handler. Invalid commands are
    /// rejected at dispatch. Argument bytes remain indexes into the caller's buffer.
    /// Keywords are case-insensitive; SP and TAB are separators (RFC 3977 §3.1).
    /// </remarks>
    public static class NntpCommandParser
    {
        /// <summary>Parses one command line (CRLF already removed). Never throws for ordinary malformed input.</summary>
        public static NntpCommand Parse(ReadOnlySpan<byte> line)
        {
            var verb = NntpVerbClassifier.Classify(line, out _, out int verbEnd);
            if (verb == NntpVerb.None)
            {
                return new NntpCommand(NntpVerb.None, NntpVerb.None, 0, 0, 0, NntpParseStatus.Empty);
            }

            if (verb == NntpVerb.Unknown)
            {
                return Finish(NntpVerb.Unknown, NntpVerb.None, line, verbEnd, NntpParseStatus.UnknownVerb);
            }

            int index = NntpAscii.SkipWhitespace(line, verbEnd);
            var qualifier = NntpVerb.None;

            if (NeedsQualifier(verb))
            {
                if (index >= line.Length)
                {
                    return new NntpCommand(verb, NntpVerb.None, 0, 0, 0, NntpParseStatus.UnknownQualifier);
                }

                int qEnd = NntpAscii.SkipToken(line, index);
                qualifier = ClassifyQualifier(verb, line.Slice(index, qEnd - index));
                if (qualifier is NntpVerb.Unknown or NntpVerb.None)
                {
                    return Finish(verb, NntpVerb.Unknown, line, qEnd, NntpParseStatus.UnknownQualifier);
                }

                index = NntpAscii.SkipWhitespace(line, qEnd);
            }
            else if (verb == NntpVerb.List && index < line.Length)
            {
                int qEnd = NntpAscii.SkipToken(line, index);
                var listQualifier = ClassifyListQualifier(line.Slice(index, qEnd - index));
                if (listQualifier == NntpVerb.Unknown)
                {
                    return Finish(verb, NntpVerb.Unknown, line, qEnd, NntpParseStatus.UnknownQualifier);
                }

                qualifier = listQualifier;
                index = NntpAscii.SkipWhitespace(line, qEnd);
            }

            int argEnd = NntpAscii.TrimEndWhitespace(line, index, line.Length);
            int tokenCount = NntpAscii.CountTokens(line, index, argEnd);
            int argLength = argEnd - index;
            var status = Validate(verb, qualifier, line, index, argLength, tokenCount);
            return new NntpCommand(verb, qualifier, index, argLength, tokenCount, status);
        }

        private static NntpCommand Finish(
            NntpVerb verb,
            NntpVerb qualifier,
            ReadOnlySpan<byte> line,
            int after,
            NntpParseStatus status)
        {
            int index = NntpAscii.SkipWhitespace(line, after);
            int argEnd = NntpAscii.TrimEndWhitespace(line, index, line.Length);
            return new NntpCommand(
                verb,
                qualifier,
                index,
                argEnd - index,
                NntpAscii.CountTokens(line, index, argEnd),
                status);
        }

        private static bool NeedsQualifier(NntpVerb verb) => verb is NntpVerb.Mode or NntpVerb.AuthInfo;

        private static NntpParseStatus Validate(
            NntpVerb verb,
            NntpVerb qualifier,
            ReadOnlySpan<byte> line,
            int argumentStart,
            int argumentLength,
            int tokenCount)
        {
            var argument = argumentLength <= 0
                ? ReadOnlySpan<byte>.Empty
                : line.Slice(argumentStart, argumentLength);

            switch (verb)
            {
                case NntpVerb.Check:
                case NntpVerb.TakeThis:
                case NntpVerb.Ihave:
                    if (tokenCount == 0)
                    {
                        return NntpParseStatus.MissingArgument;
                    }

                    if (tokenCount != 1)
                    {
                        return NntpParseStatus.ExtraArgument;
                    }

                    return NntpMessageIdValidation.IsValidMessageId(argument)
                        ? NntpParseStatus.Ok
                        : NntpParseStatus.InvalidArgument;

                case NntpVerb.SpeedTest:
                    if (tokenCount == 0)
                    {
                        return NntpParseStatus.MissingArgument;
                    }

                    if (tokenCount != 1)
                    {
                        return NntpParseStatus.ExtraArgument;
                    }

                    return IsSpeedTestPeerSyntax(argument)
                        ? NntpParseStatus.Ok
                        : NntpParseStatus.InvalidArgument;

                case NntpVerb.Quit:
                case NntpVerb.Help:
                case NntpVerb.Post:
                    return tokenCount == 0 ? NntpParseStatus.Ok : NntpParseStatus.ExtraArgument;

                case NntpVerb.Compress:
                    if (tokenCount == 0)
                    {
                        return NntpParseStatus.MissingArgument;
                    }

                    if (tokenCount != 1)
                    {
                        return NntpParseStatus.ExtraArgument;
                    }

                    return IsCompressAlgorithmSyntax(argument)
                        ? NntpParseStatus.Ok
                        : NntpParseStatus.InvalidArgument;

                case NntpVerb.AuthInfo when qualifier is NntpVerb.User or NntpVerb.Pass:
                    return argument.IsEmpty ? NntpParseStatus.MissingArgument : NntpParseStatus.Ok;

                case NntpVerb.AuthInfo when qualifier is NntpVerb.Sasl:
                    return argument.IsEmpty ? NntpParseStatus.MissingArgument : NntpParseStatus.Ok;

                case NntpVerb.Article:
                case NntpVerb.Head:
                case NntpVerb.Body:
                case NntpVerb.Stat:
                    if (tokenCount == 0)
                    {
                        return NntpParseStatus.Ok;
                    }

                    if (tokenCount != 1)
                    {
                        return NntpParseStatus.ExtraArgument;
                    }

                    return NntpMessageIdValidation.IsValidMessageId(argument) || NntpArticleNumber.IsSyntax(argument)
                        ? NntpParseStatus.Ok
                        : NntpParseStatus.InvalidArgument;

                case NntpVerb.Group:
                    if (tokenCount == 0)
                    {
                        return NntpParseStatus.MissingArgument;
                    }

                    return tokenCount == 1 ? NntpParseStatus.Ok : NntpParseStatus.ExtraArgument;

                case NntpVerb.ListGroup:
                    return tokenCount <= 2 ? NntpParseStatus.Ok : NntpParseStatus.ExtraArgument;

                case NntpVerb.List when qualifier is NntpVerb.None:
                    return tokenCount == 0 ? NntpParseStatus.Ok : NntpParseStatus.ExtraArgument;

                case NntpVerb.List when qualifier is NntpVerb.Active or NntpVerb.Counts or NntpVerb.Newsgroups:
                    if (tokenCount == 0)
                    {
                        return NntpParseStatus.Ok;
                    }

                    if (tokenCount != 1)
                    {
                        return NntpParseStatus.ExtraArgument;
                    }

                    return NntpWildmat.TryValidate(argument)
                        ? NntpParseStatus.Ok
                        : NntpParseStatus.InvalidArgument;

                case NntpVerb.List when qualifier is NntpVerb.Motd or NntpVerb.OverviewFmt:
                    return tokenCount == 0 ? NntpParseStatus.Ok : NntpParseStatus.ExtraArgument;

                case NntpVerb.List when qualifier is NntpVerb.Headers:
                    if (tokenCount == 0)
                    {
                        return NntpParseStatus.Ok;
                    }

                    if (tokenCount != 1)
                    {
                        return NntpParseStatus.ExtraArgument;
                    }

                    return NntpAscii.EqualsFolded(argument, "MSGID"u8)
                        || NntpAscii.EqualsFolded(argument, "RANGE"u8)
                        ? NntpParseStatus.Ok
                        : NntpParseStatus.InvalidArgument;

                default:
                    return NntpParseStatus.Ok;
            }
        }

        /// <summary>
        /// SPEEDTEST identifier: one NNTP token matching the Transit identifier grammar
        /// (1–256 visible ASCII octets, no whitespace or controls). Exact ordinal match
        /// is applied later against the snapshot. Not a quoted string or multi-token name.
        /// </summary>
        internal static bool IsSpeedTestPeerSyntax(ReadOnlySpan<byte> peer) =>
            TransitPeersOptionsValidator.IsPeerIdentifier(peer);

        /// <summary>RFC 8054 §5.3 algorithm syntax (case-sensitive).</summary>
        internal static bool IsCompressAlgorithmSyntax(ReadOnlySpan<byte> algorithm)
        {
            if (algorithm.Length is < 1 or > 20)
            {
                return false;
            }

            for (int i = 0; i < algorithm.Length; i++)
            {
                byte b = algorithm[i];
                if ((b >= (byte)'A' && b <= (byte)'Z')
                    || (b >= (byte)'0' && b <= (byte)'9')
                    || b == (byte)'-'
                    || b == (byte)'_')
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        private static NntpVerb ClassifyQualifier(NntpVerb verb, ReadOnlySpan<byte> token)
        {
            if (verb == NntpVerb.Mode)
            {
                if (NntpAscii.EqualsFolded(token, "STREAM"u8))
                {
                    return NntpVerb.Stream;
                }

                if (NntpAscii.EqualsFolded(token, "READER"u8))
                {
                    return NntpVerb.Reader;
                }

                return NntpVerb.Unknown;
            }

            if (verb == NntpVerb.AuthInfo)
            {
                if (NntpAscii.EqualsFolded(token, "USER"u8))
                {
                    return NntpVerb.User;
                }

                if (NntpAscii.EqualsFolded(token, "PASS"u8))
                {
                    return NntpVerb.Pass;
                }

                if (NntpAscii.EqualsFolded(token, "SASL"u8))
                {
                    return NntpVerb.Sasl;
                }

                return NntpVerb.Unknown;
            }

            return NntpVerb.None;
        }

        private static NntpVerb ClassifyListQualifier(ReadOnlySpan<byte> token)
        {
            if (NntpAscii.EqualsFolded(token, "ACTIVE"u8))
            {
                return NntpVerb.Active;
            }

            if (NntpAscii.EqualsFolded(token, "COUNTS"u8))
            {
                return NntpVerb.Counts;
            }

            if (NntpAscii.EqualsFolded(token, "HEADERS"u8))
            {
                return NntpVerb.Headers;
            }

            if (NntpAscii.EqualsFolded(token, "MOTD"u8))
            {
                return NntpVerb.Motd;
            }

            if (NntpAscii.EqualsFolded(token, "NEWSGROUPS"u8))
            {
                return NntpVerb.Newsgroups;
            }

            if (NntpAscii.EqualsFolded(token, "OVERVIEW.FMT"u8))
            {
                return NntpVerb.OverviewFmt;
            }

            return NntpVerb.Unknown;
        }
    }
}
