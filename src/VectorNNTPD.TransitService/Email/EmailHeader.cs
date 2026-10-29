namespace VectorNNTPD.TransitService.Email
{
    /// <summary>A custom RFC 5322 header field.</summary>
    /// <param name="Name">Header field name (token; no colon, CR, or LF).</param>
    /// <param name="Value">Unfolded field body (no CR or LF).</param>
    public sealed record EmailHeader(string Name, string Value)
    {
        /// <summary>Validates that <see cref="Name"/> and <see cref="Value"/> cannot inject headers.</summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name)
                || Name.Contains(':')
                || EmailAddress.ContainsLineBreak(Name)
                || !IsHeaderName(Name))
            {
                throw new ArgumentException("Header name is invalid or contains injection characters.", nameof(Name));
            }

            if (Value is null || EmailAddress.ContainsLineBreak(Value))
            {
                throw new ArgumentException("Header value must not contain CR or LF.", nameof(Value));
            }
        }

        private static bool IsHeaderName(string name)
        {
            foreach (var ch in name)
            {
                if (ch is < (char)33 or > (char)126 || ch == ':')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
