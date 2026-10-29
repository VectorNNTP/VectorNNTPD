namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// Top-level outbound email options for the generic <c>Email</c> subsystem.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The configuration section name is <see cref="SectionName"/> (<c>Email</c>;
    /// case-insensitive). This is application email infrastructure, not an NNTP or
    /// moderation setting. Moderation is one producer.
    /// </para>
    /// <para>
    /// When <see cref="Enabled"/> is <see langword="false"/> (the production default),
    /// <c>IEmailService.SendAsync</c> rejects immediately. No spool file is written
    /// and no SMTP connection is opened. SMTP host and credentials are not required.
    /// </para>
    /// <para>
    /// Credentials must be supplied via environment variables
    /// (<c>Email__Smtp__Username</c>, <c>Email__Smtp__Password</c>) or a secret store.
    /// Do not place real credentials in <c>VectorNNTPD.TransitService.json</c>.
    /// </para>
    /// </remarks>
    public sealed class EmailOptions
    {
        /// <summary>Top-level configuration section name.</summary>
        public const string SectionName = "Email";

        /// <summary>
        /// Gets or sets whether the outbound email subsystem accepts work.
        /// Default <see langword="false"/>.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the default RFC 5322 From mailbox used when a producer
        /// does not supply one. Required when <see cref="Enabled"/> is <see langword="true"/>.
        /// </summary>
        public string DefaultFrom { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the SMTP envelope sender (<c>MAIL FROM</c>).
        /// When empty, <see cref="DefaultFrom"/> is used. Never derived from recipients.
        /// </summary>
        public string EnvelopeSender { get; set; } = string.Empty;

        /// <summary>Gets or sets SMTP transport options.</summary>
        public SmtpOptions Smtp { get; set; } = new();

        /// <summary>Gets or sets the durable filesystem spool options.</summary>
        public EmailSpoolOptions Spool { get; set; } = new();
    }
}
