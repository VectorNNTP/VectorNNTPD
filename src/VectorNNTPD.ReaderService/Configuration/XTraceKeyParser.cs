using System.Diagnostics.CodeAnalysis;

namespace VectorNNTPD.ReaderService.Configuration
{
    /// <summary>
    /// Decodes the persisted POST <c>X-Trace</c> AES-256 key from configuration text.
    /// </summary>
    /// <remarks>
    /// Accepts 64 hex characters or Base64 of exactly 32 bytes. Does not generate keys.
    /// Failure paths must not include the supplied secret in messages.
    /// </remarks>
    public static class XTraceKeyParser
    {
        /// <summary>Required AES-256 key length in bytes.</summary>
        public const int KeyLength = 32;

        /// <summary>
        /// Attempts to decode <paramref name="text"/> into a 32-byte AES-256 key.
        /// </summary>
        public static bool TryDecode(string? text, [NotNullWhen(true)] out byte[]? key)
        {
            key = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var trimmed = text.Trim();
            if (trimmed.Length == KeyLength * 2)
            {
                try
                {
                    var hex = Convert.FromHexString(trimmed);
                    if (hex.Length == KeyLength)
                    {
                        key = hex;
                        return true;
                    }
                }
                catch (FormatException)
                {
                    return false;
                }
            }

            Span<byte> buffer = stackalloc byte[KeyLength];
            if (Convert.TryFromBase64String(trimmed, buffer, out var written) && written == KeyLength)
            {
                key = buffer.ToArray();
                return true;
            }

            return false;
        }
    }
}
