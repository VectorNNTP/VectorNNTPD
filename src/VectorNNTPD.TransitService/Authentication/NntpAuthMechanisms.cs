namespace VectorNNTPD.TransitService.Authentication
{
    /// <summary>Canonical authentication mechanism labels used in logs and SASL dispatch.</summary>
    internal static class NntpAuthMechanisms
    {
        public const string AuthInfoUserPass = "AUTHINFO USER/PASS";
        public const string SaslPlain = "SASL PLAIN";
        public const string SaslLogin = "SASL LOGIN";
        public const string SaslCramMd5 = "SASL CRAM-MD5";
        public const string SaslScramSha256 = "SASL SCRAM-SHA-256";

        public const string WirePlain = "PLAIN";
        public const string WireLogin = "LOGIN";
        public const string WireCramMd5 = "CRAM-MD5";
        public const string WireScramSha256 = "SCRAM-SHA-256";
    }
}
