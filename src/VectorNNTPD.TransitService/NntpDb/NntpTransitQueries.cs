namespace VectorNNTPD.TransitService.NntpDb
{
    /// <summary>SQL for the published Transit catalogue. NNTPD only reads these tables.</summary>
    internal static class NntpTransitQueries
    {
        /// <summary>The singleton current-publication row and its global flags.</summary>
        public const string SelectCurrent = """
            SELECT p.publication_id, p.receive_revision, p.send_revision, p.global_revision,
                   g.want_trash, g.log_trash
            FROM nntptransitcurrent c
            INNER JOIN nntptransitpublication p ON p.publication_id = c.publication_id
            INNER JOIN nntptransitglobalrevision g ON g.revision = p.global_revision
            WHERE c.policy_id = 1
            """;

        /// <summary>Receive and send rows for the publication's two revisions, joined on identifier.</summary>
        public const string SelectPeers = """
            SELECT r.identifier, peer.peer_name,
                   r.max_inbound, r.username, r.password, r.defer_on_duplicate,
                   r.max_article_bytes, r.article_types, r.patterns,
                   s.max_outbound, s.ssl_mode, s.username, s.password,
                   s.max_article_bytes, s.article_types, s.patterns, s.path_token
            FROM nntptransitreceive r
            INNER JOIN nntptransitsend s
                ON s.identifier = r.identifier AND s.revision = @send_revision
            INNER JOIN nntptransitpeer peer ON peer.identifier = r.identifier
            WHERE r.revision = @receive_revision
            ORDER BY r.identifier
            """;

        /// <summary>Counts receive rows so a dropped join cannot hide a missing send peer.</summary>
        public const string CountReceive = """
            SELECT COUNT(*) FROM nntptransitreceive WHERE revision = @receive_revision
            """;

        /// <summary>Counts send rows so a dropped join cannot hide a missing receive peer.</summary>
        public const string CountSend = """
            SELECT COUNT(*) FROM nntptransitsend WHERE revision = @send_revision
            """;

        /// <summary>AllowFrom entries for the receive revision, in configuration order.</summary>
        public const string SelectAllowFrom = """
            SELECT identifier, entry
            FROM nntptransitallowfrom
            WHERE revision = @receive_revision
            ORDER BY identifier, ordinal
            """;

        /// <summary>Send endpoints for the send revision, in configuration order.</summary>
        public const string SelectEndpoints = """
            SELECT identifier, host, port
            FROM nntptransitendpoint
            WHERE revision = @send_revision
            ORDER BY identifier, ordinal
            """;

        /// <summary>Send Path exclusions for the send revision, in configuration order.</summary>
        public const string SelectPathExclusions = """
            SELECT identifier, path_token
            FROM nntptransitsendpathexclude
            WHERE revision = @send_revision
            ORDER BY identifier, ordinal
            """;
    }
}
