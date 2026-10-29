# HAProxy PROXY protocol (normative reference)

## Document

| Field           | Value                                                                           |
| --------------- | ------------------------------------------------------------------------------- |
| Title           | The PROXY protocol — Versions 1 & 2                                             |
| Author / org    | Willy Tarreau / HAProxy Technologies                                            |
| Document date   | **2026/04/27**                                                                  |
| Local file      | [`proxy-protocol.txt`](proxy-protocol.txt)                                      |
| Source (raw)    | https://raw.githubusercontent.com/haproxy/haproxy/master/doc/proxy-protocol.txt |
| Source (browse) | https://github.com/haproxy/haproxy/blob/master/doc/proxy-protocol.txt           |
| SHA-256         | `6b8b5b2dbdcbca8870ae8dca37dc5d2d68ebb6c7638e2309f8a5af753cd66b30`              |

The local file is a **verbatim** copy of the raw document above. Do not edit
it for style or summaries; replace it only by re-downloading and updating the
hash when the upstream document changes.

## Role in upstream VectorNNTP / pyNNTPD

Historical note from the standards bundle. VectorNNTPD keeps this file as a protocol
reference; do not infer that PROXY protocol is implemented by every host in this solution.

- IETF RFCs (`docs/rfcs/`) define NNTP and related **protocol** behaviour.
- This HAProxy document is an **external normative interoperability standard**
  for transporting original connection metadata across TCP proxies.
- pyNNTPD implements the **receiver** side needed for a trusted HAProxy
  backend: validate and consume PROXY v1/v2 **before** TLS/NNTP processing
  when `PYNNTPD_PROXY_TRUSTED_SOURCES` is non-empty.

## Explicit non-goals

Optional PROXY SSL / client-certificate **TLVs** described in the document do
**not** imply that pyNNTPD authenticates TLS clients with certificates.
pyNNTPD does not request, inspect, or validate client certificates.

A pyNNTPD/VectorNNTP PROXY compliance matrix lived beside this file in the upstream
repository (`docs/standards/haproxy-proxy-protocol-compliance.md`). It was not
migrated into VectorNNTPD because this solution does not claim PROXY receiver
behaviour for BackFiller or StorageService.
