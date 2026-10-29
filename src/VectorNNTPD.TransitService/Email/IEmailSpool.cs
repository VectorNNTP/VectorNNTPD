namespace VectorNNTPD.TransitService.Email
{
    /// <summary>
    /// Durable filesystem outbound email spool. This is the queue.
    /// </summary>
    public interface IEmailSpool
    {
        /// <summary>Gets the resolved absolute spool directory.</summary>
        string Directory { get; }

        /// <summary>Gets whether new submissions are accepted.</summary>
        bool IsAccepting { get; }

        /// <summary>Creates the spool directory (and <c>failed</c>) if missing.</summary>
        void EnsureDirectory();

        /// <summary>Stops accepting new submissions. Existing <c>.eml</c> files remain.</summary>
        void StopAccepting();

        /// <summary>
        /// Renames leftover <c>.wrk</c> claims back to <c>.eml</c> so a crash cannot
        /// leave messages stuck in-flight. Does not touch <c>.delivered</c> or
        /// <c>failed/</c>.
        /// </summary>
        void RecoverClaims();

        /// <summary>
        /// Atomically publishes a complete spool file. Returns the final <c>.eml</c> path.
        /// </summary>
        Task<string> WriteAsync(EmailWorkItem item, CancellationToken cancellationToken);

        /// <summary>
        /// Claims the next pending top-level <c>.eml</c> by renaming it to <c>.wrk</c>.
        /// Does not scan <c>failed/</c>, <c>.delivered</c>, <c>.wrk</c>, or <c>.tmp</c>.
        /// In-process callers are serialized. Returns <see langword="null"/> when none are available.
        /// </summary>
        EmailSpoolClaim? TryClaimNext();

        /// <summary>Returns a claimed file to pending <c>.eml</c> after a transient failure or cancel.</summary>
        void Release(EmailSpoolClaim claim);

        /// <summary>
        /// Returns the file to pending <c>.eml</c> but skips it for the rest of this
        /// process lifetime so in-process retries do not hammer SMTP. After restart
        /// the file is eligible again.
        /// </summary>
        void Defer(EmailSpoolClaim claim);

        /// <summary>
        /// Moves a claimed file to <c>failed/</c> after a permanent failure or parse error.
        /// Failed files are never automatically requeued.
        /// </summary>
        void Quarantine(EmailSpoolClaim claim);

        /// <summary>
        /// Deletes the claimed file after SMTP accepted the message.
        /// If delete fails, the file is renamed to <c>.delivered</c> and must never be
        /// automatically retransmitted (SMTP already accepted it).
        /// </summary>
        void Complete(EmailSpoolClaim claim);

        /// <summary>Wakes a waiting delivery worker after a successful write.</summary>
        void Signal();

        /// <summary>Waits for a write signal or <paramref name="timeout"/>, then returns.</summary>
        Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
    }

    /// <summary>One claimed spool file being delivered.</summary>
    public sealed class EmailSpoolClaim
    {
        /// <summary>Initializes a claimed spool item.</summary>
        public EmailSpoolClaim(string id, string workPath, EmailWorkItem item)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentException.ThrowIfNullOrWhiteSpace(workPath);
            ArgumentNullException.ThrowIfNull(item);
            Id = id;
            WorkPath = workPath;
            Item = item;
        }

        /// <summary>Gets the opaque filename id (no extension).</summary>
        public string Id { get; }

        /// <summary>Gets the claimed <c>.wrk</c> path.</summary>
        public string WorkPath { get; }

        /// <summary>Gets the loaded message and envelope.</summary>
        public EmailWorkItem Item { get; }
    }
}
