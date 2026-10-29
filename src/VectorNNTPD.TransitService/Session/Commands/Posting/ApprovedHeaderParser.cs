using System.Text;

namespace VectorNNTPD.TransitService.Session.Commands.Posting
{
    /// <summary>
    /// Parses <c>Approved:</c> as RFC 5536 mailbox-list identities.
    /// </summary>
    /// <remarks>
    /// Header names compare case-insensitively. Values are mailbox identities, not booleans.
    /// Multiple fields or mailbox-list entries are collected; identical identities collapse.
    /// Conflicting identities are rejected rather than silently choosing one.
    /// Comparison of the extracted mailbox is ASCII case-insensitive.
    /// </remarks>
    internal static class ApprovedHeaderParser
    {
        /// <summary>
        /// Collects every <c>Approved:</c> identity from <paramref name="article"/>.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> when there is no Approved field, or when every field is a
        /// well-formed mailbox-list whose identities do not conflict.
        /// </returns>
        public static bool TryRead(
            ParsedPostArticle article,
            out string[] identities,
            out PostingFailure failure)
        {
            ArgumentNullException.ThrowIfNull(article);
            identities = [];
            failure = default;

            List<string>? collected = null;
            foreach (var header in article.Headers)
            {
                if (!PostFieldSyntax.EqualsFolded(header.Name.Span, "APPROVED"u8))
                {
                    continue;
                }

                if (!TryParseMailboxList(header.UnfoldedValue.Span, out var parsed, out failure))
                {
                    identities = [];
                    return false;
                }

                collected ??= new List<string>(parsed.Length);
                foreach (var identity in parsed)
                {
                    if (ContainsIdentity(collected, identity))
                    {
                        continue;
                    }

                    collected.Add(identity);
                }
            }

            identities = collected is null ? [] : collected.ToArray();
            return true;
        }

        internal static bool TryParseMailboxList(
            ReadOnlySpan<byte> value,
            out string[] identities,
            out PostingFailure failure)
        {
            identities = [];
            failure = default;
            var collected = new List<string>(2);
            var i = 0;
            var sawToken = false;
            while (i < value.Length)
            {
                while (i < value.Length && PostFieldSyntax.IsWsp(value[i]))
                {
                    i++;
                }

                if (i >= value.Length)
                {
                    break;
                }

                if (value[i] == (byte)',')
                {
                    failure = new PostingFailure(PostingFailureCategory.InvalidApproved, "malformed Approved");
                    return false;
                }

                var start = i;
                var angle = 0;
                while (i < value.Length)
                {
                    var b = value[i];
                    if (b == (byte)'<')
                    {
                        angle++;
                    }
                    else if (b == (byte)'>')
                    {
                        angle--;
                    }
                    else if (b == (byte)',' && angle == 0)
                    {
                        break;
                    }

                    i++;
                }

                if (angle != 0 || !TryExtractMailbox(value[start..i], out var mailbox))
                {
                    failure = new PostingFailure(PostingFailureCategory.InvalidApproved, "malformed Approved");
                    return false;
                }

                if (!ContainsIdentity(collected, mailbox))
                {
                    collected.Add(mailbox);
                }

                sawToken = true;
                if (i >= value.Length)
                {
                    break;
                }

                i++;
            }

            if (!sawToken)
            {
                failure = new PostingFailure(PostingFailureCategory.InvalidApproved, "empty Approved");
                return false;
            }

            identities = collected.ToArray();
            return true;
        }

        internal static bool TryExtractMailbox(ReadOnlySpan<byte> value, out string mailbox)
        {
            mailbox = string.Empty;
            var trimmed = Trim(value);
            if (trimmed.IsEmpty)
            {
                return false;
            }

            var open = LastIndexOf(trimmed, (byte)'<');
            var close = LastIndexOf(trimmed, (byte)'>');
            ReadOnlySpan<byte> candidate;
            if (open >= 0 || close >= 0)
            {
                if (open < 0 || close <= open)
                {
                    return false;
                }

                candidate = Trim(trimmed[(open + 1)..close]);
            }
            else
            {
                candidate = trimmed;
            }

            if (!PostFieldSyntax.IsMailbox(candidate))
            {
                return false;
            }

            mailbox = Encoding.ASCII.GetString(candidate);
            return true;
        }

        internal static bool IdentitiesEqual(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (var i = 0; i < left.Length; i++)
            {
                if (Fold(left[i]) != Fold(right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ContainsIdentity(List<string> identities, string candidate)
        {
            foreach (var existing in identities)
            {
                if (IdentitiesEqual(existing, candidate))
                {
                    return true;
                }
            }

            return false;
        }

        private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value)
        {
            var start = 0;
            var end = value.Length;
            while (start < end && PostFieldSyntax.IsWsp(value[start]))
            {
                start++;
            }

            while (end > start && PostFieldSyntax.IsWsp(value[end - 1]))
            {
                end--;
            }

            return value[start..end];
        }

        private static int LastIndexOf(ReadOnlySpan<byte> value, byte needle)
        {
            for (var i = value.Length - 1; i >= 0; i--)
            {
                if (value[i] == needle)
                {
                    return i;
                }
            }

            return -1;
        }

        private static char Fold(char c) =>
            c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
    }
}
