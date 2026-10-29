# PGPVERIFY (de-facto control-message authentication)

PGPVERIFY is a **de-facto Netnews control-message authentication convention**.
It is **not** standardized by RFC 5537.

RFC 5537 §5.1 states that there is no standardized means of authenticating
control-message senders, and cites `[PGPVERIFY]` as an unstandardized
mechanism in common use. VectorNNTP implements PGPVERIFY so CANCEL articles
posted by `NNTPCancelMessage` can be verified by INN `pgpverify` and
other Netnews infrastructure that already understands this convention.

Do not describe PGPVERIFY as RFC-compliant. The CANCEL article itself follows
RFC 5536 / RFC 5537. PGPVERIFY is an interoperability extension on top of that
article.

This is **not** PGP/MIME (RFC 3156), S/MIME, or a VectorNNTP-specific header.

## Local copies

| File | Role | Retrieved from |
|------|------|----------------|
| [`FORMAT`](FORMAT) | Signing and verification input (David Lawrence, “Signing Control Messages”) | https://www.eyrie.org/~eagle/usefor/other/pgpverify (Usefor copy of `ftp://ftp.isc.org/pub/pgpcontrol/FORMAT`, the URL cited by RFC 5537) |
| ISC operator README (HTML not vendored here) | “Authentication of Usenet Group Changes” — use the upstream URL; the large `README.html` copy was omitted from this migration | https://ftp.isc.org/pub/pgpcontrol/README.html (also cited by RFC 5536) |

The original ISC FTP URL `ftp://ftp.isc.org/pub/pgpcontrol/FORMAT` returned HTTP 500 at retrieval time. The Usefor copy is the same David Lawrence (`tale@isc.org`) document RFC 5537 points at.

## FORMAT vs deployed INN vs VectorNNTP

PGPVERIFY FORMAT and INN `pgpverify` detached verification diverge on trailing
horizontal whitespace, including the empty signed Sender header. FORMAT is not
amended by INN. VectorNNTP follows INN so generated CANCEL controls verify on
deployed servers.

| Source | Empty signed Sender | Trailing whitespace |
|--------|---------------------|---------------------|
| Historical/documented FORMAT | `Sender: \n` (colon + space required, including empty headers) | FORMAT does not strip SP/HT before LF |
| Deployed INN `pgpverify` 1.23–1.31 | Reconstructs `Sender: \n`, then hashes `Sender:\n` | `$message =~ s/[ \t]+\n/\n/g` after reconstruction (INN documents this as compatibility with historical attached signatures) |
| VectorNNTP | `Sender:\n` in the hashed bytes; Sender omitted on the wire (INN `parse_header` rejects empty on-wire values) | Same SP/HT-immediately-before-LF rule as INN detached verification |

## VectorNNTP mapping

| FORMAT rule | VectorNNTP CANCEL |
|-------------|-------------------|
| Signature header | `X-PGP-Sig` (not `X-PGP-Signature`, not PGP/MIME) |
| First tokens | `<version> <comma-separated-header-names-no-spaces>` |
| Signature body | tab-folded radix64 from an ASCII-armored detached OpenPGP signature |
| Signed headers (X-PGP-Sig example list) | `Subject,Control,Message-ID,Date,From,Sender` |
| Signed data prefix | `X-Signed-Headers: ` + that list (pseudoheader; not on the wire) |
| Header syntax | FORMAT construction is `Name: ` (colon + space), including missing headers; INN then strips trailing SP/HT before LF, so empty Sender hashes as `Sender:\n` |
| Sender | empty signed header present in canonical data as `Sender:\n`; omitted from the article (INN `parse_header` rejects empty on-wire values) |
| Body | signed; CRLF normalized to LF; trailing SP/HT immediately before LF stripped (INN rule) |
| Line endings in signed data | Unix LF (not `Environment.NewLine`) |
| Encoding | UTF-8 bytes of that LF text |
| Added after signing | `Newsgroups` (FORMAT), then server Path / Injection-Date / Injection-Info / X-Trace |
| OpenPGP | detached binary-document signature, SHA-256, RSA; package `BouncyCastle.Cryptography` 2.7.0 |

## SHA-256

| File | SHA-256 |
|------|---------|
| `FORMAT` | `10556ca2d8c3b53918ae60b2baa7061596c0f9e4c9979f0c51560ac20dfb69ca` |
| `README.html` | `3d6dbdccda36df4d930a1ed4db13842e6025cde632d487ad637773b44d87b629` |
