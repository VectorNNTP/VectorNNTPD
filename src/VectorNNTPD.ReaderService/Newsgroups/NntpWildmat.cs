using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Newsgroups
{
    /// <summary>
    /// RFC 3977 §4 wildmat matcher for newsgroup names.
    /// </summary>
    /// <remarks>
    /// Operates on UTF-8 octets. Literal matching is ASCII ordinal case-insensitive
    /// (NNTP newsgroup names). Comma-separated patterns and rightmost <c>!</c> negation
    /// follow §4.2. Characters <c>\</c>, <c>[</c>, and <c>]</c> are rejected as syntax
    /// errors (RFC 3977 §4.1 / §3.2.1.1).
    /// </remarks>
    public static class NntpWildmat
    {
        /// <summary>Returns whether <paramref name="wildmat"/> is a syntactically valid RFC 3977 wildmat.</summary>
        public static bool TryValidate(ReadOnlySpan<byte> wildmat)
        {
            if (wildmat.IsEmpty)
            {
                return false;
            }

            var index = 0;
            var first = true;
            while (index < wildmat.Length)
            {
                if (!first)
                {
                    if (wildmat[index] != (byte)',')
                    {
                        return false;
                    }

                    index++;
                    if (index < wildmat.Length && wildmat[index] == (byte)'!')
                    {
                        index++;
                    }
                }

                first = false;
                if (index >= wildmat.Length)
                {
                    return false;
                }

                var patternStart = index;
                while (index < wildmat.Length && wildmat[index] != (byte)',')
                {
                    var b = wildmat[index];
                    if (b is (byte)'\\' or (byte)'[' or (byte)']')
                    {
                        return false;
                    }

                    if (b == (byte)'!' && index == patternStart)
                    {
                        return false;
                    }

                    index++;
                }

                if (patternStart == index)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns whether <paramref name="text"/> matches <paramref name="wildmat"/>
        /// using ASCII ordinal case-insensitive comparison.
        /// </summary>
        public static bool IsMatch(ReadOnlySpan<byte> text, ReadOnlySpan<byte> wildmat)
        {
            if (!TryValidate(wildmat))
            {
                return false;
            }

            return IsMatchValidated(text, wildmat);
        }

        /// <summary>
        /// Matches <paramref name="text"/> against an already-validated wildmat.
        /// </summary>
        /// <remarks>
        /// LIST command handlers use this after <see cref="TryValidate"/> has already
        /// succeeded at parse time, so each group is not revalidated.
        /// </remarks>
        public static bool IsMatchValidated(ReadOnlySpan<byte> text, ReadOnlySpan<byte> wildmat)
        {
            var rightmostMatchNegated = false;
            var anyMatch = false;
            var index = 0;
            var first = true;
            while (index < wildmat.Length)
            {
                var negated = false;
                if (!first)
                {
                    index++;
                    if (index < wildmat.Length && wildmat[index] == (byte)'!')
                    {
                        negated = true;
                        index++;
                    }
                }

                first = false;
                var start = index;
                while (index < wildmat.Length && wildmat[index] != (byte)',')
                {
                    index++;
                }

                if (MatchPattern(text, wildmat[start..index]))
                {
                    anyMatch = true;
                    rightmostMatchNegated = negated;
                }
            }

            return anyMatch && !rightmostMatchNegated;
        }

        private static bool MatchPattern(ReadOnlySpan<byte> text, ReadOnlySpan<byte> pattern)
        {
            return Match(text, pattern);
        }

        private static bool Match(ReadOnlySpan<byte> text, ReadOnlySpan<byte> pattern)
        {
            while (!pattern.IsEmpty)
            {
                if (pattern[0] == (byte)'*')
                {
                    while (pattern.Length > 1 && pattern[1] == (byte)'*')
                    {
                        pattern = pattern[1..];
                    }

                    pattern = pattern[1..];
                    if (pattern.IsEmpty)
                    {
                        return true;
                    }

                    var remaining = text;
                    while (true)
                    {
                        if (Match(remaining, pattern))
                        {
                            return true;
                        }

                        if (remaining.IsEmpty)
                        {
                            return false;
                        }

                        remaining = ConsumeCharacter(remaining);
                    }
                }

                if (text.IsEmpty)
                {
                    return false;
                }

                if (pattern[0] == (byte)'?')
                {
                    text = ConsumeCharacter(text);
                    pattern = pattern[1..];
                    continue;
                }

                if (!TryConsumeLiteral(ref text, pattern[0]))
                {
                    return false;
                }

                pattern = pattern[1..];
            }

            return text.IsEmpty;
        }

        private static bool TryConsumeLiteral(ref ReadOnlySpan<byte> text, byte literal)
        {
            if (text.IsEmpty)
            {
                return false;
            }

            var foldedText = NntpAscii.FoldUpper(text[0]);
            var foldedLiteral = NntpAscii.FoldUpper(literal);
            if (foldedText != foldedLiteral)
            {
                return false;
            }

            text = text[1..];
            return true;
        }

        private static ReadOnlySpan<byte> ConsumeCharacter(ReadOnlySpan<byte> text)
        {
            if (text.IsEmpty)
            {
                return text;
            }

            var width = Utf8CharacterWidth(text[0]);
            return width >= text.Length ? ReadOnlySpan<byte>.Empty : text[width..];
        }

        private static int Utf8CharacterWidth(byte lead)
        {
            if (lead < 0x80)
            {
                return 1;
            }

            if (lead < 0xE0)
            {
                return 2;
            }

            if (lead < 0xF0)
            {
                return 3;
            }

            return 4;
        }
    }
}
