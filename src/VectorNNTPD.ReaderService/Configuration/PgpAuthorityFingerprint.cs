using System.Diagnostics.CodeAnalysis;

namespace VectorNNTPD.ReaderService.Configuration
{
    /// <summary>
    /// Normalizes PGP key fingerprints from ISC/INN <c>control.ctl</c> grouping
    /// into a machine-readable uppercase hexadecimal form.
    /// </summary>
    public static class PgpAuthorityFingerprint
    {
        /// <summary>
        /// Attempts to normalize a fingerprint to uppercase hexadecimal without
        /// whitespace.
        /// </summary>
        /// <param name="value">Source fingerprint text, possibly grouped with spaces.</param>
        /// <param name="normalized">Normalized fingerprint when the method returns <see langword="true"/>.</param>
        /// <returns>
        /// <see langword="true"/> when <paramref name="value"/> contains only hexadecimal
        /// digits and whitespace, is non-empty after stripping whitespace, and has an
        /// even number of hex digits.
        /// </returns>
        /// <remarks>
        /// Normalization removes whitespace and uppercases hex digits. It does not
        /// reinterpret or invent bytes.
        /// </remarks>
        public static bool TryNormalize(string? value, [NotNullWhen(true)] out string? normalized)
        {
            normalized = null;
            if (value is null)
            {
                return false;
            }

            var hexLength = 0;
            foreach (var c in value)
            {
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                if (!IsHex(c))
                {
                    return false;
                }

                hexLength++;
            }

            if (hexLength == 0 || (hexLength & 1) != 0)
            {
                return false;
            }

            var chars = new char[hexLength];
            var i = 0;
            foreach (var c in value)
            {
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                chars[i++] = char.ToUpperInvariant(c);
            }

            normalized = new string(chars);
            return true;
        }

        /// <summary>
        /// Normalizes a fingerprint or throws when the value is not hexadecimal.
        /// </summary>
        /// <param name="value">Source fingerprint text.</param>
        /// <returns>Uppercase hexadecimal without whitespace.</returns>
        /// <exception cref="ArgumentException">The value is not a fingerprint.</exception>
        public static string Normalize(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!TryNormalize(value, out var normalized))
            {
                throw new ArgumentException("Value is not a hexadecimal PGP key fingerprint.", nameof(value));
            }

            return normalized;
        }

        private static bool IsHex(char c) =>
            c is (>= '0' and <= '9') or (>= 'A' and <= 'F') or (>= 'a' and <= 'f');
    }
}
