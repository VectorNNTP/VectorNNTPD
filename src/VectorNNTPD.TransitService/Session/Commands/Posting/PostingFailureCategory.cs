namespace VectorNNTPD.TransitService.Session.Commands.Posting
{
    /// <summary>Internal POST validation/persistence failure category for structured logs.</summary>
    /// <remarks>Never copied onto the NNTP wire. Clients always receive <c>441 Posting failed</c>.</remarks>
    public enum PostingFailureCategory
    {
        /// <summary>Destuffed article exceeded <c>Nntpd:MaxArticleSize</c> during receive.</summary>
        ArticleTooLarge = 0,

        /// <summary>Header line, folding, field-name, or CR/LF structure is invalid.</summary>
        MalformedHeader = 1,

        /// <summary>A required header is absent.</summary>
        MissingRequiredHeader = 2,

        /// <summary>A singleton header occurred more than once.</summary>
        DuplicateHeader = 3,

        /// <summary>Message-ID syntax is invalid.</summary>
        InvalidMessageId = 4,

        /// <summary>Date syntax is invalid or outside server date policy.</summary>
        InvalidDate = 5,

        /// <summary>Newsgroups syntax is invalid.</summary>
        InvalidNewsgroups = 6,

        /// <summary>Path generation/normalization failed.</summary>
        InvalidPath = 7,

        /// <summary>Followup-To syntax is invalid.</summary>
        InvalidFollowupTo = 8,

        /// <summary>References / In-Reply-To syntax or consistency is invalid.</summary>
        InvalidReferences = 9,

        /// <summary>Distribution syntax or policy is invalid.</summary>
        InvalidDistribution = 10,

        /// <summary>Control / unsupported control semantics.</summary>
        InvalidControl = 11,

        /// <summary>Approved syntax is invalid.</summary>
        InvalidApproved = 12,

        /// <summary>HistoryDB already knows this Message-ID.</summary>
        DuplicateArticle = 13,

        /// <summary>Session or isolated policy rejected the article.</summary>
        PolicyRejected = 14,

        /// <summary>Ingestion/history persistence did not accept the article.</summary>
        PersistenceFailure = 15,

        /// <summary>
        /// An <c>Approved:</c> identity was present but the authenticated principal is not
        /// authorized to approve every moderated target.
        /// </summary>
        UnauthorizedApproval = 16,

        /// <summary>
        /// The proto-article required moderator forwarding and submission was unavailable or failed.
        /// </summary>
        ModerationForwardingFailed = 17,
    }

    /// <summary>A POST validation or persistence failure.</summary>
    /// <param name="Category">Structured failure category.</param>
    /// <param name="Detail">Short internal detail (not sent to the client).</param>
    public readonly record struct PostingFailure(PostingFailureCategory Category, string Detail);
}
