using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace VectorNNTPD.ReaderService.Authentication.Sasl
{
    /// <summary>RFC 5802 SCRAM-SHA-256 server-side exchange using stored keys (no PBKDF2 at auth time).</summary>
    internal sealed class ScramMechanism
    {
        private readonly ScramStoredCredential _credential;
        private readonly string _gs2Header;
        private readonly string _clientFirstBare;
        private readonly string _serverFirst;
        private readonly string _combinedNonce;

        private ScramMechanism(
            ScramStoredCredential credential,
            string gs2Header,
            string clientFirstBare,
            string serverFirst,
            string combinedNonce)
        {
            _credential = credential;
            _gs2Header = gs2Header;
            _clientFirstBare = clientFirstBare;
            _serverFirst = serverFirst;
            _combinedNonce = combinedNonce;
        }

        /// <summary>Begins SCRAM with a client-first message and returns server-first.</summary>
        public static (ScramMechanism State, string ServerFirst) Begin(string clientFirst, ScramStoredCredential credential)
        {
            if (!TrySplitClientFirst(clientFirst, out var gs2Header, out var clientFirstBare)
                || !TryParseAttribute(clientFirstBare, 'r', out var clientNonce)
                || string.IsNullOrEmpty(clientNonce))
            {
                throw new ArgumentException("Invalid SCRAM client-first message.", nameof(clientFirst));
            }

            var serverNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
            var combinedNonce = clientNonce + serverNonce;
            var saltB64 = Convert.ToBase64String(credential.Salt.Span);
            var serverFirst = $"r={combinedNonce},s={saltB64},i={credential.IterationCount}";
            return (new ScramMechanism(credential, gs2Header, clientFirstBare, serverFirst, combinedNonce), serverFirst);
        }

        /// <summary>Verifies client-final and returns <c>v=</c> server-final, or <see langword="null"/> on failure.</summary>
        public string? TryFinish(string clientFinal)
        {
            if (!TryParseAttribute(clientFinal, 'p', out var proofB64)
                || !TryParseAttribute(clientFinal, 'r', out var combinedNonce)
                || !TryParseAttribute(clientFinal, 'c', out var channelBinding))
            {
                return null;
            }

            if (!string.Equals(combinedNonce, _combinedNonce, StringComparison.Ordinal))
            {
                return null;
            }

            if (!string.Equals(channelBinding, Convert.ToBase64String(Encoding.ASCII.GetBytes(_gs2Header)), StringComparison.Ordinal))
            {
                return null;
            }

            byte[] clientProof;
            try
            {
                clientProof = Convert.FromBase64String(proofB64);
            }
            catch (FormatException)
            {
                return null;
            }

            var clientFinalWithoutProof = clientFinal.Replace($",p={proofB64}", string.Empty, StringComparison.Ordinal);
            var authMessage = $"{_clientFirstBare},{_serverFirst},{clientFinalWithoutProof}";
            var storedKey = _credential.StoredKey.ToArray();
            var clientSignature = HmacSha256(storedKey, Encoding.UTF8.GetBytes(authMessage));
            var clientKey = Xor(clientProof, clientSignature);
            var computedStoredKey = SHA256.HashData(clientKey);
            if (computedStoredKey.Length != storedKey.Length
                || !CryptographicOperations.FixedTimeEquals(computedStoredKey, storedKey))
            {
                return null;
            }

            var serverSignature = HmacSha256(_credential.ServerKey.ToArray(), Encoding.UTF8.GetBytes(authMessage));
            return $"v={Convert.ToBase64String(serverSignature)}";
        }

        /// <summary>Extracts the <c>n=</c> username from a client-first message.</summary>
        public static bool TryGetUsername(string clientFirst, [NotNullWhen(true)] out string? username)
        {
            username = null;
            if (!TrySplitClientFirst(clientFirst, out _, out var bare))
            {
                return false;
            }

            return TryParseAttribute(bare, 'n', out username) && !string.IsNullOrEmpty(username);
        }

        private static byte[] HmacSha256(byte[] key, byte[] data)
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            hmac.AppendData(data);
            return hmac.GetHashAndReset();
        }

        private static byte[] Xor(byte[] a, byte[] b)
        {
            var len = Math.Min(a.Length, b.Length);
            var result = new byte[len];
            for (var i = 0; i < len; i++)
            {
                result[i] = (byte)(a[i] ^ b[i]);
            }

            return result;
        }

        private static bool TrySplitClientFirst(
            string clientFirst,
            [NotNullWhen(true)] out string? gs2Header,
            [NotNullWhen(true)] out string? clientFirstBare)
        {
            var idx = clientFirst.IndexOf(",,", StringComparison.Ordinal);
            if (idx < 0)
            {
                gs2Header = null;
                clientFirstBare = null;
                return false;
            }

            gs2Header = clientFirst[..(idx + 2)];
            clientFirstBare = clientFirst[(idx + 2)..];
            return !string.IsNullOrEmpty(clientFirstBare);
        }

        private static bool TryParseAttribute(string message, char key, [NotNullWhen(true)] out string? value)
        {
            foreach (var part in message.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Length >= 2 && part[0] == key && part[1] == '=')
                {
                    value = part[2..];
                    return true;
                }
            }

            value = null;
            return false;
        }
    }
}
