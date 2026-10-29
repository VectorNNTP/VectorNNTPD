using System.Text;

namespace VectorNNTPD.TransitService.Moderation
{
    /// <summary>
    /// INN <c>samples/moderators</c> address-template expansion.
    /// </summary>
    /// <remarks>
    /// <c>%s</c> is replaced with the matched newsgroup name after <c>.</c> is
    /// changed to <c>-</c>. The result is a routing mailbox for unapproved
    /// submission. Expansion does not authorize an NNTP principal.
    /// </remarks>
    public static class ModeratorAddressTemplate
    {
        /// <summary>INN substitution token.</summary>
        public const string Token = "%s";

        /// <summary>
        /// Returns whether <paramref name="address"/> contains the INN <c>%s</c> token.
        /// </summary>
        public static bool IsTemplate(ReadOnlySpan<char> address) =>
            address.IndexOf(Token, StringComparison.Ordinal) >= 0;

        /// <summary>
        /// Expands a static mailbox or an INN <c>%s</c> template against
        /// <paramref name="newsgroup"/>.
        /// </summary>
        public static string Expand(string address, ReadOnlySpan<byte> newsgroup)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(address);
            if (!IsTemplate(address))
            {
                return address;
            }

            var name = Encoding.ASCII.GetString(newsgroup);
            var substituted = name.Replace('.', '-');
            return address.Replace(Token, substituted, StringComparison.Ordinal);
        }

        /// <summary>
        /// Validates a static mailbox or a single-<c>%s</c> INN template.
        /// </summary>
        public static bool TryValidate(string address)
        {
            if (string.IsNullOrWhiteSpace(address) || !IsAscii(address))
            {
                return false;
            }

            var tokenCount = 0;
            var i = 0;
            while (i < address.Length)
            {
                if (address[i] == '%')
                {
                    if (i + 1 >= address.Length || address[i + 1] != 's')
                    {
                        return false;
                    }

                    tokenCount++;
                    i += 2;
                    continue;
                }

                i++;
            }

            if (tokenCount > 1)
            {
                return false;
            }

            var candidate = tokenCount == 0
                ? address
                : address.Replace(Token, "x", StringComparison.Ordinal);
            return IsMailboxIdentity(candidate);
        }

        private static bool IsMailboxIdentity(string value)
        {
            var at = value.IndexOf('@');
            return at > 0
                && at == value.LastIndexOf('@')
                && at < value.Length - 1;
        }

        private static bool IsAscii(string value)
        {
            foreach (var c in value)
            {
                if (c > 127)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
