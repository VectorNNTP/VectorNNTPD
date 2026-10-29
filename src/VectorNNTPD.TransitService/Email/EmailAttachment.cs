namespace VectorNNTPD.TransitService.Email
{
    /// <summary>An optional MIME attachment.</summary>
    /// <param name="FileName">Sanitized file name (no path separators, CR, or LF).</param>
    /// <param name="ContentType">MIME type (default <c>application/octet-stream</c>).</param>
    /// <param name="Content">Raw attachment bytes.</param>
    public sealed record EmailAttachment(
        string FileName,
        ReadOnlyMemory<byte> Content,
        string ContentType = "application/octet-stream")
    {
        /// <summary>Validates that the file name cannot inject headers or paths.</summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(FileName)
                || EmailAddress.ContainsLineBreak(FileName)
                || FileName.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            {
                throw new ArgumentException("Attachment file name is invalid.", nameof(FileName));
            }

            if (string.IsNullOrWhiteSpace(ContentType)
                || EmailAddress.ContainsLineBreak(ContentType)
                || ContentType.Contains('\0'))
            {
                throw new ArgumentException("Attachment content type is invalid.", nameof(ContentType));
            }
        }
    }
}
