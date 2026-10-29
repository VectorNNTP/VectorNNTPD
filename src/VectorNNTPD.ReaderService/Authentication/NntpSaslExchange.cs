using VectorNNTPD.ReaderService.Authentication.Sasl;
using VectorNNTPD.ReaderService.Session.Authentication;

namespace VectorNNTPD.ReaderService.Authentication
{
    /// <summary>In-progress AUTHINFO SASL exchange isolated to one NNTP session.</summary>
    internal sealed class NntpSaslExchange
    {
        public NntpSaslExchange(string mechanism)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(mechanism);
            Mechanism = mechanism;
        }

        public string Mechanism { get; }
        public string? LoginUsername { get; set; }
        /// <summary>Gets or sets the native RFC 2195 CRAM-MD5 challenge (not Base64).</summary>
        public string? CramChallenge { get; set; }
        public ScramMechanism? Scram { get; set; }
        public string? ScramUsername { get; set; }

        /// <summary>
        /// Gets or sets whether this exchange used the server-local dummy verifier.
        /// Dummy success must never authenticate.
        /// </summary>
        public bool ScramUsedDummy { get; set; }

        public bool WaitingPlainCredentials { get; set; }
    }

    /// <summary>SASL start/continuation outcome for the AUTHINFO handler.</summary>
    internal sealed class NntpSaslReply
    {
        public NntpSaslReply(
            ReadOnlyMemory<byte> wire,
            string statusLine,
            bool completed,
            NntpSaslExchange? exchange = null,
            NntpAuthenticationResult? authentication = null)
        {
            Wire = wire;
            StatusLine = statusLine;
            Completed = completed;
            Exchange = exchange;
            Authentication = authentication;
        }

        public ReadOnlyMemory<byte> Wire { get; }
        public string StatusLine { get; }
        public bool Completed { get; }
        public NntpSaslExchange? Exchange { get; }
        public NntpAuthenticationResult? Authentication { get; }
    }
}
