using VectorNNTPD.Common.Articles.Validation;

namespace VectorNNTPD.ReaderService.Session.Commands.Posting
{
    /// <summary>Strict POST header-field validation after one structural parse.</summary>
    internal static class PostArticleValidator
    {
        private static readonly byte[][] SingletonNames =
        [
            "DATE"u8.ToArray(),
            "FROM"u8.ToArray(),
            "MESSAGE-ID"u8.ToArray(),
            "NEWSGROUPS"u8.ToArray(),
            "PATH"u8.ToArray(),
            "SUBJECT"u8.ToArray(),
            "CONTROL"u8.ToArray(),
            "DISTRIBUTION"u8.ToArray(),
            "FOLLOWUP-TO"u8.ToArray(),
            "INJECTION-DATE"u8.ToArray(),
            "INJECTION-INFO"u8.ToArray(),
            "LINES"u8.ToArray(),
            "ORGANIZATION"u8.ToArray(),
            "REFERENCES"u8.ToArray(),
            "SUMMARY"u8.ToArray(),
            "SUPERSEDES"u8.ToArray(),
            "USER-AGENT"u8.ToArray(),
            "XREF"u8.ToArray(),
            "NNTP-POSTING-DATE"u8.ToArray(),
            "NNTP-POSTING-HOST"u8.ToArray(),
            "X-TRACE"u8.ToArray(),
            "IN-REPLY-TO"u8.ToArray(),
        ];

        /// <summary>Validates parsed headers and fills Message-ID, Newsgroups, and Date on <paramref name="article"/>.</summary>
        public static bool TryValidate(
            ParsedPostArticle article,
            DateTimeOffset injectionUtc,
            INewsgroupPostingPolicy newsgroupPolicy,
            out PostingFailure failure,
            bool controlCancelPermitted = false)
        {
            ArgumentNullException.ThrowIfNull(article);
            ArgumentNullException.ThrowIfNull(newsgroupPolicy);

            if (!TryCheckSingletonsAndForbidden(article, out failure))
            {
                return false;
            }

            if (!TryRequire(article, "FROM"u8, "From", out var fromHeader, out failure)
                || !TryRequire(article, "NEWSGROUPS"u8, "Newsgroups", out var groupsHeader, out failure)
                || !TryRequire(article, "SUBJECT"u8, "Subject", out _, out failure))
            {
                return false;
            }

            if (!PostFieldSyntax.IsMailbox(fromHeader.UnfoldedValue.Span))
            {
                failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "invalid From");
                return false;
            }

            if (article.TryGetHeader("DATE"u8, out var dateHeader))
            {
                if (!PostRfcDate.TryParse(dateHeader.UnfoldedValue.Span, out var authorDate))
                {
                    failure = new PostingFailure(PostingFailureCategory.InvalidDate, "malformed Date");
                    return false;
                }

                if (!PostRfcDate.IsWithinPolicy(authorDate, injectionUtc))
                {
                    failure = new PostingFailure(PostingFailureCategory.InvalidDate, "Date outside policy window");
                    return false;
                }

                article.AuthorDate = authorDate;
                article.DateSynthesized = false;
            }
            else
            {
                article.AuthorDate = injectionUtc;
                article.DateSynthesized = true;
            }

            var groups = new List<string>(4);
            if (!PostFieldSyntax.TryParseNewsgroupList(
                    groupsHeader.UnfoldedValue.Span,
                    groups,
                    PostingLimits.MaxNewsgroups,
                    allowPoster: false))
            {
                failure = new PostingFailure(PostingFailureCategory.InvalidNewsgroups, "malformed Newsgroups");
                return false;
            }

            article.Newsgroups = groups.ToArray();

            if (article.TryGetHeader("MESSAGE-ID"u8, out var messageIdHeader))
            {
                if (!NntpMessageIdValidation.IsValidMessageId(messageIdHeader.UnfoldedValue.Span))
                {
                    failure = new PostingFailure(PostingFailureCategory.InvalidMessageId, "malformed Message-ID");
                    return false;
                }

                article.MessageId = System.Text.Encoding.ASCII.GetString(messageIdHeader.UnfoldedValue.Span);
                article.MessageIdSynthesized = false;
            }
            else
            {
                article.MessageId = PostMessageIdFactory.Create();
                article.MessageIdSynthesized = true;
            }

            if (!TryValidateOptionalFields(article, controlCancelPermitted, out failure))
            {
                return false;
            }

            var evaluation = newsgroupPolicy.Evaluate(article.Newsgroups);
            article.CatalogStatus = evaluation.Status;
            article.ModeratedGroups = evaluation.ModeratedGroupNames;
            if (evaluation.Status == NewsgroupCatalogStatus.Rejected)
            {
                failure = evaluation.Failure
                    ?? new PostingFailure(PostingFailureCategory.PolicyRejected, "newsgroup policy rejected");
                return false;
            }

            return true;
        }

        private static bool TryCheckSingletonsAndForbidden(ParsedPostArticle article, out PostingFailure failure)
        {
            failure = default;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in article.Headers)
            {
                var name = System.Text.Encoding.ASCII.GetString(header.Name.Span);
                if (IsForbiddenMailRouting(header.Name.Span))
                {
                    failure = new PostingFailure(PostingFailureCategory.PolicyRejected, "mail routing header");
                    return false;
                }

                if (!IsSingleton(header.Name.Span))
                {
                    continue;
                }

                if (!seen.Add(name))
                {
                    failure = new PostingFailure(PostingFailureCategory.DuplicateHeader, name);
                    return false;
                }
            }

            return true;
        }

        private static bool TryValidateOptionalFields(
            ParsedPostArticle article,
            bool controlCancelPermitted,
            out PostingFailure failure)
        {
            failure = default;
            var ids = new List<ReadOnlyMemory<byte>>(8);

            if (article.TryGetHeader("FOLLOWUP-TO"u8, out var followup))
            {
                var followGroups = new List<string>(4);
                if (!PostFieldSyntax.TryParseNewsgroupList(
                        followup.UnfoldedValue.Span,
                        followGroups,
                        PostingLimits.MaxFollowupToGroups,
                        allowPoster: true))
                {
                    failure = new PostingFailure(PostingFailureCategory.InvalidFollowupTo, "malformed Followup-To");
                    return false;
                }
            }

            if (article.TryGetHeader("REFERENCES"u8, out var references)
                && !PostFieldSyntax.TryParseMessageIdList(references.UnfoldedValue.Span, ids, maxCount: 32))
            {
                failure = new PostingFailure(PostingFailureCategory.InvalidReferences, "malformed References");
                return false;
            }

            var referenceIds = ids.ToArray();
            if (article.TryGetHeader("IN-REPLY-TO"u8, out var inReplyTo))
            {
                if (!PostFieldSyntax.TryParseMessageIdList(inReplyTo.UnfoldedValue.Span, ids, maxCount: 8))
                {
                    failure = new PostingFailure(PostingFailureCategory.InvalidReferences, "malformed In-Reply-To");
                    return false;
                }

                if (referenceIds.Length > 0 && !ContainsMessageId(referenceIds, ids[^1].Span))
                {
                    failure = new PostingFailure(
                        PostingFailureCategory.InvalidReferences,
                        "In-Reply-To is not in References");
                    return false;
                }
            }

            if (article.TryGetHeader("DISTRIBUTION"u8, out var distribution)
                && !TryValidateDistribution(distribution.UnfoldedValue.Span))
            {
                failure = new PostingFailure(PostingFailureCategory.InvalidDistribution, "malformed Distribution");
                return false;
            }

            if (!ApprovedHeaderParser.TryRead(article, out var approvedIdentities, out failure))
            {
                return false;
            }

            article.ApprovedIdentities = approvedIdentities;
            article.ApprovedPresent = approvedIdentities.Length > 0;

            if (article.TryGetHeader("CONTROL"u8, out var control)
                && (!controlCancelPermitted
                    || !IsAuthorizedCancelControl(control.UnfoldedValue.Span)))
            {
                failure = new PostingFailure(PostingFailureCategory.InvalidControl, "unsupported Control");
                return false;
            }

            if (article.TryGetHeader("SUPERSEDES"u8, out var supersedes)
                && !PostFieldSyntax.TryParseMessageIdList(supersedes.UnfoldedValue.Span, ids, maxCount: 8))
            {
                failure = new PostingFailure(PostingFailureCategory.InvalidControl, "malformed Supersedes");
                return false;
            }

            if (article.TryGetHeader("LINES"u8, out var lines)
                && !IsUnsignedInteger(lines.UnfoldedValue.Span))
            {
                failure = new PostingFailure(PostingFailureCategory.MalformedHeader, "malformed Lines");
                return false;
            }

            return true;
        }

        private static bool TryValidateDistribution(ReadOnlySpan<byte> value)
        {
            var i = 0;
            var count = 0;
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
                    return false;
                }

                var start = i;
                while (i < value.Length && value[i] != (byte)',' && !PostFieldSyntax.IsWsp(value[i]))
                {
                    i++;
                }

                if (!PostFieldSyntax.IsDistributionToken(value[start..i]))
                {
                    return false;
                }

                count++;
                if (count > 16)
                {
                    return false;
                }

                while (i < value.Length && PostFieldSyntax.IsWsp(value[i]))
                {
                    i++;
                }

                if (i >= value.Length)
                {
                    break;
                }

                if (value[i] != (byte)',')
                {
                    return false;
                }

                i++;
            }

            return count > 0;
        }

        /// <summary>
        /// Accepts only <c>cancel &lt;message-id&gt;</c> (optional surrounding WSP).
        /// Other Control verbs stay rejected even for the newsmaster.
        /// </summary>
        internal static bool IsAuthorizedCancelControl(ReadOnlySpan<byte> value)
        // codeql[cs/complex-block] Protocol validation clarity for cancel Control parsing.
        {
            var i = 0;
            while (i < value.Length && PostFieldSyntax.IsWsp(value[i]))
            {
                i++;
            }

            if (i + 6 > value.Length || !PostFieldSyntax.EqualsFolded(value.Slice(i, 6), "CANCEL"u8))
            {
                return false;
            }

            i += 6;
            if (i >= value.Length || !PostFieldSyntax.IsWsp(value[i]))
            {
                return false;
            }

            while (i < value.Length && PostFieldSyntax.IsWsp(value[i]))
            {
                i++;
            }

            var idStart = i;
            while (i < value.Length && !PostFieldSyntax.IsWsp(value[i]))
            {
                i++;
            }

            var messageId = value[idStart..i];
            while (i < value.Length && PostFieldSyntax.IsWsp(value[i]))
            {
                i++;
            }

            return i == value.Length && NntpMessageIdValidation.IsValidMessageId(messageId);
        }

        private static bool TryRequire(
            ParsedPostArticle article,
            ReadOnlySpan<byte> upperName,
            string display,
            out ParsedPostHeader header,
            out PostingFailure failure)
        {
            if (article.TryGetHeader(upperName, out header))
            {
                failure = default;
                return true;
            }

            header = default;
            failure = new PostingFailure(PostingFailureCategory.MissingRequiredHeader, display);
            return false;
        }

        private static bool IsSingleton(ReadOnlySpan<byte> name)
        {
            foreach (var candidate in SingletonNames)
            {
                if (PostFieldSyntax.EqualsFolded(name, candidate))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsForbiddenMailRouting(ReadOnlySpan<byte> name) =>
            PostFieldSyntax.EqualsFolded(name, "RECEIVED"u8)
            || PostFieldSyntax.EqualsFolded(name, "DELIVERED-TO"u8)
            || PostFieldSyntax.EqualsFolded(name, "BCC"u8)
            || PostFieldSyntax.EqualsFolded(name, "CC"u8)
            || PostFieldSyntax.EqualsFolded(name, "TO"u8)
            || PostFieldSyntax.EqualsFolded(name, "X-FORWARDED-FOR"u8)
            || PostFieldSyntax.EqualsFolded(name, "FORWARDED"u8);

        private static bool ContainsMessageId(ReadOnlyMemory<byte>[] haystack, ReadOnlySpan<byte> needle)
        {
            foreach (var item in haystack)
            {
                if (item.Span.SequenceEqual(needle))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsUnsignedInteger(ReadOnlySpan<byte> value)
        {
            if (value.IsEmpty)
            {
                return false;
            }

            foreach (var b in value)
            {
                if (b is < (byte)'0' or > (byte)'9')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
