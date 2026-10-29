-- NntpDB PostFilter control-plane schema.
-- Apply this script to an empty NntpDB before VectorNNTP.NNTPD starts.
-- PostFilter policy is authoritative in NntpDB, not appsettings.
-- Policy rows are versioned and append-only.
-- nntppostfiltercurrent identifies the published revision.
-- Collection rows are keyed by revision so a load cannot mix revisions.
-- Seed revision 1 is Gate=Disabled (no filtering until an operator publishes).
-- Change policy by inserting a complete new revision, then publishing it.
-- Do not modify a published revision in place.
-- NNTPD only SELECTs. There is no application migration runner.
-- Conventions match existing NntpDB catalogues: InnoDB, snake_case,
-- CHAR(1) Y/N, DATETIME(3), utf8mb4_unicode_ci.

CREATE TABLE nntppostfilterpolicy (
  revision BIGINT UNSIGNED NOT NULL,
  updated_utc DATETIME(3) NOT NULL,
  gate VARCHAR(16) NOT NULL,
  long_window_ms BIGINT UNSIGNED NOT NULL,
  short_window_ms BIGINT UNSIGNED NOT NULL,
  max_messages_long BIGINT UNSIGNED NOT NULL,
  max_bytes_long BIGINT UNSIGNED NOT NULL,
  max_identical_long BIGINT UNSIGNED NOT NULL,
  max_messages_short BIGINT UNSIGNED NOT NULL,
  max_bytes_short BIGINT UNSIGNED NOT NULL,
  max_identical_short BIGINT UNSIGNED NOT NULL,
  sa_enabled CHAR(1) NOT NULL,
  sa_on_failure VARCHAR(16) NULL,
  sa_max_article_size INT NOT NULL,
  sa_port INT UNSIGNED NOT NULL,
  sa_protocol_version VARCHAR(16) NOT NULL,
  sa_max_connections INT UNSIGNED NOT NULL,
  sa_host_selection VARCHAR(16) NOT NULL,
  sa_connect_timeout_ms INT UNSIGNED NOT NULL,
  sa_operation_timeout_ms INT UNSIGNED NOT NULL,
  PRIMARY KEY (revision),
  CONSTRAINT chk_nntppostfilterpolicy_revision CHECK (revision >= 1),
  CONSTRAINT chk_nntppostfilterpolicy_gate CHECK (gate IN ('Disabled', 'Active', 'Closed')),
  CONSTRAINT chk_nntppostfilterpolicy_windows CHECK (long_window_ms >= 1 AND short_window_ms >= 1),
  CONSTRAINT chk_nntppostfilterpolicy_sa_enabled CHECK (sa_enabled IN ('Y', 'N')),
  CONSTRAINT chk_nntppostfilterpolicy_sa_on_failure CHECK (
    sa_on_failure IS NULL OR sa_on_failure IN ('Reject', 'Accept')
  ),
  CONSTRAINT chk_nntppostfilterpolicy_sa_required CHECK (
    sa_enabled <> 'Y' OR sa_on_failure IN ('Reject', 'Accept')
  ),
  CONSTRAINT chk_nntppostfilterpolicy_sa_size CHECK (sa_max_article_size >= 0),
  CONSTRAINT chk_nntppostfilterpolicy_sa_port CHECK (sa_port BETWEEN 1 AND 65535),
  CONSTRAINT chk_nntppostfilterpolicy_sa_pool CHECK (sa_max_connections BETWEEN 1 AND 32),
  CONSTRAINT chk_nntppostfilterpolicy_sa_hosts CHECK (sa_host_selection IN ('RoundRobin', 'Failover')),
  CONSTRAINT chk_nntppostfilterpolicy_sa_timeouts CHECK (
    sa_connect_timeout_ms >= 1
    AND sa_operation_timeout_ms >= 1
    AND sa_connect_timeout_ms <= sa_operation_timeout_ms
  )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='Append-only PostFilter scalar revisions';

CREATE TABLE nntppostfiltercurrent (
  policy_id TINYINT UNSIGNED NOT NULL,
  revision BIGINT UNSIGNED NOT NULL,
  PRIMARY KEY (policy_id),
  CONSTRAINT chk_nntppostfiltercurrent_policy_id CHECK (policy_id = 1),
  CONSTRAINT fk_nntppostfiltercurrent_revision
    FOREIGN KEY (revision) REFERENCES nntppostfilterpolicy (revision)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='Singleton published PostFilter revision (policy_id=1)';

CREATE TABLE nntppostfilteraccounts (
  revision BIGINT UNSIGNED NOT NULL,
  list_kind VARCHAR(8) NOT NULL,
  account_name CHAR(32) NOT NULL,
  PRIMARY KEY (revision, list_kind, account_name),
  CONSTRAINT chk_nntppostfilteraccounts_kind CHECK (list_kind IN ('deny', 'allow')),
  CONSTRAINT chk_nntppostfilteraccounts_name CHECK (account_name REGEXP '^[0-9a-f]{32}$'),
  CONSTRAINT fk_nntppostfilteraccounts_revision
    FOREIGN KEY (revision) REFERENCES nntppostfilterpolicy (revision)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='PostFilter deny/allow MD5(hex) AUTH identities; real usernames are not stored';

CREATE TABLE nntppostfiltercidrs (
  revision BIGINT UNSIGNED NOT NULL,
  list_kind VARCHAR(8) NOT NULL,
  cidr VARCHAR(64) NOT NULL,
  PRIMARY KEY (revision, list_kind, cidr),
  CONSTRAINT chk_nntppostfiltercidrs_kind CHECK (list_kind IN ('deny', 'allow')),
  CONSTRAINT chk_nntppostfiltercidrs_value CHECK (CHAR_LENGTH(TRIM(cidr)) > 0),
  CONSTRAINT fk_nntppostfiltercidrs_revision
    FOREIGN KEY (revision) REFERENCES nntppostfilterpolicy (revision)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='PostFilter deny/allow client CIDRs for one revision';

CREATE TABLE nntppostfilterarttypes (
  revision BIGINT UNSIGNED NOT NULL,
  list_kind VARCHAR(16) NOT NULL,
  art_type ENUM(
    'Default',
    'Control',
    'Cancel',
    'Mime',
    'Binary',
    'UuEncode',
    'Base64',
    'YEncoded',
    'BommaNews',
    'UniData',
    'Multipart',
    'Html',
    'PostScript',
    'BinHex',
    'Partial',
    'PgpMessage'
  ) NOT NULL,
  PRIMARY KEY (revision, list_kind, art_type),
  CONSTRAINT chk_nntppostfilterarttypes_kind CHECK (list_kind IN ('reject', 'sa_exclude')),
  CONSTRAINT fk_nntppostfilterarttypes_revision
    FOREIGN KEY (revision) REFERENCES nntppostfilterpolicy (revision)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='PostFilter reject and SA-exclude ArtTypes; one classifier name per row';

CREATE TABLE nntppostfiltersahosts (
  revision BIGINT UNSIGNED NOT NULL,
  host_order INT UNSIGNED NOT NULL,
  host VARCHAR(255) NOT NULL,
  PRIMARY KEY (revision, host_order),
  UNIQUE KEY uq_nntppostfiltersahosts_host (revision, host),
  CONSTRAINT chk_nntppostfiltersahosts_host CHECK (CHAR_LENGTH(TRIM(host)) > 0),
  CONSTRAINT fk_nntppostfiltersahosts_revision
    FOREIGN KEY (revision) REFERENCES nntppostfilterpolicy (revision)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='PostFilter SPAMD hosts in compiled order for one revision';

CREATE TABLE nntppostfilterrejections (
  rejection_id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  rejected_utc DATETIME(3) NOT NULL,
  revision BIGINT UNSIGNED NOT NULL,
  account_name CHAR(32) NULL,
  source_ip VARBINARY(16) NOT NULL,
  art_type INT UNSIGNED NOT NULL,
  message_id VARCHAR(250) NULL,
  article_size INT UNSIGNED NOT NULL,
  stage VARCHAR(32) NOT NULL,
  reason VARCHAR(64) NOT NULL,
  sa_status VARCHAR(16) NULL,
  sa_score DECIMAL(8,3) NULL,
  sa_threshold DECIMAL(8,3) NULL,
  article_payload LONGBLOB NULL,
  PRIMARY KEY (rejection_id),
  KEY ix_nntppostfilterrejections_rejected_utc (rejected_utc),
  KEY ix_nntppostfilterrejections_account (account_name),
  KEY ix_nntppostfilterrejections_revision (revision),
  CONSTRAINT chk_nntppostfilterrejections_source_ip CHECK (OCTET_LENGTH(source_ip) IN (4, 16)),
  CONSTRAINT chk_nntppostfilterrejections_account CHECK (
    account_name IS NULL OR account_name REGEXP '^[0-9a-f]{32}$'
  ),
  CONSTRAINT chk_nntppostfilterrejections_stage CHECK (CHAR_LENGTH(TRIM(stage)) > 0),
  CONSTRAINT chk_nntppostfilterrejections_reason CHECK (CHAR_LENGTH(TRIM(reason)) > 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='Append-only PostFilter rejection evidence; payload is unstuffed article bytes or NULL';

DELIMITER $$
CREATE TRIGGER trg_nntppostfiltercurrent_revision_forward
BEFORE UPDATE ON nntppostfiltercurrent
FOR EACH ROW
BEGIN
  IF NEW.revision <= OLD.revision THEN
    SIGNAL SQLSTATE '45000'
      SET MESSAGE_TEXT = 'nntppostfiltercurrent.revision must increase';
  END IF;
END$$
DELIMITER ;

INSERT INTO nntppostfilterpolicy (
  revision, updated_utc, gate,
  long_window_ms, short_window_ms,
  max_messages_long, max_bytes_long, max_identical_long,
  max_messages_short, max_bytes_short, max_identical_short,
  sa_enabled, sa_on_failure, sa_max_article_size, sa_port,
  sa_protocol_version, sa_max_connections, sa_host_selection,
  sa_connect_timeout_ms, sa_operation_timeout_ms
) VALUES (
  1, UTC_TIMESTAMP(3), 'Disabled',
  86400000, 600000,
  0, 0, 0, 0, 0, 0,
  'N', NULL, 131072, 783,
  '1.5', 4, 'RoundRobin',
  5000, 30000
);

INSERT INTO nntppostfilterarttypes (revision, list_kind, art_type)
VALUES (1, 'sa_exclude', 'YEncoded');

INSERT INTO nntppostfiltercurrent (policy_id, revision) VALUES (1, 1);

-- nntppostfilterrejections is append-oriented evidence. This script does not
-- delete rows. Retention is an explicit operator decision; NNTPD has no
-- PostFilter evidence sweeper.
--
-- Authenticated account ArtType capability is not created here.
-- Apply docs/schema/nntpusers-account-art-type.sql to the existing account
-- database before deploying the AUTHINFO SELECT that reads account_art_type.
