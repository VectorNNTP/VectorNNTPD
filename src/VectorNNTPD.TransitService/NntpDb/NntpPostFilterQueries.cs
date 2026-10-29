namespace VectorNNTPD.TransitService.NntpDb
{
    /// <summary>Authoritative NntpDB queries for the cluster PostFilter policy.</summary>
    /// <remarks>
    /// NNTPD only <c>SELECT</c>s one published revision. Schema DDL is
    /// <c>docs/schema/postfilter.sql</c>. There is no application migration runner.
    /// </remarks>
    public static class NntpPostFilterQueries
    {
        /// <summary>Published revision pointer. <c>policy_id</c> is always <c>1</c>.</summary>
        public const string SelectCurrentRevision =
            "SELECT revision FROM nntppostfiltercurrent WHERE policy_id = 1";

        /// <summary>Scalar policy for one revision.</summary>
        public const string SelectPolicy =
            """
            SELECT
                revision,
                updated_utc,
                gate,
                long_window_ms,
                short_window_ms,
                max_messages_long,
                max_bytes_long,
                max_identical_long,
                max_messages_short,
                max_bytes_short,
                max_identical_short,
                sa_enabled,
                sa_on_failure,
                sa_max_article_size,
                sa_port,
                sa_protocol_version,
                sa_max_connections,
                sa_host_selection,
                sa_connect_timeout_ms,
                sa_operation_timeout_ms
            FROM nntppostfilterpolicy
            WHERE revision = @revision
            """;

        /// <summary>Deny/allow MD5 account identifiers for <c>@revision</c>.</summary>
        public const string SelectAccounts =
            "SELECT list_kind, account_name FROM nntppostfilteraccounts "
            + "WHERE revision = @revision ORDER BY list_kind, account_name";

        /// <summary>
        /// Inserts one PostFilter rejection evidence row. Payload is the unstuffed
        /// article when available. Never used on the accept path.
        /// </summary>
        public const string InsertRejection =
            """
            INSERT INTO nntppostfilterrejections (
              rejected_utc,
              revision,
              account_name,
              source_ip,
              art_type,
              message_id,
              article_size,
              stage,
              reason,
              sa_status,
              sa_score,
              sa_threshold,
              article_payload
            ) VALUES (
              @rejected_utc,
              @revision,
              @account_name,
              @source_ip,
              @art_type,
              @message_id,
              @article_size,
              @stage,
              @reason,
              @sa_status,
              @sa_score,
              @sa_threshold,
              @article_payload
            )
            """;

        /// <summary>Deny/allow client CIDRs for <c>@revision</c>.</summary>
        public const string SelectCidrs =
            "SELECT list_kind, cidr FROM nntppostfiltercidrs "
            + "WHERE revision = @revision ORDER BY list_kind, cidr";

        /// <summary>ArtType lists for <c>@revision</c>.</summary>
        public const string SelectArtTypes =
            "SELECT list_kind, art_type FROM nntppostfilterarttypes "
            + "WHERE revision = @revision ORDER BY list_kind, art_type";

        /// <summary>SPAMD hosts for <c>@revision</c> in compiled order.</summary>
        public const string SelectHosts =
            "SELECT host FROM nntppostfiltersahosts WHERE revision = @revision ORDER BY host_order";

        /// <summary>Account list kind stored for deny.</summary>
        public const string ListKindDeny = "deny";

        /// <summary>Account/CIDR list kind stored for allowlist.</summary>
        public const string ListKindAllow = "allow";

        /// <summary>ArtType list kind stored for Stage 3 reject.</summary>
        public const string ListKindReject = "reject";

        /// <summary>ArtType list kind stored for SpamAssassin exclusion.</summary>
        public const string ListKindSaExclude = "sa_exclude";
    }
}
