using VectorNNTPD.TransitService.Configuration;

namespace VectorNNTPD.TransitService.Email
{
    /// <summary>A protocol-independent mailbox with an optional display name.</summary>
    /// <remarks>
    /// The address is an RFC 5321/5322 mailbox. Display names are sanitized by the
    /// message encoder. Neither field may contain CR or LF.
    /// </remarks>
    public sealed class EmailAddress
    {
        /// <summary>Initializes a new mailbox.</summary>
        /// <param name="address">Mailbox (<c>local@domain</c>).</param>
        /// <param name="displayName">Optional display name.</param>
        public EmailAddress(string address, string? displayName = null)
        {
            if (!EmailOptionsValidator.TryValidateMailbox(address, out var mailbox))
            {
                throw new ArgumentException("Mailbox address is missing or invalid.", nameof(address));
            }

            if (displayName is not null && ContainsLineBreak(displayName))
            {
                throw new ArgumentException("Display name must not contain CR or LF.", nameof(displayName));
            }

            Address = mailbox;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        }

        /// <summary>Gets the mailbox.</summary>
        public string Address { get; }

        /// <summary>Gets the optional display name.</summary>
        public string? DisplayName { get; }

        /// <inheritdoc />
        public override string ToString() => Address;

        internal static bool ContainsLineBreak(ReadOnlySpan<char> value)
        {
            foreach (var ch in value)
            {
                if (ch is '\r' or '\n')
                {
                    return true;
                }
            }

            return false;
        }
    }
}
