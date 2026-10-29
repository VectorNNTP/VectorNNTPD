-- Additive nntpusers.account_art_type for AUTHINFO ArticleType capability.
-- Apply to the existing account database before deploying an NNTPD that
-- SELECTs account_art_type. Idempotent. Does not drop nntpusers.
-- Default 65535 is ArticleTypeCapabilities.All (unrestricted for existing users).
-- NNTPD does not run this script.

DROP PROCEDURE IF EXISTS nntpusers_add_account_art_type;

DELIMITER $$
CREATE PROCEDURE nntpusers_add_account_art_type()
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM information_schema.columns
    WHERE table_schema = DATABASE()
      AND table_name = 'nntpusers'
      AND column_name = 'account_art_type'
  ) THEN
    ALTER TABLE nntpusers
      ADD COLUMN account_art_type INT UNSIGNED NOT NULL DEFAULT 65535
      COMMENT 'ArticleType flags; 65535=ArticleTypeCapabilities.All (unrestricted)';
  END IF;
END$$
DELIMITER ;

CALL nntpusers_add_account_art_type();
DROP PROCEDURE nntpusers_add_account_art_type;
