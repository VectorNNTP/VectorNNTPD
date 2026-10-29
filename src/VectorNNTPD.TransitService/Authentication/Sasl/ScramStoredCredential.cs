namespace VectorNNTPD.TransitService.Authentication.Sasl
{
    /// <summary>Stored SCRAM-SHA-256 material from <c>nntpusers</c>.</summary>
    internal readonly struct ScramStoredCredential
    {
        /// <summary>Initializes stored SCRAM fields.</summary>
        public ScramStoredCredential(
            ReadOnlyMemory<byte> salt,
            int iterationCount,
            ReadOnlyMemory<byte> storedKey,
            ReadOnlyMemory<byte> serverKey)
        {
            Salt = salt;
            IterationCount = iterationCount;
            StoredKey = storedKey;
            ServerKey = serverKey;
        }

        public ReadOnlyMemory<byte> Salt { get; }
        public int IterationCount { get; }
        public ReadOnlyMemory<byte> StoredKey { get; }
        public ReadOnlyMemory<byte> ServerKey { get; }

        public static ScramStoredCredential FromRecord(NntpUserRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            return new(record.ScramSalt, record.ScramIterations, record.ScramStoredKey, record.ScramServerKey);
        }

        /// <summary>
        /// Returns the real stored verifier only for an enabled account with usable
        /// SCRAM-SHA-256 material. Disabled, denied, missing, or malformed rows
        /// must use the dummy verifier so server-first does not enumerate accounts.
        /// </summary>
        public static bool TryGetAuthorized(NntpUserRecord? record, out ScramStoredCredential credential)
        {
            credential = default;
            if (record is null
                || !record.IsEnabled
                || !record.AllowAuthScram256
                || record.ScramSalt.IsEmpty
                || record.ScramIterations <= 0
                || record.ScramStoredKey.Length != 32
                || record.ScramServerKey.Length != 32)
            {
                return false;
            }

            credential = FromRecord(record);
            return true;
        }
    }
}
