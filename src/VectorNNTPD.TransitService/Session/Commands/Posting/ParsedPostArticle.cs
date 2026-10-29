namespace VectorNNTPD.TransitService.Session.Commands.Posting
{
    /// <summary>One structurally parsed header field from a destuffed POST article.</summary>
    /// <param name="Name">Original field-name octets (case preserved).</param>
    /// <param name="UnfoldedValue">Logical field body with folding CRLF removed.</param>
    /// <param name="RawField">Original field octets including folding and the terminating CRLF.</param>
    internal readonly record struct ParsedPostHeader(
        ReadOnlyMemory<byte> Name,
        ReadOnlyMemory<byte> UnfoldedValue,
        ReadOnlyMemory<byte> RawField);

    /// <summary>Destuffed POST article after one structural header parse.</summary>
    internal sealed class ParsedPostArticle
    {
        /// <summary>Initializes a new instance of the <see cref="ParsedPostArticle"/> class.</summary>
        public ParsedPostArticle(
            ReadOnlyMemory<byte> payload,
            IReadOnlyList<ParsedPostHeader> headers,
            ReadOnlyMemory<byte> body,
            int headerBlockBytes)
        {
            Payload = payload;
            Headers = headers;
            Body = body;
            HeaderBlockBytes = headerBlockBytes;
        }

        /// <summary>Gets the destuffed received payload (headers + separator + body).</summary>
        public ReadOnlyMemory<byte> Payload { get; }

        /// <summary>Gets parsed headers in wire order.</summary>
        public IReadOnlyList<ParsedPostHeader> Headers { get; }

        /// <summary>Gets the destuffed body (may be empty).</summary>
        public ReadOnlyMemory<byte> Body { get; }

        /// <summary>Gets destuffed header-block length including the blank separator CRLF.</summary>
        public int HeaderBlockBytes { get; }

        /// <summary>Gets destuffed article size (payload length; terminator excluded).</summary>
        public int ArticleSize => Payload.Length;

        /// <summary>Gets or sets the article Message-ID (supplied or synthesized).</summary>
        public string? MessageId { get; set; }

        /// <summary>Gets or sets whether <see cref="MessageId"/> was synthesized by the server.</summary>
        public bool MessageIdSynthesized { get; set; }

        /// <summary>Gets or sets syntax-validated newsgroup names.</summary>
        public string[] Newsgroups { get; set; } = [];

        /// <summary>Gets or sets the parsed author <c>Date:</c>, or the injection time when synthesized.</summary>
        public DateTimeOffset AuthorDate { get; set; }

        /// <summary>Gets or sets whether <see cref="AuthorDate"/> was generated because the client omitted <c>Date:</c>.</summary>
        public bool DateSynthesized { get; set; }

        /// <summary>Gets or sets whether a structurally valid <c>Approved:</c> is present.</summary>
        public bool ApprovedPresent { get; set; }

        /// <summary>
        /// Gets or sets the distinct mailbox identities parsed from <c>Approved:</c>.
        /// Empty when the header is absent.
        /// </summary>
        public string[] ApprovedIdentities { get; set; } = [];

        /// <summary>Gets or sets the catalogue classification for this article's newsgroups.</summary>
        public NewsgroupCatalogStatus CatalogStatus { get; set; }

        /// <summary>
        /// Gets or sets moderated <c>Newsgroups:</c> targets in header order when
        /// <see cref="CatalogStatus"/> is <see cref="NewsgroupCatalogStatus.RequiresModeration"/>.
        /// </summary>
        public string[] ModeratedGroups { get; set; } = [];

        /// <summary>Returns the first header whose name matches <paramref name="upperName"/>.</summary>
        public bool TryGetHeader(ReadOnlySpan<byte> upperName, out ParsedPostHeader header)
        {
            foreach (var candidate in Headers)
            {
                if (PostFieldSyntax.EqualsFolded(candidate.Name.Span, upperName))
                {
                    header = candidate;
                    return true;
                }
            }

            header = default;
            return false;
        }
    }
}
