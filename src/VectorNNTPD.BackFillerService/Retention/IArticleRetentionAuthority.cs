namespace VectorNNTPD.BackFillerService.Retention
{
    /// <summary>
    /// Single owner of retained CanonicalV1 <see cref="VectorNNTPD.Common.Articles.ArticleRecord"/> values,
    /// identity, TTL, and capacity. Independent of RabbitMQ and NNTP sessions.
    /// <see cref="TryClaimForDelivery"/> is the NNTP delivery claim; the article is removed only when that claim is committed.
    /// </summary>
    /// <remarks>
    /// An openable RequestId is a hard ArticleWork Success capability: FIFO capacity reclaim must not
    /// unindex an entry while <c>OpenableRequestIdCount &gt; 0</c>. An active NNTP delivery claim is also
    /// a FIFO pin, including after its RequestIds are cancelled. TTL expiry is a hard deadline:
    /// at <c>ExpiresUtc</c> the entry is removed from every index, stops counting as retained payload,
    /// and cannot start another delivery. An in-flight delivery claim does not extend that deadline.
    /// Explicit cancel, committed NNTP delivery, and process dispose also end a capability.
    /// </remarks>
    internal interface IArticleRetentionAuthority
    {
        /// <summary>
        /// Fixed interval between <see cref="SweepExpired"/> passes. Set at authority construction and not changed at runtime.
        /// </summary>
        TimeSpan SweepInterval { get; }

        /// <summary>
        /// Sum of authority-owned ArtData bytes, including logically removed entries that still have a delivery reader.
        /// </summary>
        long RetainedPayloadBytes { get; }

        /// <summary>
        /// Entries not yet physically disposed. TTL and committed delivery can drop this before the last reader releases.
        /// </summary>
        int RetainedCount { get; }

        /// <summary>
        /// Retains a CanonicalV1 article record and attaches an Article Work RequestId capability.
        /// </summary>
        /// <param name="messageId">Exact Message-ID. Not normalized.</param>
        /// <param name="requestId">ArticleWork Success RequestId to attach as an openable capability.</param>
        /// <param name="record">CanonicalV1 record. ArtData is referenced on first admission and is not copied.</param>
        /// <param name="selectedDateHeaderName">Date-family header name captured on first admission.</param>
        /// <param name="endpointFqdn">
        /// FQDN captured with the rest of this article's shared snapshot. Null uses the authority's constructed FQDN.
        /// </param>
        /// <returns>
        /// Admission classification, identity when one was resolved, the listener endpoint when the article
        /// is available, and the payload-byte totals after the attempt.
        /// </returns>
        /// <remarks>
        /// Does not copy ArtData. On AlreadyPresent, the existing retained
        /// <see cref="VectorNNTPD.Common.Articles.ArticleRecord"/> remains authoritative (first-wins);
        /// the new RequestId is added as an additional openable capability and does not revoke prior
        /// RequestIds. When the per-article openable RequestId bound is reached, admission is rejected
        /// without modifying existing RequestIds or ArtData.
        /// The caller retains ownership of the unused incoming ArtData buffer on AlreadyPresent.
        /// When capacity is insufficient, FIFO reclaim may remove only entries with no openable
        /// RequestIds; otherwise admission fails with <see cref="ArticleRetentionKind.CapacityUnavailable"/>.
        /// </remarks>
        ArticleRetentionResult RetainCanonical(
            string messageId,
            Guid requestId,
            VectorNNTPD.Common.Articles.ArticleRecord record,
            VectorNNTPD.Common.Articles.Parsing.NntpArticleHeaderName selectedDateHeaderName,
            string? endpointFqdn = null);

        /// <summary>
        /// Claims one retained article for an NNTP <c>ARTICLE</c> delivery.
        /// </summary>
        /// <param name="messageIdOctets">
        /// Message-ID octets, including angle brackets. Compared to the stored key with the same
        /// low-8-bit match as article admission. Not trimmed or case-folded.
        /// </param>
        /// <returns>
        /// <see cref="ArticleDeliveryClaimKind.Claimed"/> with a claim whose
        /// <see cref="ArticleRetentionAuthority.ArticleDeliveryClaim.ArticleBytes"/> references the existing ArtData,
        /// or a rejection that does not claim the entry and does not consume a RequestId.
        /// </returns>
        /// <remarks>
        /// Does not copy ArtData. Does not remove the entry. RequestIds stay openable until
        /// <see cref="ArticleRetentionAuthority.ArticleDeliveryClaim.Commit"/>.
        /// An entry whose lines are not exclusively CRLF, or that has a dot-leading line and no header/body
        /// separator, is <see cref="ArticleDeliveryClaimKind.NotFrameable"/>. The retained buffer is not rewritten.
        /// A second claim while one is active is <see cref="ArticleDeliveryClaimKind.InDelivery"/>.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">The authority has been disposed.</exception>
        ArticleDeliveryClaimResult TryClaimForDelivery(ReadOnlySpan<byte> messageIdOctets);

        /// <summary>Cancels one openable RequestId without releasing the Message-ID entry.</summary>
        /// <param name="requestId">Openable RequestId to detach.</param>
        /// <returns>
        /// <see langword="true"/> when that RequestId was removed.
        /// <see langword="false"/> when it was not an openable capability.
        /// </returns>
        bool TryCancelPendingRequest(Guid requestId);

        /// <summary>Reclaims TTL-expired entries.</summary>
        /// <returns>Payload bytes physically released by this pass. Zero when nothing was physically released.</returns>
        long SweepExpired();

        /// <summary>Stops new admissions.</summary>
        /// <remarks>
        /// Closes <see cref="RetainCanonical"/> only. Existing entries, openable RequestIds,
        /// <see cref="TryClaimForDelivery"/>, <see cref="TryCancelPendingRequest"/>, and
        /// <see cref="SweepExpired"/> stay available until the authority is disposed.
        /// </remarks>
        void BeginShutdown();
    }
}
