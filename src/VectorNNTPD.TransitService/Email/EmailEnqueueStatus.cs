namespace VectorNNTPD.TransitService.Email
{
    /// <summary>
    /// Result of <see cref="IEmailService.SendAsync"/>. This is durable local spool
    /// acceptance, not remote SMTP delivery.
    /// </summary>
    public enum EmailEnqueueStatus
    {
        /// <summary>
        /// The complete message was atomically written to the local filesystem spool.
        /// Not proof of remote SMTP receipt.
        /// </summary>
        Accepted = 0,

        /// <summary>The outbound subsystem is disabled. No spool file was written.</summary>
        Disabled = 1,

        /// <summary>The message failed validation or encoding. No spool file was written.</summary>
        Rejected = 2,

        /// <summary>The service is stopping and is not accepting new submissions.</summary>
        Unavailable = 3,

        /// <summary>The spool file could not be created (I/O, permission, disk, or cancellation).</summary>
        Failed = 4,
    }

    /// <summary>Outcome of one <see cref="IEmailService.SendAsync"/> call.</summary>
    /// <param name="Status">Local spool acceptance status.</param>
    /// <param name="Detail">Internal detail (never a credential).</param>
    public readonly record struct EmailEnqueueResult(EmailEnqueueStatus Status, string Detail);
}
