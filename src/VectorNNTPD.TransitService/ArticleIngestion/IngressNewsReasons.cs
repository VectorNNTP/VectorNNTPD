using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles.Processing;
using VectorNNTPD.TransitService.Newsgroups;
using VectorNNTPD.TransitService.Session.Commands.Posting;

namespace VectorNNTPD.TransitService.ArticleIngestion
{
    /// <summary>
    /// Maps already-decided ingress failures to concise operator-facing news reasons.
    /// </summary>
    /// <remarks>
    /// The formatter does not inspect parser enums, response codes, or validator
    /// state. Callers supply the existing decision value; this type only chooses
    /// the news-journal text. It does not change NNTP responses or admission.
    /// </remarks>
    internal static class IngressNewsReasons
    {
        /// <summary>Invalid or missing Message-ID.</summary>
        internal const string MessageIdInvalid = "message-id invalid";

        /// <summary>Invalid or missing Date.</summary>
        internal const string DateInvalid = "date invalid";

        /// <summary>
        /// Unknown/non-carried Newsgroups: WantTrash=false rejection, or WantTrash=true junk.
        /// Decision sites append <c>: group[, group...]</c> via <see cref="WithGroups"/>.
        /// </summary>
        internal const string NewsgroupNotCarried = "newsgroup not carried";

        /// <summary>Receive newsfeeds expression did not subscribe the article.</summary>
        internal const string NewsgroupPatternRejected = "newsgroup pattern rejected";

        /// <summary>RFC 6048 <c>j</c> / <see cref="NewsgroupPostingStatus.PeerOnly"/> catalogue hit.</summary>
        internal const string PeerOnly = "peer-only";

        /// <summary>Receive-size or ArticleRecord size rejection.</summary>
        internal const string ArticleTooLarge = "article too large";

        /// <summary>Session article-type capability rejection.</summary>
        internal const string ArticleTypeNotPermitted = "article type not permitted";

        /// <summary>Hard queue memory-budget rejection (<see cref="ArticleEnqueueResult.Rejected"/>).</summary>
        internal const string QueueCapacityExceeded = "queue capacity exceeded";

        /// <summary>yEnc markers present and validation failed.</summary>
        internal const string YEncodingInvalid = "yEncoding invalid";

        /// <summary>
        /// Formats an already-decided prefix plus the responsible group names.
        /// </summary>
        /// <param name="prefix">Already-decided reason prefix.</param>
        /// <param name="groups">Responsible group names in header order.</param>
        /// <returns>The prefix, or <c>prefix: name[, name...]</c>.</returns>
        /// <remarks>
        /// Does not inspect catalogue or header state. Callers supply the groups
        /// that caused the existing decision, in header order.
        /// </remarks>
        internal static string WithGroups(string prefix, IReadOnlyList<string> groups)
        {
            ArgumentException.ThrowIfNullOrEmpty(prefix);
            ArgumentNullException.ThrowIfNull(groups);
            if (groups.Count == 0)
            {
                return prefix;
            }

            var builder = new StringBuilder(prefix.Length + 2 + (groups.Count * 16));
            builder.Append(prefix);
            builder.Append(": ");
            for (var i = 0; i < groups.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(groups[i]);
            }

            return builder.ToString();
        }

        /// <summary>Maps an ArticleRecord construction failure to a news reason.</summary>
        public static string ForArticleRecord(in ArticleRecordCreateResult created)
        {
            if (created.ParseFailure != NntpArticleParseFailureCode.None)
            {
                return ForParseFailure(created.ParseFailure);
            }

            return ForMaterializeFailure(created.MaterializeFailure);
        }

        /// <summary>Maps a parser failure code already produced by ArticleRecord construction.</summary>
        public static string ForParseFailure(NntpArticleParseFailureCode code) =>
            code switch
            {
                NntpArticleParseFailureCode.YEncDecodingFailed => YEncodingInvalid,
                NntpArticleParseFailureCode.InvalidMessageId
                    or NntpArticleParseFailureCode.MissingMessageId => MessageIdInvalid,
                NntpArticleParseFailureCode.MissingOrInvalidDate => DateInvalid,
                NntpArticleParseFailureCode.ArticleTooLarge => ArticleTooLarge,
                NntpArticleParseFailureCode.EmptyArticle => "empty article",
                NntpArticleParseFailureCode.MissingHeaderBodySeparator => "missing header/body separator",
                NntpArticleParseFailureCode.HeaderSectionTooLarge => "header too large",
                NntpArticleParseFailureCode.TooManyHeaders => "too many headers",
                NntpArticleParseFailureCode.HeaderLineTooLong => "header line too long",
                NntpArticleParseFailureCode.MalformedHeader
                    or NntpArticleParseFailureCode.MalformedHeaderContinuation => "malformed header",
                NntpArticleParseFailureCode.HeaderNameTooLong => "header name too long",
                NntpArticleParseFailureCode.HeaderValueTooLong => "header value too long",
                NntpArticleParseFailureCode.ContainsNul => "embedded NUL",
                NntpArticleParseFailureCode.ContainsIllegalControlByte => "illegal control byte",
                NntpArticleParseFailureCode.MissingNewsgroups => "newsgroups missing",
                NntpArticleParseFailureCode.InvalidNewsgroups => "newsgroups invalid",
                NntpArticleParseFailureCode.InvalidFrom => "from invalid",
                NntpArticleParseFailureCode.InvalidPath => "path invalid",
                NntpArticleParseFailureCode.DuplicateMessageId => "duplicate message-id",
                NntpArticleParseFailureCode.DuplicateNewsgroups => "duplicate newsgroups",
                NntpArticleParseFailureCode.DuplicatePath => "duplicate path",
                NntpArticleParseFailureCode.BodyLineTooLong => "body line too long",
                _ => "article rejected",
            };

        /// <summary>Maps a materializer failure code already produced by ArticleRecord construction.</summary>
        public static string ForMaterializeFailure(NntpArticleCanonicalFailureCode code) =>
            code switch
            {
                NntpArticleCanonicalFailureCode.ArticleTooLarge => ArticleTooLarge,
                NntpArticleCanonicalFailureCode.MissingSelectedDateHeader => DateInvalid,
                NntpArticleCanonicalFailureCode.EmptyArticle => "empty article",
                NntpArticleCanonicalFailureCode.DuplicatePath => "duplicate path",
                NntpArticleCanonicalFailureCode.InvalidHeaderSeparator => "malformed header",
                NntpArticleCanonicalFailureCode.DateLineTooLong => "date line too long",
                NntpArticleCanonicalFailureCode.PathRewriteLineTooLong
                    or NntpArticleCanonicalFailureCode.PathInsertionLineTooLong => "path invalid",
                _ => "article record rejected",
            };

        /// <summary>
        /// Maps an existing IHAVE/TAKETHIS reject-detail string (kept for command
        /// completion / TX logs) to the news-journal reason.
        /// </summary>
        public static string ForExistingRejectDetail(string? detail)
        {
            if (string.IsNullOrEmpty(detail))
            {
                return "article rejected";
            }

            if (detail is "rejected too large")
            {
                return ArticleTooLarge;
            }

            if (detail is "rejected article type")
            {
                return ArticleTypeNotPermitted;
            }

            if (detail is "rejected newsgroup pattern")
            {
                return NewsgroupPatternRejected;
            }

            if (detail is "rejected exceeds queue budget")
            {
                return QueueCapacityExceeded;
            }

            if (detail.StartsWith("rejected article record ", StringComparison.Ordinal))
            {
                var token = detail["rejected article record ".Length..];
                if (Enum.TryParse(token, out NntpArticleParseFailureCode parse)
                    && parse != NntpArticleParseFailureCode.None)
                {
                    return ForParseFailure(parse);
                }

                if (Enum.TryParse(token, out NntpArticleCanonicalFailureCode materialize)
                    && materialize != NntpArticleCanonicalFailureCode.None)
                {
                    return ForMaterializeFailure(materialize);
                }
            }

            return detail;
        }

        /// <summary>Maps a POST <see cref="PostingFailure"/> already produced by the command path.</summary>
        public static string ForPostingFailure(in PostingFailure failure)
        {
            if (TryMapArticleRecordEnumName(failure.Detail, out var fromEnum))
            {
                return fromEnum;
            }

            return failure.Category switch
            {
                PostingFailureCategory.ArticleTooLarge => ArticleTooLarge,
                PostingFailureCategory.InvalidMessageId => MessageIdInvalid,
                PostingFailureCategory.InvalidDate => DateInvalid,
                PostingFailureCategory.PolicyRejected when failure.Detail == "arttype-capability"
                    => ArticleTypeNotPermitted,
                PostingFailureCategory.PolicyRejected when failure.Detail == "unknown newsgroup"
                    => NewsgroupNotCarried,
                PostingFailureCategory.PersistenceFailure when failure.Detail is "Rejected" or "Full"
                    => QueueCapacityExceeded,
                _ => string.IsNullOrEmpty(failure.Detail) ? "article rejected" : failure.Detail,
            };
        }

        private static bool TryMapArticleRecordEnumName(string? detail, out string reason)
        {
            if (!string.IsNullOrEmpty(detail)
                && Enum.TryParse(detail, out NntpArticleParseFailureCode parse)
                && parse != NntpArticleParseFailureCode.None)
            {
                reason = ForParseFailure(parse);
                return true;
            }

            if (!string.IsNullOrEmpty(detail)
                && Enum.TryParse(detail, out NntpArticleCanonicalFailureCode materialize)
                && materialize != NntpArticleCanonicalFailureCode.None)
            {
                reason = ForMaterializeFailure(materialize);
                return true;
            }

            reason = string.Empty;
            return false;
        }
    }
}
