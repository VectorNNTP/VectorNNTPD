# Apache SpamAssassin SPAMD network protocol

## Document

| Field           | Value                                                                 |
| --------------- | --------------------------------------------------------------------- |
| Title           | The SpamAssassin Network Protocol                                     |
| Author / org    | Apache SpamAssassin                                                   |
| Local file      | [`PROTOCOL.md`](PROTOCOL.md)                                          |
| Source (browse) | https://github.com/apache/spamassassin/blob/trunk/spamd/PROTOCOL      |
| Source (raw)    | https://raw.githubusercontent.com/apache/spamassassin/trunk/spamd/PROTOCOL |
| Upstream SHA-256 | `ddd66ba900080cb510244bcb6e855f13ea469febe24373a84cff323caaaf0af5`  |

This directory holds a bundled copy of the Apache SpamAssassin `spamc`/`spamd`
network protocol specification used by **upstream** VectorNNTP.NNTPD PostFilter.
VectorNNTPD.BackFillerService and VectorNNTPD.StorageService do not implement a SPAMD client.

`PROTOCOL.md` begins with a short VectorNNTP provenance note. The protocol
specification text after that note is a **verbatim** copy of the raw upstream
document. Do not edit the specification for style or summaries; replace it only
by re-downloading the upstream file and updating the hash when that document
changes.

The upstream document states that it is perpetually in draft status. This
library does not assign a protocol version or revision date beyond what the
upstream text itself contains.
