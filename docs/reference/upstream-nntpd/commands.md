# NNTP command inventory

> **Upstream reference only.** This file describes `VectorNNTP.NNTPD` in the Usenet.ninja
> VectorNNTP repository. It is **not** a claim about commands implemented by
> `VectorNNTPD.BackFillerService` or `VectorNNTPD.StorageService`. Paths such as
> `src/VectorNNTP.NNTPD/...` refer to that upstream tree. Kept here so VectorNNTPD engineers
> can consult the historical NNTP command inventory without opening the source repo.
>
> VectorNNTPD wire behaviour: see [`../../architecture.md`](../../architecture.md) (BackFiller
> client + cache listener) and [`../../storage-nntp-feed.md`](../../storage-nntp-feed.md)
> (Storage ARTICLE / TAKETHIS / QUIT). A `[x]` below does **not** mean Storage or BackFiller implements it.

Tracker for VectorNNTP.NNTPD command implementations under `src/VectorNNTP.NNTPD/Session/Commands/`.
Command-processing infrastructure (dispatch, catalog, execution, response writer, logging) lives under `src/VectorNNTP.NNTPD/Session/CommandProcessor/`.

- `[x]` = real protocol behavior present (not merely a stub class).
- `[ ]` = registered placeholder; returns not-implemented (or equivalent) until the RFC-compliant handler is written.

Do **not** mark `[x]` solely because a `.cs` file exists, or because a LIST keyword is recognized by the parser. Recognition is not implementation.

## Implemented

```text
[x] CAPABILITIES
[x] AUTHINFO USER
[x] AUTHINFO PASS
[x] AUTHINFO SASL
[x] MODE READER
[x] HELP
[x] DATE
[x] MODE STREAM
[x] QUIT
[x] STARTTLS
[x] COMPRESS DEFLATE
[x] TAKETHIS
[x] CHECK
[x] IHAVE
[x] SPEEDTEST
[x] POST
[x] LIST
[x] LIST ACTIVE
[x] LIST COUNTS
[x] LIST NEWSGROUPS
[x] LIST OVERVIEW.FMT
[x] LIST HEADERS
[x] GROUP
[x] ARTICLE
[x] HEAD
[x] BODY
[x] STAT
```

AUTHINFO USER/PASS (RFC 4643 §2.3) caches a username then authenticates. AUTHINFO SASL (RFC 4643 §2.4) implements PLAIN, LOGIN, CRAM-MD5, and SCRAM-SHA-256 only. DIGEST-MD5, SCRAM-SHA-1, SCRAM-SHA-256-PLUS, EXTERNAL, and GSSAPI are not implemented and are not advertised. Challenges use `383` plus Base64 (`383 =` for an empty PLAIN challenge; LOGIN uses Base64 `Username:` / `Password:`; CRAM-MD5 Base64-encodes the RFC 2195 `<random.timestamp@Fqdn>` challenge, where `Fqdn` is the generated Nntpd identity). Success is `281`, or `283` plus Base64 when the mechanism returns additional SASL data (SCRAM server-final). A syntactically valid SCRAM-SHA-256 client-first always receives `383` server-first; unknown, disabled, and non-SCRAM accounts use a process-local dummy verifier and still fail with `481`. Dummy material cannot authenticate. Reader identity comes only from a successful AUTHINFO/SASL exchange in Reader authority (`MODE READER` or unspecified). `MODE STREAM` AUTHINFO uses Transit peer credentials only; there is no Transit ↔ MySQL fallback. MySQL `nntpusers` is loaded through the existing NntpDB pool. CAPABILITIES advertises `AUTHINFO USER SASL` and `SASL PLAIN LOGIN SCRAM-SHA-256 CRAM-MD5` when SASL is registered and cleartext AUTHINFO is permitted.

CHECK (RFC 4644 §2.4) uses HistoryDB `PeekAsync` (local/Redis hit is `438`, or `431` when the connection's Receive `DeferOnDuplicate` is true; `238` double miss without recording the identifier; `431` Redis unavailable regardless of that flag). Consecutive transit-authorized CHECK commands may overlap Redis lookups in a per-session window of **16** (`CheckPipeline.Depth`; architectural constant, not configurable). Responses are emitted in send order. Every other command is a serial barrier: outstanding CHECK replies are drained first. See `docs/architecture.md` (Redis and HistoryDB).

LIST / LIST ACTIVE / LIST COUNTS / LIST NEWSGROUPS (RFC 3977 §7.6 / RFC 6048 §2.2) and GROUP (RFC 3977 §6.1.1) read one captured in-memory `NewsgroupSnapshot`. The NNTP command path does not query MySQL. LIST ACTIVE, LIST COUNTS, and LIST NEWSGROUPS write precomputed UTF-8 lines; a wildmat filters by group name only and an empty match is still `215`. LIST ACTIVE and LIST COUNTS status is exactly the stored octet `y` / `n` / `m` / `x` / `j` (RFC 3977 §7.6.3 plus RFC 6048 §3.1 single-letter values). The RFC 6048 `=<newsgroup>` status form is not supported; this is not complete RFC 6048 LIST ACTIVE status support. LIST COUNTS lines are `group high low estimated status` with a single ASCII space between fields. The estimate is the same GROUP watermark estimate already stored on the snapshot (`high − low + 1` when `high >= low`, except the empty `0 0` case; `0` when `high < low`). It is not an exact stored-article count. ARTICLE / HEAD / BODY / STAT (RFC 3977 §6.2) message-id form resolves through ArticleWork RPC then VATP to a CanonicalV1 `ArticleRecord`, then emits `220` / `221` / `222` / `223` (article number `0` for message-id selection). Local ingest uses `TryAdmit` with `InboundArticleProducer.BackFiller` and does not gate the NNTP response. ArticleWork not-found → `430`; VATP/transfer failures → `400` without closing. Article-number form remains `412` without a selected group and `423` after GROUP; the omitted current-article form is `412` / `420`. GROUP does not invent a current article number from water marks. Unsuccessful lookups do not change the selected group or current article. Error replies are single-line (no multiline terminator). LISTGROUP remains a registered placeholder (`500`) until an article-number source exists; it does not consult the catalogue and does not invent article numbers from `count_low`/`count_high`. LIST OVERVIEW.FMT and LIST HEADERS are static field-list descriptions (RFC 3977 §8.4 / §8.6), not article-data operations. Both write an immortal precomputed `215` multiline response and do not read the catalogue, NntpDb, or articles. OVERVIEW.FMT uses the RFC 3977 §8.4.2 compatibility names `Bytes:` and `Lines:` (not `:bytes` / `:lines`) and does not invent extra fields. LIST HEADERS, LIST HEADERS MSGID, and LIST HEADERS RANGE return the same static field list because HDR forms are not distinguished; metadata items use the RFC HEADERS names `:bytes` and `:lines`. This is not an implementation of OVER, HDR, or article overview storage. LIST MOTD remains recognized without stored information (`503 Data item not stored`). Unknown LIST keywords (ACTIVE.TIMES, DISTRIB.PATS, DISTRIBUTIONS, MODERATORS, SUBSCRIPTIONS, and any unregistered token) remain `501 Unknown command variant`. Extra arguments on MOTD/OVERVIEW.FMT, invalid HEADERS arguments, extra LIST tokens, and invalid wildmats remain `501`. CAPABILITIES advertises `LIST ACTIVE COUNTS HEADERS NEWSGROUPS OVERVIEW.FMT` with READER. OVER and HDR are not advertised.

POST (RFC 3977 §6.3.1) is not pipelined. `440` is returned when the session is not permitted to post and no article is read. After `340`, the article is streamed under command-work accounting (`Nntpd:MaxArticleSize` destuffed). Client headers that are accepted are written through. `From`, `Newsgroups`, and `Subject` are required. A valid client `Date` is preserved; when `Date` is absent, POST writes one generated from the injection time at the header/body boundary and does not replace a valid client `Date`. A valid client `Message-ID` is preserved; when `Message-ID` is absent, POST writes a generated one. A client `Path` is discarded. Server-owned headers (`Path: .POSTED`, `Injection-Date`, `Injection-Info` without a plaintext peer IP, and authenticated-encrypted `X-Trace`) are generated at the header/body boundary. `NNTP-Posting-Date` and `NNTP-Posting-Host` are discarded and are not generated. Client `Control` is rejected (`441`) unless the session has `ControlCancelPermitted` (newsmaster AUTHINFO only) and the header is exactly `cancel <message-id>` per RFC 5536 / RFC 5537 §5.3. Ordinary authenticated users cannot inject `Control`. The protected `X-Trace` payload includes peer IP, peer port, injection timestamp, trace id, and the AUTHINFO username captured at POST admission (empty when the session is unauthenticated). The body is copied with stuffing preserved into one owned stuffed buffer (IHAVE/TAKETHIS queue representation) and admitted with `TryAdmit`. History Peek stays after the terminator and before ArticleRecord / PostFilter. After a successful `ArticleRecord` create, POST first applies the session `account_art_type` capability when an AUTHINFO policy is present (`441` if the article flags are not a subset of the allowed mask; unauthenticated/newsmaster remain unrestricted), then evaluates the immutable PostFilter snapshot loaded from NntpDB (`nntppostfilterpolicy`; seed `gate=Disabled`: no quota, no SPAMD). A PostFilter reject is `441 Posting failed`; the internal reason is not on the wire. Rejection evidence is enqueued asynchronously and cannot change the wire code. Unapproved catalogue `m` submission, IHAVE, and TAKETHIS do not run PostFilter. TAKETHIS enforces the same account capability after `ArticleRecord` creation (`439`). IHAVE still queues stuffed wire and classifies downstream of `235`, so IHAVE cannot enforce ArtType at the command boundary. ARTICLE/HEAD/BODY/STAT still have no stored ArtType. See `docs/postfilter.md`. `240` is sent only after queue admission; POST does not await spool or worker processing.

RFC 3977 does not expose the article `Newsgroups:` list before `340`, so group-policy failures after the article (or at the header/body boundary during receive) are `441 Posting failed`, not `440`. Every `Newsgroups:` target is parsed by the existing POST header parser, then checked against one captured `NewsgroupCatalogue.Current` snapshot using the same case-insensitive `TryGet` as GROUP/LIST.

| `posting_status` | Local POST |
|------------------|------------|
| `y` | Ordinary local posting (when every other target is also `y`, or `y` plus authorized/forwarded `m`) |
| `n` | Rejected — local posting prohibited |
| `m` | Moderated. No `Approved:` → proto-article is offered to `IModerationSubmissionService` for the leftmost moderated group (RFC 5537 §3.5.1) and is not injected. `Approved:` → ordinary injection only when the authenticated AUTHINFO principal is authorized by the `nntpmoderators` catalogue for every moderated target and the header identities match those routes. |
| `x` | Rejected — closed: local posting and peer articles are prohibited |
| `j` | Rejected — peer-only: local posting is not accepted |
| unknown name | Rejected — the group is not in the captured snapshot |

`Approved:` is an assertion (RFC 5536 mailbox-list), not a boolean and not trust by itself. Header-name comparison is case-insensitive. Mailbox identities compare ASCII case-insensitively. Conflicting identities are rejected; identical duplicates collapse. `Approved: 1` / `true` are malformed. `Control:PgpAuthorities` is not a moderator catalogue.

`nntpmoderators.moderator_address` is a routing destination (static mailbox or INN `%s` template: matched newsgroup name with `.` → `-`). That address is where an unapproved proto-article would be submitted. It does not authenticate a client. `account_name` is the AUTHINFO principal required for local reinjection. The INN `samples/moderators` URL in `Moderation:Source` is provenance only.

Moderator reinjection is a normal POST: `AUTHINFO USER/PASS`, then `POST` with `Approved: moderator@example.com`. There is no `MODERATE` command. The authenticated session identity plus a configured `Username` mapping is the authorization.

Unapproved moderated forwarding happens after Message-ID/Date are present and before Injection-Info/Injection-Date (RFC 5537 §3.5 step 7). The original Message-ID is preserved. The production `IModerationSubmissionService` composes an RFC 5537 `application/news-transmission; usage=moderate` email and calls `IEmailService.SendAsync`. `240` means the complete moderation email was durably written to the local filesystem spool (RFC 3977: received, possibly following further processing). It is not proof of remote SMTP or moderator receipt. SMTP delivery is asynchronous. When email is disabled, the spool write fails, or encoding fails, POST returns `441`. POST does not perform SMTP. Successful submission does not Peek, `TryAdmit`, or Remember.

Group-policy and unauthorized-approval rejection drain the article. History Peek / `TryAdmit` / Remember happen only after injection authorization succeeds. Malformed or empty `Newsgroups:` remain syntax failures (`441`) and do not consult the snapshot.

## Recognized LIST keywords (information not stored)

These parse as valid LIST keywords and return `503 Data item not stored`. That is the RFC 3977 §7.6.1.2 response for a recognized keyword whose information is not maintained. It is not a `500` unimplemented-command placeholder, and it is not a successful LIST implementation.

```text
LIST MOTD
```

## Placeholder (registered, not implemented)

```text
[ ] LISTGROUP
[ ] LAST
[ ] NEXT
[ ] OVER
[ ] HDR
```

## File map

| File | Commands |
|------|----------|
| `Capabilities.cs` | CAPABILITIES |
| `AuthInfo.cs` | AUTHINFO USER, PASS, SASL |
| `Mode.cs` | MODE READER, MODE STREAM |
| `Help.cs` | HELP |
| `Date.cs` | DATE |
| `Quit.cs` | QUIT |
| `StartTls.cs` | STARTTLS |
| `Compress.cs` | COMPRESS DEFLATE |
| `List.cs` | LIST, LIST ACTIVE, LIST COUNTS, LIST NEWSGROUPS, LIST OVERVIEW.FMT, LIST HEADERS; MOTD returns 503 |
| `Group.cs` | GROUP |
| `ListGroup.cs` | LISTGROUP |
| `Newsgroups/` | Immutable catalogue snapshot + five-minute refresh |
| `Article.cs` | ARTICLE, HEAD, BODY, STAT |
| `Last.cs` | LAST |
| `Next.cs` | NEXT |
| `Over.cs` | OVER |
| `Hdr.cs` | HDR |
| `Post.cs` | POST |
| `IHave.cs` | IHAVE |
| `Check.cs` | CHECK |
| `Session/CheckPipeline.cs` | Per-session CHECK overlap window (not a command) |
| `TakeThis.cs` | TAKETHIS |
| `SpeedTest.cs` | SPEEDTEST (VectorNNTP diagnostic extension) |
| `Session/SpeedTest/` | SPEEDTEST coordinator, payload, concurrency |

IHAVE (RFC 3977 §6.3.2) is a serial, non-pipelined transit ingest: HistoryDB peek → non-blocking Transit queue probe → `335`/`435`/`436` → raw article receive (frame `CRLF . CRLF`, stuffed wire) → destuff + `ArticleRecordFactory` → CanonicalV1 `ArticleRecord` → non-blocking `TryAdmit` → `235`/`436`/`437`. A History hit is `435`, or the pre-article `436 Transfer not possible; try again later` when the connection's Receive `DeferOnDuplicate` is true; neither reads an article. IHAVE never waits for queue memory; temporary inability to accept is `436`. Articles that cannot become CanonicalV1 are `437` after `335`. TAKETHIS production receive/framing is unchanged. See `docs/architecture.md` (IHAVE article ingestion).

`SPEEDTEST <identifier>` is a VectorNNTP private diagnostic extension (not an RFC command). `<identifier>` is the configured Transit peer identifier (exact ordinal match, one NNTP token). `PeerName` is the human-readable administrative name and is not a command argument. The command is transit-authorized and measures TX on the **current** NNTP session with a synthetic payload. It does not open outbound Transit sockets, does not use article ingestion, HistoryDB, or Redis, and never reports measurements that were not performed. Outbound initiator support is not implemented (`OUTBOUND=NOT-AVAILABLE`). See `docs/architecture.md`.

Article ingestion (TAKETHIS / POST / IHAVE → CanonicalV1 `ArticleRecord` → byte-budgeted queue → `IncomingSpoolWriterService` / `IngestionWorkerPool` → protobuf OverviewDB handoff on `overviewdb.queue` → dedicated Serilog `news` file → dedicated Serilog Path-survey `inpaths` file → /dev/null no-op sink) lives under `ArticleIngestion/`. Accepted `+`/`j` lines are generated after a confirmed OverviewDB publish. The Path-survey line is `Path: ` plus canonical `ArticleRecord.Path` from that same record; it is not added to news. Deliberate rejections write `-` at the protocol decision because those articles never enter the queue. Successful moderated POST writes `m` at that decision. Cancel (`c`) is not implemented. The news line format is application-owned and not configurable (innd `ARTlog` field order: inbound Transit identifier, Message-ID, size, then outbound `?` only on accepted `+`; `j`/`-`/`m` omit outbound sites; response codes are not rendered). The Path-survey format is likewise application-owned (`Path: <canonical-path>` plus LF) and is not configurable. Serilog owns path, rolling, retention, compression, and flush for both files. Completed `inpaths` files are streamed by the internal ninpaths processor when `Nntpd:Top1000` has recipients.

Registration lives in `Session/CommandProcessor/DefaultNntpCommandCatalog.cs` (descriptors + authorization metadata only).

## NNTPCancelMessage

`src/VectorNNTP.NNTPCancelMessage` is a separate newsmaster client. It is not part of the NNTP server runtime.

```text
NNTPCancelMessage --host nntpd01.usenet.ninja --port 563 --tls \
  --username newsmaster --password 'secret' <message-id>

NNTPCancelMessage --host nntpd01.usenet.ninja --port 563 --tls \
  --username newsmaster --password 'secret' --cancel <message-id>
```

`--cancel` / `-cancel` is the destructive confirmation. Command-line values override `NNTPCancelMessage.json`. `--password` can appear in OS process listings and shell history; prefer environment or secrets.

Flow: connect → AUTHINFO USER/PASS when credentials are supplied (mandatory for `--cancel`) → `HEAD <message-id>` → decrypt original `X-Trace` → if `--cancel`, canonicalize PGPVERIFY signed fields, sign, then POST an RFC 5536/5537 cancel article that copies the original `Newsgroups` and uses `Control: cancel <message-id>` (not the obsolete RFC 1036 `cmsg` Subject convention). `--cancel` cannot skip signing. RFC 1036 is retained under `docs/standards/rfcs/` for historical reference only. RFC 5536 and RFC 5537 govern the CANCEL article. PGPVERIFY is a de-facto interoperability convention (see `docs/standards/pgpverify/`); it is not an RFC-defined standard. The signed header is `X-PGP-Sig` as FORMAT specifies, not PGP/MIME and not `X-PGP-Signature`. Server-owned Path / Injection-Date / Injection-Info / X-Trace remain unsigned and are generated by POST.

**HEAD note:** VectorNNTP.NNTPD `HEAD <message-id>` retrieves CanonicalV1 headers through ArticleWork + VATP when BackFiller can supply the article. Numeric `HEAD` forms remain lookup-failure only.
