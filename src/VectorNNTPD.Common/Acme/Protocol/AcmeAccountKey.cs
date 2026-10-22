using System.Security.Cryptography;
using System.Text;

namespace VectorNNTPD.Common.Acme.Protocol
{
    /// <summary>
    /// ACME account private key (JWK thumbprint, JWS signing, DNS-01 key authorization).
    /// VectorNNTP persists account keys as PKCS#8 DER; use <see cref="ImportPkcs8Der"/>.
    /// </summary>
    internal sealed class AcmeAccountKey : IDisposable
    {
        /// <summary>Account RSA key. Disposed by <see cref="Dispose"/>.</summary>
        private readonly RSA _rsa;

        /// <summary>Hash used for JWS signatures. Always SHA-256.</summary>
        private readonly HashAlgorithmName _hash = HashAlgorithmName.SHA256;

        /// <summary>
        /// Captures <paramref name="rsa"/> and computes the canonical JWK and thumbprint from the public parameters.
        /// </summary>
        /// <param name="rsa">RSA key this instance owns.</param>
        private AcmeAccountKey(RSA rsa)
        {
            _rsa = rsa;
            SignatureAlgorithm = "RS256";

            RSAParameters parameters = rsa.ExportParameters(includePrivateParameters: false);
            Jwk = $"{{\"e\":\"{Base64Url.Encode(parameters.Exponent!)}\",\"kty\":\"RSA\",\"n\":\"{Base64Url.Encode(parameters.Modulus!)}\"}}";
            Thumbprint = ComputeThumbprint(Jwk);
        }

        /// <summary>Gets the JWS <c>alg</c> value (<c>RS256</c>).</summary>
        internal string SignatureAlgorithm { get; }

        /// <summary>Gets the canonical JWK JSON object used for thumbprints and newAccount.</summary>
        internal string Jwk { get; }

        /// <summary>Gets the JWK thumbprint (base64url SHA-256 of <see cref="Jwk"/>).</summary>
        internal string Thumbprint { get; }

        /// <summary>Creates a new RSA account key (minimum 2048 bits).</summary>
        /// <param name="keySize">RSA modulus size in bits. Values below 2048 throw.</param>
        /// <returns>An owned account key. The caller disposes it.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="keySize"/> is below 2048.</exception>
        private static AcmeAccountKey CreateRsa(int keySize = 2048)
        {
            if (keySize < 2048)
            {
                throw new ArgumentOutOfRangeException(nameof(keySize), "RSA keys must be at least 2048 bits.");
            }

            return new AcmeAccountKey(RSA.Create(keySize));
        }

        /// <summary>
        /// Imports a PKCS#8 DER RSA private key. Consumes the entire buffer; preserves key material losslessly.
        /// </summary>
        /// <param name="pkcs8Der">PKCS#8 DER bytes. Empty input throws <see cref="CryptographicException"/>.</param>
        /// <returns>An owned account key. The caller disposes it.</returns>
        /// <exception cref="CryptographicException">Thrown when the buffer is empty, import fails, or not every byte is consumed.</exception>
        internal static AcmeAccountKey ImportPkcs8Der(ReadOnlySpan<byte> pkcs8Der)
        {
            if (pkcs8Der.IsEmpty)
            {
                throw new CryptographicException("PKCS#8 DER account key is empty.");
            }

            RSA rsa = RSA.Create();
            RSA? pendingDispose = rsa;
            try
            {
                rsa.ImportPkcs8PrivateKey(pkcs8Der, out int bytesRead);
                if (bytesRead != pkcs8Der.Length)
                {
                    throw new CryptographicException(
                        $"PKCS#8 import consumed {bytesRead} of {pkcs8Der.Length} bytes.");
                }

                pendingDispose = null;
                return new AcmeAccountKey(rsa);
            }
            finally
            {
                pendingDispose?.Dispose();
            }
        }

        /// <summary>
        /// Proves DER → <see cref="AcmeAccountKey"/> → DER preserves RSA private key material (modulus and D).
        /// </summary>
        /// <param name="pkcs8Der">PKCS#8 DER bytes to import and export.</param>
        /// <exception cref="CryptographicException">Thrown when modulus or D differs after the round trip, or import fails.</exception>
        internal static void AssertPkcs8DerRoundTripPreservesRsaMaterial(ReadOnlySpan<byte> pkcs8Der)
        {
            using var original = RSA.Create();
            original.ImportPkcs8PrivateKey(pkcs8Der, out _);
            RSAParameters before = original.ExportParameters(includePrivateParameters: true);

            using AcmeAccountKey key = ImportPkcs8Der(pkcs8Der);
            using var verify = RSA.Create();
            verify.ImportPkcs8PrivateKey(key.ExportPkcs8Der(), out _);
            RSAParameters after = verify.ExportParameters(includePrivateParameters: true);

            if (!before.Modulus!.AsSpan().SequenceEqual(after.Modulus!)
                || !before.D!.AsSpan().SequenceEqual(after.D!))
            {
                throw new CryptographicException("PKCS#8 DER import did not preserve RSA private key material.");
            }
        }

        /// <summary>Exports the private key as PKCS#8 DER.</summary>
        /// <returns>PKCS#8 DER bytes of <see cref="_rsa"/>.</returns>
        private byte[] ExportPkcs8Der() => _rsa.ExportPkcs8PrivateKey();

        /// <summary>Signs <paramref name="data"/> for JWS (RSA PKCS#1 v1.5 / SHA-256).</summary>
        /// <param name="data">ASCII signing input <c>protected.payload</c>.</param>
        /// <returns>The RSA signature.</returns>
        internal byte[] Sign(ReadOnlySpan<byte> data) =>
            _rsa.SignData(data.ToArray(), _hash, RSASignaturePadding.Pkcs1);

        /// <summary>Builds the ACME key authorization string for <paramref name="token"/>.</summary>
        /// <param name="token">Challenge token from the CA.</param>
        /// <returns><c>{token}.{thumbprint}</c>.</returns>
        internal string GetKeyAuthorization(string token) => token + "." + Thumbprint;

        /// <summary>Computes the DNS-01 TXT value (base64url SHA-256 of the key authorization).</summary>
        /// <param name="token">Challenge token from the CA.</param>
        /// <returns>Unpadded base64url SHA-256 of <see cref="GetKeyAuthorization"/>.</returns>
        internal string GetDnsRecordValue(string token)
        {
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(GetKeyAuthorization(token)));
            return Base64Url.Encode(digest);
        }

        /// <summary>Disposes the RSA account key.</summary>
        public void Dispose() => _rsa.Dispose();

        /// <summary>Base64url-encodes the SHA-256 digest of the canonical JWK JSON.</summary>
        /// <param name="canonicalJwk">JWK JSON already in thumbprint order.</param>
        /// <returns>The JWK thumbprint.</returns>
        private static string ComputeThumbprint(string canonicalJwk) =>
            Base64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJwk)));
    }
}
