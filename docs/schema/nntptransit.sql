-- NntpDB Transit control-plane schema.
-- This file documents the catalogue installed in the nntp database.
-- Do not re-apply it against that catalogue. NNTPD only SELECTs.
-- One stable peer identity has a Receive plane and a Send plane.
-- Receive and Send revisions are independent. nntptransitpublication names one of each,
-- plus one global revision. nntptransitcurrent points at exactly one publication.
-- A publication is valid only when those two peer revisions contain the same identifiers.
-- Published revision rows are immutable. Change a plane by inserting a new revision, then a new publication.
-- There is no application migration runner.
-- Conventions match existing NntpDB catalogues: InnoDB, snake_case, CHAR(1) Y/N, DATETIME(3), utf8mb4_unicode_ci.
-- Peer identifiers are ascii/ascii_bin so they are not case-folded.
--
-- Connection limits are the enable switches. There is no Enabled flag.
-- max_inbound = 0 closes receive. max_outbound = 0 closes send. Zero is not unlimited.
-- article_types is the ArticleType / ArticleTypeCapabilities mask (0..65535).
-- 0 permits no classified article. 1 is ArticleType.Default (text only). 65535 is unrestricted.
-- patterns is one NewsfeedsPattern expression per plane. '*' is all groups.
-- Empty and whitespace-only patterns are rejected here. Wildmat grammar is NewsfeedsPattern.TryParse.
--
-- Not stored here (process-local):
--   Nntpd:Transit:StreamOutstandingArticleDepth
--   Nntpd:TransitQueueMemoryLimit
--   Nntpd:ArticleIngestion:MaxArticleBytes
--
-- nntptransitsend.max_article_bytes: NULL is unlimited. A non-NULL value is an explicit
-- maximum article size in bytes (1–2147483647). 0 is not unlimited.
-- nntptransitreceive.max_article_bytes stays NOT NULL in that same positive range.
-- Send revision 1 was seeded from nntpsharedconfig.maxartsize (5242880), not from each peer's receive size.

CREATE TABLE nntptransitpeer (
  identifier VARCHAR(256) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  peer_name VARCHAR(256) NOT NULL,
  PRIMARY KEY (identifier),
  CONSTRAINT chk_nntptransitpeer_identifier CHECK (
    CHAR_LENGTH(identifier) BETWEEN 1 AND 256
    AND identifier REGEXP '^[!-~]+$'
  ),
  CONSTRAINT chk_nntptransitpeer_name CHECK (
    CHAR_LENGTH(peer_name) BETWEEN 1 AND 256
    AND CHAR_LENGTH(TRIM(peer_name)) >= 1
    AND peer_name NOT REGEXP '[[:cntrl:]]'
  )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='Stable Transit peer identity. peer_name is display only';

CREATE TABLE nntptransitreceiverevision (
  revision BIGINT UNSIGNED NOT NULL,
  published_utc DATETIME(3) NOT NULL,
  PRIMARY KEY (revision),
  CONSTRAINT chk_nntptransitreceiverevision_revision CHECK (revision >= 1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='Immutable header for one complete Receive catalogue revision';

CREATE TABLE nntptransitreceive (
  revision BIGINT UNSIGNED NOT NULL,
  identifier VARCHAR(256) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  max_inbound INT UNSIGNED NOT NULL,
  username VARCHAR(255) NOT NULL,
  password VARCHAR(255) NOT NULL,
  defer_on_duplicate CHAR(1) NOT NULL,
  max_article_bytes INT UNSIGNED NOT NULL,
  article_types INT UNSIGNED NOT NULL,
  patterns VARCHAR(4096) NOT NULL,
  PRIMARY KEY (revision, identifier),
  CONSTRAINT fk_nntptransitreceive_revision
    FOREIGN KEY (revision) REFERENCES nntptransitreceiverevision (revision),
  CONSTRAINT fk_nntptransitreceive_peer
    FOREIGN KEY (identifier) REFERENCES nntptransitpeer (identifier),
  CONSTRAINT chk_nntptransitreceive_inbound CHECK (max_inbound <= 4096),
  CONSTRAINT chk_nntptransitreceive_credentials CHECK (
    (CHAR_LENGTH(username) = 0 AND CHAR_LENGTH(password) = 0)
    OR (CHAR_LENGTH(username) > 0 AND CHAR_LENGTH(password) > 0)
  ),
  CONSTRAINT chk_nntptransitreceive_defer CHECK (defer_on_duplicate IN ('Y', 'N')),
  CONSTRAINT chk_nntptransitreceive_size CHECK (max_article_bytes BETWEEN 1 AND 2147483647),
  CONSTRAINT chk_nntptransitreceive_types CHECK (article_types <= 65535),
  CONSTRAINT chk_nntptransitreceive_patterns CHECK (
    CHAR_LENGTH(patterns) BETWEEN 1 AND 4096
    AND CHAR_LENGTH(TRIM(patterns)) >= 1
  )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='Receive plane: what this peer may send into us. max_inbound 0 closes receive';

CREATE TABLE nntptransitallowfrom (
  revision BIGINT UNSIGNED NOT NULL,
  identifier VARCHAR(256) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  ordinal INT UNSIGNED NOT NULL,
  entry VARCHAR(255) NOT NULL,
  PRIMARY KEY (revision, identifier, ordinal),
  CONSTRAINT fk_nntptransitallowfrom_receive
    FOREIGN KEY (revision, identifier) REFERENCES nntptransitreceive (revision, identifier),
  CONSTRAINT chk_nntptransitallowfrom_entry CHECK (CHAR_LENGTH(TRIM(entry)) BETWEEN 1 AND 255)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='Receive AllowFrom sources in configuration order. Zero rows means the peer never matches';

CREATE TABLE nntptransitsendrevision (
  revision BIGINT UNSIGNED NOT NULL,
  published_utc DATETIME(3) NOT NULL,
  PRIMARY KEY (revision),
  CONSTRAINT chk_nntptransitsendrevision_revision CHECK (revision >= 1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='Immutable header for one complete Send catalogue revision';

CREATE TABLE nntptransitsend (
  revision BIGINT UNSIGNED NOT NULL,
  identifier VARCHAR(256) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  max_outbound INT UNSIGNED NOT NULL,
  ssl_mode VARCHAR(16) NOT NULL,
  username VARCHAR(255) NOT NULL,
  password VARCHAR(255) NOT NULL,
  max_article_bytes INT UNSIGNED NULL COMMENT 'NULL means unlimited. A non-NULL value is the maximum article size in bytes',
  article_types INT UNSIGNED NOT NULL,
  patterns VARCHAR(4096) NOT NULL,
  path_token VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
  PRIMARY KEY (revision, identifier),
  CONSTRAINT fk_nntptransitsend_revision
    FOREIGN KEY (revision) REFERENCES nntptransitsendrevision (revision),
  CONSTRAINT fk_nntptransitsend_peer
    FOREIGN KEY (identifier) REFERENCES nntptransitpeer (identifier),
  CONSTRAINT chk_nntptransitsend_outbound CHECK (max_outbound <= 4096),
  CONSTRAINT chk_nntptransitsend_ssl CHECK (ssl_mode IN ('None', 'Tls', 'StartTls')),
  CONSTRAINT chk_nntptransitsend_credentials CHECK (
    (CHAR_LENGTH(username) = 0 AND CHAR_LENGTH(password) = 0)
    OR (CHAR_LENGTH(username) > 0 AND CHAR_LENGTH(password) > 0)
  ),
  CONSTRAINT chk_nntptransitsend_size CHECK (
    max_article_bytes IS NULL OR max_article_bytes BETWEEN 1 AND 2147483647
  ),
  CONSTRAINT chk_nntptransitsend_types CHECK (article_types <= 65535),
  CONSTRAINT chk_nntptransitsend_patterns CHECK (
    CHAR_LENGTH(patterns) BETWEEN 1 AND 4096
    AND CHAR_LENGTH(TRIM(patterns)) >= 1
  ),
  CONSTRAINT chk_nntptransitsend_path CHECK (
    CHAR_LENGTH(path_token) <= 255
    AND path_token NOT REGEXP '[[:cntrl:]]'
  )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='Send plane: what we are willing to send to this peer. max_outbound 0 closes send';

CREATE TABLE nntptransitendpoint (
  revision BIGINT UNSIGNED NOT NULL,
  identifier VARCHAR(256) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  ordinal INT UNSIGNED NOT NULL,
  host VARCHAR(255) NOT NULL,
  port INT UNSIGNED NOT NULL,
  PRIMARY KEY (revision, identifier, ordinal),
  UNIQUE KEY uq_nntptransitendpoint_host (revision, identifier, host, port),
  CONSTRAINT fk_nntptransitendpoint_send
    FOREIGN KEY (revision, identifier) REFERENCES nntptransitsend (revision, identifier),
  CONSTRAINT chk_nntptransitendpoint_host CHECK (CHAR_LENGTH(TRIM(host)) BETWEEN 1 AND 255),
  CONSTRAINT chk_nntptransitendpoint_port CHECK (port BETWEEN 1 AND 65535)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='Send ConnectTo endpoints. Host is unbracketed. Zero rows is allowed';

CREATE TABLE nntptransitsendpathexclude (
  revision BIGINT UNSIGNED NOT NULL,
  identifier VARCHAR(256) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  ordinal INT UNSIGNED NOT NULL,
  path_token VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
  PRIMARY KEY (revision, identifier, ordinal),
  UNIQUE KEY uq_nntptransitsendpathexclude_token (revision, identifier, path_token),
  CONSTRAINT fk_nntptransitsendpathexclude_send
    FOREIGN KEY (revision, identifier) REFERENCES nntptransitsend (revision, identifier),
  CONSTRAINT chk_nntptransitsendpathexclude_token CHECK (
    CHAR_LENGTH(path_token) BETWEEN 1 AND 255
    AND path_token NOT REGEXP '[[:cntrl:]]'
  )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='Additional Path hops that suppress sending to this peer. Distinct from path_token and from identifier';

CREATE TABLE nntptransitglobalrevision (
  revision BIGINT UNSIGNED NOT NULL,
  published_utc DATETIME(3) NOT NULL,
  want_trash CHAR(1) NOT NULL,
  log_trash CHAR(1) NOT NULL,
  PRIMARY KEY (revision),
  CONSTRAINT chk_nntptransitglobalrevision_revision CHECK (revision >= 1),
  CONSTRAINT chk_nntptransitglobalrevision_want CHECK (want_trash IN ('Y', 'N')),
  CONSTRAINT chk_nntptransitglobalrevision_log CHECK (log_trash IN ('Y', 'N'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='Site-wide Transit junk policy. Not per peer';

CREATE TABLE nntptransitpublication (
  publication_id BIGINT UNSIGNED NOT NULL,
  receive_revision BIGINT UNSIGNED NOT NULL,
  send_revision BIGINT UNSIGNED NOT NULL,
  global_revision BIGINT UNSIGNED NOT NULL,
  published_utc DATETIME(3) NOT NULL,
  PRIMARY KEY (publication_id),
  CONSTRAINT fk_nntptransitpublication_receive
    FOREIGN KEY (receive_revision) REFERENCES nntptransitreceiverevision (revision),
  CONSTRAINT fk_nntptransitpublication_send
    FOREIGN KEY (send_revision) REFERENCES nntptransitsendrevision (revision),
  CONSTRAINT fk_nntptransitpublication_global
    FOREIGN KEY (global_revision) REFERENCES nntptransitglobalrevision (revision),
  CONSTRAINT chk_nntptransitpublication_id CHECK (publication_id >= 1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='One atomic Transit snapshot: receive revision + send revision + global revision';

CREATE TABLE nntptransitcurrent (
  policy_id TINYINT UNSIGNED NOT NULL,
  publication_id BIGINT UNSIGNED NOT NULL,
  PRIMARY KEY (policy_id),
  CONSTRAINT fk_nntptransitcurrent_publication
    FOREIGN KEY (publication_id) REFERENCES nntptransitpublication (publication_id),
  CONSTRAINT chk_nntptransitcurrent_policy_id CHECK (policy_id = 1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='Singleton published Transit snapshot (policy_id=1)';

DELIMITER $$

CREATE TRIGGER trg_nntptransitpublication_peer_set
BEFORE INSERT ON nntptransitpublication
FOR EACH ROW
BEGIN
  IF (SELECT COUNT(*) FROM nntptransitreceive WHERE revision = NEW.receive_revision)
     <> (SELECT COUNT(*) FROM nntptransitsend WHERE revision = NEW.send_revision)
     OR EXISTS (
       SELECT 1
       FROM nntptransitreceive r
       LEFT JOIN nntptransitsend s
         ON s.revision = NEW.send_revision AND s.identifier = r.identifier
       WHERE r.revision = NEW.receive_revision AND s.identifier IS NULL
     )
  THEN
    SIGNAL SQLSTATE '45000'
      SET MESSAGE_TEXT = 'Transit receive and send revisions must contain the same peer identifiers';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitcurrent_publication_forward
BEFORE UPDATE ON nntptransitcurrent
FOR EACH ROW
BEGIN
  IF NEW.publication_id <= OLD.publication_id THEN
    SIGNAL SQLSTATE '45000'
      SET MESSAGE_TEXT = 'nntptransitcurrent.publication_id must increase';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitreceive_insert_frozen
BEFORE INSERT ON nntptransitreceive
FOR EACH ROW
BEGIN
  IF EXISTS (SELECT 1 FROM nntptransitpublication WHERE receive_revision = NEW.revision) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Published Transit receive revision is immutable';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitreceive_update_frozen
BEFORE UPDATE ON nntptransitreceive
FOR EACH ROW
BEGIN
  IF EXISTS (SELECT 1 FROM nntptransitpublication WHERE receive_revision = OLD.revision) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Published Transit receive revision is immutable';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitreceive_delete_frozen
BEFORE DELETE ON nntptransitreceive
FOR EACH ROW
BEGIN
  IF EXISTS (SELECT 1 FROM nntptransitpublication WHERE receive_revision = OLD.revision) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Published Transit receive revision is immutable';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitallowfrom_insert_frozen
BEFORE INSERT ON nntptransitallowfrom
FOR EACH ROW
BEGIN
  IF EXISTS (SELECT 1 FROM nntptransitpublication WHERE receive_revision = NEW.revision) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Published Transit receive revision is immutable';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitallowfrom_update_frozen
BEFORE UPDATE ON nntptransitallowfrom
FOR EACH ROW
BEGIN
  IF EXISTS (SELECT 1 FROM nntptransitpublication WHERE receive_revision = OLD.revision) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Published Transit receive revision is immutable';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitallowfrom_delete_frozen
BEFORE DELETE ON nntptransitallowfrom
FOR EACH ROW
BEGIN
  IF EXISTS (SELECT 1 FROM nntptransitpublication WHERE receive_revision = OLD.revision) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Published Transit receive revision is immutable';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitsend_insert_frozen
BEFORE INSERT ON nntptransitsend
FOR EACH ROW
BEGIN
  IF EXISTS (SELECT 1 FROM nntptransitpublication WHERE send_revision = NEW.revision) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Published Transit send revision is immutable';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitsend_update_frozen
BEFORE UPDATE ON nntptransitsend
FOR EACH ROW
BEGIN
  IF EXISTS (SELECT 1 FROM nntptransitpublication WHERE send_revision = OLD.revision) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Published Transit send revision is immutable';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitsend_delete_frozen
BEFORE DELETE ON nntptransitsend
FOR EACH ROW
BEGIN
  IF EXISTS (SELECT 1 FROM nntptransitpublication WHERE send_revision = OLD.revision) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Published Transit send revision is immutable';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitendpoint_insert_frozen
BEFORE INSERT ON nntptransitendpoint
FOR EACH ROW
BEGIN
  IF EXISTS (SELECT 1 FROM nntptransitpublication WHERE send_revision = NEW.revision) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Published Transit send revision is immutable';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitendpoint_update_frozen
BEFORE UPDATE ON nntptransitendpoint
FOR EACH ROW
BEGIN
  IF EXISTS (SELECT 1 FROM nntptransitpublication WHERE send_revision = OLD.revision) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Published Transit send revision is immutable';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitendpoint_delete_frozen
BEFORE DELETE ON nntptransitendpoint
FOR EACH ROW
BEGIN
  IF EXISTS (SELECT 1 FROM nntptransitpublication WHERE send_revision = OLD.revision) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Published Transit send revision is immutable';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitsendpathexclude_insert_frozen
BEFORE INSERT ON nntptransitsendpathexclude
FOR EACH ROW
BEGIN
  IF EXISTS (SELECT 1 FROM nntptransitpublication WHERE send_revision = NEW.revision) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Published Transit send revision is immutable';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitsendpathexclude_update_frozen
BEFORE UPDATE ON nntptransitsendpathexclude
FOR EACH ROW
BEGIN
  IF EXISTS (SELECT 1 FROM nntptransitpublication WHERE send_revision = OLD.revision) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Published Transit send revision is immutable';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitsendpathexclude_delete_frozen
BEFORE DELETE ON nntptransitsendpathexclude
FOR EACH ROW
BEGIN
  IF EXISTS (SELECT 1 FROM nntptransitpublication WHERE send_revision = OLD.revision) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Published Transit send revision is immutable';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitglobalrevision_update_frozen
BEFORE UPDATE ON nntptransitglobalrevision
FOR EACH ROW
BEGIN
  IF EXISTS (SELECT 1 FROM nntptransitpublication WHERE global_revision = OLD.revision) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Published Transit global revision is immutable';
  END IF;
END$$

CREATE TRIGGER trg_nntptransitglobalrevision_delete_frozen
BEFORE DELETE ON nntptransitglobalrevision
FOR EACH ROW
BEGIN
  IF EXISTS (SELECT 1 FROM nntptransitpublication WHERE global_revision = OLD.revision) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Published Transit global revision is immutable';
  END IF;
END$$

DELIMITER ;

INSERT INTO nntptransitpeer (identifier, peer_name) VALUES
  ('blueworld-hosting', 'Blueworld Hosting'),
  ('giganews', 'Giganews, Inc.'),
  ('usenet-ninja', 'Usenet Ninja');

INSERT INTO nntptransitreceiverevision (revision, published_utc) VALUES (1, UTC_TIMESTAMP(3));
INSERT INTO nntptransitsendrevision (revision, published_utc) VALUES (1, UTC_TIMESTAMP(3));
INSERT INTO nntptransitglobalrevision (revision, published_utc, want_trash, log_trash)
VALUES (1, UTC_TIMESTAMP(3), 'Y', 'Y');

INSERT INTO nntptransitreceive (
  revision, identifier, max_inbound, username, password, defer_on_duplicate,
  max_article_bytes, article_types, patterns
) VALUES
  (1, 'blueworld-hosting', 10, '', '', 'Y', 1048576, 65535, '*'),
  (1, 'giganews', 80, '', '', 'Y', 5242880, 65535, '*'),
  (1, 'usenet-ninja', 10, '', '', 'Y', 5242880, 65535, '*');

INSERT INTO nntptransitallowfrom (revision, identifier, ordinal, entry) VALUES
  (1, 'blueworld-hosting', 0, 'usenet.blueworldhosting.com'),
  (1, 'giganews', 0, 'news-out.nntp.giganews.com'),
  (1, 'usenet-ninja', 0, '198.18.0.0/15');

INSERT INTO nntptransitsend (
  revision, identifier, max_outbound, ssl_mode, username, password,
  max_article_bytes, article_types, patterns, path_token
) VALUES
  (1, 'blueworld-hosting', 10, 'None', '', '', 5242880, 65535, '*,!unidata.*,!control.*,!junk', 'usenet.blueworldhosting.com'),
  (1, 'giganews', 80, 'None', '', '', 5242880, 65535, '*,!unidata.*', 'nntp.giganews.com'),
  (1, 'usenet-ninja', 10, 'None', '', '', 5242880, 65535, '*', 'nntp.usenet.ninja');

INSERT INTO nntptransitendpoint (revision, identifier, ordinal, host, port) VALUES
  (1, 'blueworld-hosting', 0, 'usenet.blueworldhosting.com', 119),
  (1, 'giganews', 0, 'opticnetworks-in.nntp.ord.giganews.com', 119);

INSERT INTO nntptransitpublication (
  publication_id, receive_revision, send_revision, global_revision, published_utc
) VALUES (1, 1, 1, 1, UTC_TIMESTAMP(3));

INSERT INTO nntptransitcurrent (policy_id, publication_id) VALUES (1, 1);
