using VectorNNTPD.ReaderService.Email;

namespace VectorNNTPD.ReaderService.Ninpaths
{
    /// <summary>
    /// Builds one <see cref="EmailMessage"/> for a compact ninpaths dump (sendinpaths To/Subject/Auto-Submitted).
    /// </summary>
    internal static class NinpathsReportMail
    {
        /// <summary>
        /// Creates a single message with every recipient in <c>To</c>. Returns
        /// <see langword="null"/> when there is no dump or no recipients.
        /// </summary>
        public static EmailMessage? TryCreate(
            ReadOnlyMemory<byte> report,
            string fqdn,
            string fromMailbox,
            IReadOnlyList<string> recipients)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
            ArgumentException.ThrowIfNullOrWhiteSpace(fromMailbox);
            ArgumentNullException.ThrowIfNull(recipients);
            if (report.IsEmpty || recipients.Count == 0)
            {
                return null;
            }

            var to = new EmailAddress[recipients.Count];
            for (var i = 0; i < recipients.Count; i++)
            {
                to[i] = new EmailAddress(recipients[i]);
            }

            return new EmailMessage
            {
                From = new EmailAddress(fromMailbox),
                To = to,
                Subject = NinpathsConstants.EmailSubjectPrefix + fqdn,
                Body = report,
                ContentType = "text/plain",
                Charset = "us-ascii",
                Headers = [new EmailHeader("Auto-Submitted", "auto-generated")],
            };
        }
    }
}
