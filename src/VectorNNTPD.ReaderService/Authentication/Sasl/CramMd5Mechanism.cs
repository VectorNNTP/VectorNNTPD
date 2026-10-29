using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VectorNNTPD.ReaderService.Authentication.Sasl
{
    /// <summary>
    /// RFC 2195 CRAM-MD5 native challenge and verification.
    /// </summary>
    /// <remarks>
    /// The mechanism challenge is an RFC 822 <c>msg-id</c>
    /// <c>&lt;random-digits.timestamp@fqdn&gt;</c>. HMAC-MD5 is computed over that
    /// native string. NNTP AUTHINFO SASL Base64-encodes the same octets in a
    /// <c>383</c> reply (RFC 4643); that encoding is not part of CRAM-MD5.
    /// </remarks>
    internal static partial class CramMd5Mechanism
    {
        [GeneratedRegex(@"^<[0-9]+\.[0-9]+@[^@<>\s]+>$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
        private static partial Regex ChallengeForm();

        /// <summary>
        /// Creates a native RFC 2195 challenge using random digits, a UTC Unix
        /// timestamp, and the application's Nntpd FQDN.
        /// </summary>
        public static string CreateChallenge(string fqdn, TimeProvider? timeProvider = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
            var host = NormalizeFqdn(fqdn);
            var unique = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
            var timestamp = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
            return string.Create(
                CultureInfo.InvariantCulture,
                $"<{unique}.{timestamp}@{host}>");
        }

        /// <summary>Returns <see langword="true"/> when <paramref name="challenge"/> matches RFC 2195 msg-id form.</summary>
        public static bool IsRfc2195Challenge(ReadOnlySpan<char> challenge) =>
            ChallengeForm().IsMatch(challenge);

        /// <summary>Verifies <c>username hexhmac</c> against HMAC-MD5 of the native challenge octets.</summary>
        public static bool Verify(string username, string response, string challenge, ReadOnlySpan<byte> secret)
        {
            ArgumentException.ThrowIfNullOrEmpty(username);
            ArgumentNullException.ThrowIfNull(response);
            ArgumentException.ThrowIfNullOrEmpty(challenge);
            var space = response.IndexOf(' ');
            if (space <= 0)
            {
                return false;
            }

            var respUser = response[..space];
            if (!string.Equals(respUser, username, StringComparison.Ordinal))
            {
                return false;
            }

            var expectedHex = ComputeHexDigest(secret, challenge);
            return string.Equals(response[(space + 1)..], expectedHex, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Computes the lowercase hex HMAC-MD5 of the native CRAM challenge.</summary>
        public static string ComputeHexDigest(ReadOnlySpan<byte> secret, string nativeChallenge)
        {
            ArgumentException.ThrowIfNullOrEmpty(nativeChallenge);
#pragma warning disable CA5351
            using var hmac = new HMACMD5(secret.ToArray());
#pragma warning restore CA5351
            return Convert.ToHexString(hmac.ComputeHash(Encoding.ASCII.GetBytes(nativeChallenge))).ToLowerInvariant();
        }

        /// <summary>Encodes an ASCII password as HMAC key bytes.</summary>
        public static byte[] SecretFromPassword(string password)
        {
            ArgumentNullException.ThrowIfNull(password);
            return Encoding.ASCII.GetBytes(password);
        }

        private static string NormalizeFqdn(string fqdn)
        {
            var host = fqdn.Trim().TrimEnd('.');
            if (host.Length == 0
                || host.Contains('@', StringComparison.Ordinal)
                || host.Contains('<', StringComparison.Ordinal)
                || host.Contains('>', StringComparison.Ordinal)
                || host.Contains(' ', StringComparison.Ordinal))
            {
                throw new ArgumentException("CRAM-MD5 host identity must be the Nntpd FQDN.", nameof(fqdn));
            }

            return host;
        }
    }
}
