# NntpDB schema references

SQL scripts for catalogues used by VectorNNTPD hosts. There is **no application migration
runner** that applies these files automatically. Operators apply them to the site MySQL
database when enabling the corresponding features.

| File | Used by | Role |
|------|---------|------|
| [nntptransit.sql](nntptransit.sql) | **TransitService** (also present as duplicated plumbing in ReaderService sources) | Transit control-plane DDL (peer / receive / send / publication). Code SELECTs via `NntpTransitQueries`; it does not CREATE. |
| [postfilter.sql](postfilter.sql) | **ReaderService** PostFilter (duplicated types also exist under TransitService) | PostFilter policy DDL. Apply before enabling PostFilter. See [../nntpd-reader-transit.md](../nntpd-reader-transit.md) and [../reference/upstream-nntpd/postfilter.md](../reference/upstream-nntpd/postfilter.md). |
| [nntpusers-account-art-type.sql](nntpusers-account-art-type.sql) | **ReaderService** AUTHINFO ArtType capability | Additive `nntpusers.account_art_type`. Apply before deploying a Reader that SELECTs that column. |

## Not provisioned by these scripts

| Catalogue / store | Used by | How maintained |
|-------------------|---------|----------------|
| `nntpsharedconfig` (single row) | All hosts (column subsets differ) | External ops; no checked-in CREATE TABLE. |
| `nntpbackfilleraccounts` | BackFiller only | External ops; `MySqlProviderAccountSource` SELECTs. |
| SQLite `ArticleIndex` / `ArticleIndexMeta` | StorageService | Created in-process (`ArticleIndexDatabase`). See [../storage.md](../storage.md). |

## Deployment notes

- Do **not** treat these scripts as a BackFiller or StorageService install step.
- Transit and PostFilter DDL **are** required when operating TransitService / Reader PostFilter against a fresh NntpDB.
- Redis is required for Reader PostFilter quota and related Reader session features; it is **not** required for BackFiller or StorageService.
