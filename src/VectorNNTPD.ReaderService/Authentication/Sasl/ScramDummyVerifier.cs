using System.Security.Cryptography;

namespace VectorNNTPD.ReaderService.Authentication.Sasl
{
    /// <summary>
    /// Server-local dummy SCRAM-SHA-256 verifier used so the 383 server-first
    /// exchange does not reveal whether a username exists or has SCRAM material.
    /// </summary>
    /// <remarks>
    /// Material is generated once and is not derived from any client-controlled
    /// input. A client that somehow obtained these keys still cannot authenticate:
    /// the SASL exchange records that the dummy verifier was used and forces 481.
    /// Never log this material.
    /// </remarks>
    internal sealed class ScramDummyVerifier
    {
        /// <summary>RFC 7677 minimum iteration count. Same shape as a real server-first.</summary>
        public const int IterationCount = 4096;

        /// <summary>Salt length used for dummy server-first <c>s=</c>.</summary>
        public const int SaltLength = 16;

        /// <summary>SHA-256 key length for dummy StoredKey and ServerKey.</summary>
        public const int KeyLength = 32;

        /// <summary>Creates a dummy verifier with cryptographically random keys.</summary>
        public static ScramDummyVerifier Create() =>
            new(new ScramStoredCredential(
                RandomNumberGenerator.GetBytes(SaltLength),
                IterationCount,
                RandomNumberGenerator.GetBytes(KeyLength),
                RandomNumberGenerator.GetBytes(KeyLength)));

        /// <summary>Creates a dummy verifier from injected material (tests).</summary>
        public static ScramDummyVerifier Create(ScramStoredCredential credential)
        {
            if (credential.Salt.Length != SaltLength
                || credential.IterationCount != IterationCount
                || credential.StoredKey.Length != KeyLength
                || credential.ServerKey.Length != KeyLength)
            {
                throw new ArgumentException("Dummy SCRAM material must match the production dummy shape.", nameof(credential));
            }

            return new ScramDummyVerifier(credential);
        }

        private ScramDummyVerifier(ScramStoredCredential credential)
        {
            Credential = credential;
        }

        /// <summary>Gets the immutable dummy stored credential. Do not log.</summary>
        public ScramStoredCredential Credential { get; }
    }
}
