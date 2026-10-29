using System.Text;

namespace VectorNNTPD.ReaderService.Transit
{
    /// <summary>
    /// Compiled newsfeeds(5) subscription expression using INN <c>uwildmat_poison</c> semantics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The configuration value is a single comma-separated string, not a JSON array and not a
    /// .NET glob or regular expression. Grammar and matching follow INN
    /// <c>libinn-uwildmat</c> / newsfeeds(5):
    /// </para>
    /// <list type="bullet">
    /// <item><description>Comma separates patterns; <c>\,</c> is a literal comma.</description></item>
    /// <item><description>Each pattern is matched against the complete newsgroup name (anchored).</description></item>
    /// <item><description>The rightmost matching pattern decides the result.</description></item>
    /// <item><description>Leading <c>!</c> excludes; leading <c>@</c> poisons.</description></item>
    /// <item><description><c>*</c> any sequence, <c>?</c> one Unicode scalar, <c>[...]</c> / <c>[^...]</c> sets, <c>\</c> escapes.</description></item>
    /// </list>
    /// <para>
    /// Empty segments, a dangling escape, or an unclosed character set fail validation.
    /// </para>
    /// </remarks>
    public sealed class NewsfeedsPattern
    {
        private readonly IReadOnlyList<Segment> _segments;

        private NewsfeedsPattern(string expression, IReadOnlyList<Segment> segments)
        {
            Expression = expression;
            _segments = segments;
        }

        /// <summary>Gets the original configuration expression.</summary>
        public string Expression { get; }

        /// <summary>Parses and validates a newsfeeds-style expression.</summary>
        public static bool TryParse(string? expression, out NewsfeedsPattern? pattern, out string? error)
        {
            pattern = null;
            error = null;
            if (expression is null)
            {
                error = "Patterns must not be null.";
                return false;
            }

            if (!TrySplit(expression, out var rawSegments, out error))
            {
                return false;
            }

            if (rawSegments.Count == 0)
            {
                error = "Patterns must contain at least one wildmat expression.";
                return false;
            }

            var segments = new List<Segment>(rawSegments.Count);
            for (var i = 0; i < rawSegments.Count; i++)
            {
                var raw = rawSegments[i];
                if (raw.Length == 0)
                {
                    error = "Patterns contains an empty expression (leading, trailing, or doubled comma).";
                    return false;
                }

                var kind = NewsfeedsPatternDecision.Match;
                var start = 0;
                if (raw[0] == '!')
                {
                    kind = NewsfeedsPatternDecision.Exclude;
                    start = 1;
                }
                else if (raw[0] == '@')
                {
                    kind = NewsfeedsPatternDecision.Poison;
                    start = 1;
                }

                if (start >= raw.Length)
                {
                    error = "Patterns contains an empty wildmat after '!' or '@'.";
                    return false;
                }

                var wildmat = raw[start..];
                if (!TryValidateWildmat(wildmat, out error))
                {
                    return false;
                }

                segments.Add(new Segment(kind, wildmat));
            }

            pattern = new NewsfeedsPattern(expression, segments);
            return true;
        }

        /// <summary>
        /// Returns whether <paramref name="newsgroup"/> is subscribed (inclusion, not exclude/poison).
        /// </summary>
        public bool MatchesNewsgroup(string newsgroup) =>
            Evaluate(newsgroup) == NewsfeedsPatternDecision.Match;

        /// <summary>Evaluates the expression against a single newsgroup name.</summary>
        public NewsfeedsPatternDecision Evaluate(string newsgroup)
        {
            ArgumentNullException.ThrowIfNull(newsgroup);
            var decision = NewsfeedsPatternDecision.NoMatch;
            // codeql[cs/linq/missed-where] Hot-path wildmat evaluation without LINQ allocation.
            foreach (var segment in _segments)
            {
                if (Wildmat.IsMatch(newsgroup, segment.Wildmat))
                {
                    decision = segment.Kind;
                }
            }

            return decision;
        }

        /// <summary>
        /// Evaluates poison/subscription across an article's newsgroups.
        /// </summary>
        /// <remarks>
        /// Any poison group poisons the article even when another cross-posted group would match.
        /// Otherwise the article is subscribed if any group matches.
        /// </remarks>
        public NewsfeedsPatternDecision EvaluateArticle(IEnumerable<string> newsgroups)
        {
            ArgumentNullException.ThrowIfNull(newsgroups);
            var subscribed = false;
            // codeql[cs/linq/missed-select] Early-exit poison check; Select enumerator allocation on article path.
            foreach (var group in newsgroups)
            {
                var decision = Evaluate(group);
                if (decision == NewsfeedsPatternDecision.Poison)
                {
                    return NewsfeedsPatternDecision.Poison;
                }

                if (decision == NewsfeedsPatternDecision.Match)
                {
                    subscribed = true;
                }
            }

            return subscribed ? NewsfeedsPatternDecision.Match : NewsfeedsPatternDecision.NoMatch;
        }

        private static bool TrySplit(string expression, out List<string> segments, out string? error)
        {
            segments = [];
            error = null;
            var current = new StringBuilder();
            var escaped = false;
            foreach (var ch in expression)
            {
                if (escaped)
                {
                    current.Append(ch);
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    current.Append(ch);
                    escaped = true;
                    continue;
                }

                if (ch == ',')
                {
                    segments.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(ch);
            }

            if (escaped)
            {
                error = "Patterns ends with a dangling backslash escape.";
                return false;
            }

            segments.Add(current.ToString());
            return true;
        }

        private static bool TryValidateWildmat(string wildmat, out string? error)
        {
            error = null;
            var escaped = false;
            for (var i = 0; i < wildmat.Length; i++)
            {
                var ch = wildmat[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch != '[')
                {
                    continue;
                }

                if (!TryConsumeCharacterSet(wildmat, ref i, out error))
                {
                    return false;
                }
            }

            if (escaped)
            {
                error = "Patterns contains a dangling backslash escape.";
                return false;
            }

            return true;
        }

        private static bool TryConsumeCharacterSet(string wildmat, ref int index, out string? error)
        {
            error = null;
            var i = index + 1;
            if (i >= wildmat.Length)
            {
                error = "Patterns contains an unclosed character set.";
                return false;
            }

            if (wildmat[i] == '^')
            {
                i++;
            }

            if (i < wildmat.Length && wildmat[i] == ']')
            {
                i++;
            }

            while (i < wildmat.Length && wildmat[i] != ']')
            {
                i++;
            }

            if (i >= wildmat.Length)
            {
                error = "Patterns contains an unclosed character set.";
                return false;
            }

            index = i;
            return true;
        }

        private readonly struct Segment
        {
            public Segment(NewsfeedsPatternDecision kind, string wildmat)
            {
                Kind = kind;
                Wildmat = wildmat;
            }

            public NewsfeedsPatternDecision Kind { get; }

            public string Wildmat { get; }
        }

        /// <summary>INN uwildmat single-pattern matcher (no comma / ! / @ handling).</summary>
        internal static class Wildmat
        {
            public static bool IsMatch(string text, string pattern) =>
                Match(text.AsSpan(), pattern.AsSpan());

            private static bool Match(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
            {
                while (!pattern.IsEmpty)
                {
                    if (pattern[0] == '*')
                    {
                        while (pattern.Length > 1 && pattern[1] == '*')
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

                            remaining = ConsumeRune(remaining);
                        }
                    }

                    if (text.IsEmpty)
                    {
                        return false;
                    }

                    if (pattern[0] == '\\')
                    {
                        if (pattern.Length < 2)
                        {
                            return false;
                        }

                        if (!TryConsumeLiteral(ref text, pattern[1]))
                        {
                            return false;
                        }

                        pattern = pattern[2..];
                        continue;
                    }

                    if (pattern[0] == '?')
                    {
                        text = ConsumeRune(text);
                        pattern = pattern[1..];
                        continue;
                    }

                    if (pattern[0] == '[')
                    {
                        if (!TryMatchCharacterSet(ref text, ref pattern))
                        {
                            return false;
                        }

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

            private static bool TryMatchCharacterSet(ref ReadOnlySpan<char> text, ref ReadOnlySpan<char> pattern)
            {
                if (text.IsEmpty)
                {
                    return false;
                }

                var body = pattern[1..];
                var negated = false;
                if (!body.IsEmpty && body[0] == '^')
                {
                    negated = true;
                    body = body[1..];
                }

                if (body.IsEmpty)
                {
                    return false;
                }

                Rune.DecodeFromUtf16(text, out var candidate, out var candidateLen);
                var matched = false;
                var i = 0;
                if (body[0] == ']')
                {
                    matched = candidate.Value == ']';
                    i = 1;
                }

                while (i < body.Length && body[i] != ']')
                {
                    if (i + 2 < body.Length && body[i + 1] == '-' && body[i + 2] != ']')
                    {
                        var start = body[i];
                        var end = body[i + 2];
                        if (candidate.Value >= start && candidate.Value <= end)
                        {
                            matched = true;
                        }

                        i += 3;
                        continue;
                    }

                    if (candidate.Value == body[i])
                    {
                        matched = true;
                    }

                    i++;
                }

                if (i >= body.Length || body[i] != ']')
                {
                    return false;
                }

                if (matched == negated)
                {
                    return false;
                }

                text = text[candidateLen..];
                pattern = body[(i + 1)..];
                return true;
            }

            private static bool TryConsumeLiteral(ref ReadOnlySpan<char> text, char expected)
            {
                if (text.IsEmpty || text[0] != expected)
                {
                    return false;
                }

                text = text[1..];
                return true;
            }

            private static ReadOnlySpan<char> ConsumeRune(ReadOnlySpan<char> text)
            {
                Rune.DecodeFromUtf16(text, out _, out var len);
                return text[len..];
            }
        }
    }
}
