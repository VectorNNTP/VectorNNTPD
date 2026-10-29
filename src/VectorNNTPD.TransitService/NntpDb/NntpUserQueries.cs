namespace VectorNNTPD.TransitService.NntpDb
{
    /// <summary>Authoritative NntpDB query for reader account authentication.</summary>
    public static class NntpUserQueries
    {
        /// <summary>
        /// Looks up one <c>nntpusers</c> row. <c>account_name</c> is stored as MD5 hex of the
        /// plaintext wire username. <c>account_pass</c> is decrypted in SQL with
        /// <c>AES_DECRYPT(..., UNHEX(SHA2(account_name, 256)))</c> using the stored hash column.
        /// Does not select <c>account_type</c>; every account has both remaining-byte and rate
        /// policies.
        /// </summary>
        public const string SelectUserByName =
            "SELECT "
            + "CAST(AES_DECRYPT(account_pass, UNHEX(SHA2(account_name, 256))) AS CHAR) AS account_pass, "
            + "scram_salt, scram_iterations, scram_stored_key, scram_server_key, "
            + "allow_auth_plain, allow_auth_scram256, "
            + "account_rate_limit, account_byte_limit, account_session_limit, account_srcip_limit, "
            + "is_enabled, customer_id, account_art_type "
            + "FROM nntpusers "
            + "WHERE account_name = MD5(@account_name)";

        /// <summary>
        /// Additive <c>nntpusers</c> extension from
        /// <c>docs/schema/nntpusers-account-art-type.sql</c>. Does not drop the table.
        /// Default <c>65535</c> is <c>ArticleTypeCapabilities.All</c>.
        /// </summary>
        public const string AddAccountArtTypeColumn =
            "ALTER TABLE nntpusers "
            + "ADD COLUMN account_art_type INT UNSIGNED NOT NULL DEFAULT 65535 "
            + "COMMENT 'ArticleType flags; 65535=ArticleTypeCapabilities.All (unrestricted)'";

        /// <summary>Locks one <c>nntpusers</c> row so a consume can read remaining atomically.</summary>
        public const string SelectByteQuotaForUpdate =
            "SELECT account_byte_limit "
            + "FROM nntpusers "
            + "WHERE account_name = MD5(@account_name) "
            + "FOR UPDATE";

        /// <summary>
        /// Subtracts consumed bytes from remaining quota, clamping at zero.
        /// Uses <c>CASE</c> rather than unchecked subtraction so an unsigned column
        /// cannot wrap before the floor is applied.
        /// </summary>
        public const string ConsumeAccountBytes =
            "UPDATE nntpusers "
            + "SET account_byte_limit = CASE "
            + "WHEN account_byte_limit IS NULL THEN 0 "
            + "WHEN account_byte_limit > @bytes THEN account_byte_limit - @bytes "
            + "ELSE 0 END "
            + "WHERE account_name = MD5(@account_name)";

        /// <summary>Reads remaining quota without locking.</summary>
        public const string SelectAccountByteRemaining =
            "SELECT account_byte_limit "
            + "FROM nntpusers "
            + "WHERE account_name = MD5(@account_name)";
    }
}
