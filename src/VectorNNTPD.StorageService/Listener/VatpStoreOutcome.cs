namespace VectorNNTPD.StorageService.Listener
{

    /// <summary>VATP RESULT outcome byte written after an article is queued.</summary>
    public static class VatpStoreOutcome
    {
        /// <summary>
        /// The Path-rewritten article was accepted into the in-memory queue.
        /// This is the historical <c>ArticleAcceptOutcome.Accepted</c> value (<c>1</c>).
        /// </summary>
        public const byte Accepted = 1;

        /// <summary>
        /// The article cannot fit in the queue budget, so the body is not authorized.
        /// This is the historical <c>ArticleAcceptOutcome.RejectedCapacity</c> value (<c>6</c>).
        /// A full queue does not use this outcome. The server withholds WINDOW until a reservation fits.
        /// </summary>
        public const byte RejectedCapacity = 6;
    }
}
