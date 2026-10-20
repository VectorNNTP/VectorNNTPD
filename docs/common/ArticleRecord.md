# ArticleRecord v1

Authoritative developer reference for the Common in-memory article envelope implemented in `VectorNNTPD.Common`.

This document describes the **current** implementation. It does not invent APIs, limits, or integrations that are not present in `src/` and `tests/`.

| Concern | Owner |
|---|---|
| Types, factory, parser, Date/Path, classifier, ArtHash | `src/VectorNNTPD.Common/Articles/` |
| Tests | `tests/VectorNNTPD.Common.Tests/Articles/` |
| ArticleId hashing | `src/VectorNNTPD.Common/Articles/ArticleId.cs` |
| OverviewDB protobuf + codec | `src/VectorNNTPD.Common/Articles/OverviewDb/` |
| History identity reuse (same bytes, not a second hash) | `src/VectorNNTP.NNTPD/History/HistoryDigest.cs` |
| OverviewDB RabbitMQ transport | `src/VectorNNTP.NNTPD/` (topology, confirms, expiration, AppId) |

NNTPD TAKETHIS, POST, and IHAVE construct an ArticleRecord before queue admission. NNTPD ingestion workers encode a compact protobuf OverviewDB handoff (`OverviewArticleV1` in `VectorNNTPD.Common`) and publish those bytes to RabbitMQ `overviewdb.queue`. RabbitMQ remains application-owned. ArticleRecord is **not** a serialization protocol of its own and is not wired into BackFiller ingestion, article-work RPC JSON, retention, storage, or transit.

---

## 1. Overview

`ArticleRecord` is a `readonly struct` that holds the result of one successful Common parse, Diablo-derived classification, and Date/Path canonicalization. The factory input is a destuffed article. The retained `ArtData` is the stuffed canonical image.

It exists so Vector processes can keep article identity, type, size, line count, overview ranges, and a fast fingerprint of the canonical bytes **without scanning the article again**. Downstream code that already has a `CanonicalV1` record should read `ArtId`, `ArtType`, `Fields`, `ArtSize`, and `ArtLines` instead of calling `NntpArticleParser.Parse` again.

Common owns the reusable model:

- `ArticleRecord`, `ArticleRecordFactory`, `ArticleId`, `ArticleType`, `ArticleTypeClassifier`
- The byte-oriented parser, Date resolver, Path analyzer, yEnc validator, and Date/Path materializer used to build the record

Current scope is the Common data model and factory only. Destuff is a **caller** responsibility. The factory does not destuff, does not compare a request Message-ID, and does not publish or retain the article.

Current NNTPD wiring:

- TAKETHIS: ArticleRecord ingress is implemented
- POST: ArticleRecord ingress is implemented
- IHAVE: remains raw/unstructured (not ArticleRecord ingress)

Deliberately excluded today:

- BackFiller `ProviderArticleWorkHandler` / retention
- RabbitMQ Article Work payloads
- Storage and transit protocols
- A full serialized envelope of ArticleRecord / ArtData (OverviewDB protobuf is a metadata subset only)
- A second-hop Path API on an existing `ArticleRecord`

---

## 2. ArticleRecord structure

Defined in [`src/VectorNNTPD.Common/Articles/ArticleRecord.cs`](../../src/VectorNNTPD.Common/Articles/ArticleRecord.cs).

```csharp
public readonly struct ArticleRecord
{
    public ArticleId ArtId { get; }
    public ulong ArtHash { get; }
    public int ArtSize { get; }          // _artData?.Length ?? 0
    public ArticleType ArtType { get; }
    public int ArtLines { get; }
    public DateTime CanonicalUtc { get; }
    public ArticleParseStatus ParseStatus { get; }
    public ReadOnlyMemory<byte> ArtData { get; }
    public ArticleFieldTable Fields { get; }

    public ReadOnlySpan<byte> MessageId { get; }
    public ReadOnlySpan<byte> Newsgroups { get; }
    public ReadOnlySpan<byte> Subject { get; }
    public ReadOnlySpan<byte> From { get; }
    public ReadOnlySpan<byte> Date { get; }
    public ReadOnlySpan<byte> References { get; }
    public ReadOnlySpan<byte> Path { get; }
}
```

The constructor is `internal`. The public construction path is `ArticleRecordFactory.TryCreate`.

| Member | Type | Meaning |
|---|---|---|
| `ArtId` | `ArticleId` | BLAKE3 of the Message-ID **value** bytes in ArtData |
| `ArtHash` | `ulong` | XXH3-64 fingerprint of the entire ArtData buffer |
| `ArtSize` | `int` | `ArtData.Length` (0 when the default/empty record has no buffer) |
| `ArtType` | `ArticleType` | Diablo-derived flags (not `NntpArticleType`) |
| `ArtLines` | `int` | Body line count from the parser walk (overview `:lines`) |
| `CanonicalUtc` | `DateTime` | Winning Date-family header resolved to UTC |
| `ParseStatus` | `ArticleParseStatus` | `None` or `CanonicalV1` |
| `ArtData` | `ReadOnlyMemory<byte>` | Canonical stuffed article (headers + blank line + body with dot-leading lines already stuffed). The NNTP terminator is not included. |
| `Fields` | `ArticleFieldTable` | Integer ranges into **this** ArtData |

The span properties slice `Fields.*` against the referenced buffer. They allocate no header strings.

A default `ArticleRecord` (factory rejection) has `ParseStatus.None` and `ArtSize == 0`.

---

## 3. ArtId / ArticleId

Implementation: [`src/VectorNNTPD.Common/Articles/ArticleId.cs`](../../src/VectorNNTPD.Common/Articles/ArticleId.cs).  
Tests: [`tests/VectorNNTPD.Common.Tests/Articles/ArticleIdTests.cs`](../../tests/VectorNNTPD.Common.Tests/Articles/ArticleIdTests.cs).

`ArticleId` is a 32-byte value type (`4 × ulong`, `Length = 32`). `FromMessageId` calls Blake3.NET `Hasher.Hash` on the supplied span and stores the digest little-endian. The value type itself has no heap storage; `FromMessageId` plus `CopyTo` allocates 0 bytes after warmup (`ValueType_DoesNotAllocateAfterWarmup`).

### Exact hash input

The factory hashes `fields.MessageId.Slice(artData)` — the Message-ID **header value** as stored in canonical ArtData:

- Includes `<` and `>`
- Does **not** include the `Message-ID:` field name
- Does not trim, case-fold, or convert to a string

Tests:

- `FromMessageId_HashesValueBytes_NotFieldName` — `"Message-ID: <id@example>"` ≠ `"<id@example>"`
- `FromMessageId_DoesNotTrimOrCaseFold` — `"<Id@Example>"` ≠ `"<id@example>"`; leading/trailing spaces change the digest
- Known vector: BLAKE3 of ASCII `<id@example>` is  
  `CFC44E7F244E2AAB6D1E7FF93F9FB93C5DDE9DAFAA16F6DB2FBBAE712881D250`

ArtId is **identity**, not a content fingerprint. Path and Date rewrites do not change the Message-ID value, so ArtId stays the same while ArtHash/ArtSize/ranges change.

### HistoryDigest

NNTPD `HistoryDigest` wraps `ArticleId`. That project is not in this repository. `HistoryDigest.FromMessageId` is `ArticleId.FromMessageId`. `HistoryDbTests.HistoryDigest_MatchesCommonArticleIdBytes` asserts identical 32-byte output.

This is not the BackFiller Success `articleId` field, which is the lowercase hexadecimal `ArticleId`.

---

## 4. ArtHash

Implementation: `System.IO.Hashing.XxHash3.HashToUInt64` (package `System.IO.Hashing` 10.0.12).  
Factory: one-shot `XxHash3.HashToUInt64(artData)` **after** materialize, over the completed canonical `byte[]`.  
Tests: `ArticleRecordArtHashTests`, `ArticleRecordTests.TryCreate_ArtHash_CoversEntireArtData`.

| Property | Value |
|---|---|
| Algorithm | XXH3-64 (non-cryptographic) |
| Width | 64-bit `ulong` |
| Coverage | Exact final canonical ArtData bytes |
| Known vector | `XxHash3.HashToUInt64("123456789") == 0x72DCB18B67A17DFF` |
| Invariant | `record.ArtHash == XxHash3.HashToUInt64(record.ArtData.Span)` |

ArtHash is an **internal deterministic fingerprint** of the final canonical ArtData. It is **not** cryptographic identity and **must not** be treated as proof of byte equality. Equal ArtHash values do not prove two buffers are identical.

ArtId remains separate: BLAKE3 of the Message-ID **value** only. Date/Path rewrites change ArtHash (and ArtSize/ranges) but not ArtId.

Do not hash destuffed input, the Message-ID alone, or only the body. Hash the materialized ArtData.

yEnc trailer checks are IEEE CRC-32 of **decoded yEnc payload**, computed by `YEncArticleValidator` with `System.IO.Hashing.Crc32`. `YEncCrc32` / `IeeeCrc32` remain the scalar reference for that same value. That is a different coverage and algorithm from ArtHash.

ArtHash is not folded into the materializer. The path is: materialize one `byte[]` → `HashToUInt64` → store on the record.

---

## 5. ArtSize

`ArtSize` is `ArtData.Length` of the **canonical stuffed** article: headers + blank-line separator + body, after Date/Path rewrite, plus one byte for each body line whose first content byte is `.`.

Header lines are not stuffed. The terminating NNTP `.CRLF` is not stored, so `ArtSize` is not the full multiline response length. Tests assert `record.ArtSize == record.ArtData.Length` and, when Path is rewritten, `ArtSize != destuffed.Length` (`TryCreate_ProducesCanonicalV1RecordWithDiabloTypeAndFinalRanges`). A body with no dot-leading line has the same length as the pre-stuff projection.

---

## 6. ArtType

Implementation: [`ArticleType.cs`](../../src/VectorNNTPD.Common/Articles/ArticleType.cs), [`ArticleTypeClassifier.cs`](../../src/VectorNNTPD.Common/Articles/ArticleTypeClassifier.cs).  
Tests: [`ArticleTypeClassifierTests.cs`](../../tests/VectorNNTPD.Common.Tests/Articles/ArticleTypeClassifierTests.cs). NNTPD has a separate classifier test project that is not in this repository.

`ArticleType` is `[Flags]`. Bits match NNTPD `TransitMessageTypes` (`YEncoded` is the same bit as transit `Yenc`). Inbound Transit receive policy compares this classified value with the peer's receive mask. Send article-type matching is not wired.

| Flag | Bit | Established meaning |
|---|---|---|
| `None` | 0 | No bits |
| `Default` | 1<<0 | Ordinary text; no other marker (`Classify` returns this when nothing else is set) |
| `Control` | 1<<1 | Header `Control:` |
| `Cancel` | 1<<2 | Header `Control: cancel ` / tab |
| `Mime` | 1<<3 | `Mime-Version:` or `Content-Type:` |
| `Binary` | 1<<4 | Binary transfer / octet-stream / encode markers |
| `UuEncode` | 1<<5 | Body line `BEGIN ` (length > 6) |
| `Base64` | 1<<6 | `Content-Transfer-Encoding: base64` |
| `YEncoded` | 1<<7 | `=ybegin line=` or `=ybegin part=` |
| `BommaNews` | 1<<8 | `Content-Transfer-Encoding: X-Bommanews` |
| `UniData` | 1<<9 | `Content-Transfer-Encoding: X-UnidataEncoding` |
| `Multipart` | 1<<10 | `Content-Type: multipart` |
| `Html` | 1<<11 | `Content-Type: text/html` |
| `PostScript` | 1<<12 | `Content-Type: application/postscript` |
| `BinHex` | 1<<13 | `Content-Type: application/mac-binhex40` |
| `Partial` | 1<<14 | `message/partial` or `=ybegin part=` |
| `PgpMessage` | 1<<15 | Body `-----BEGIN PGP MESSAGE-----` |

`ArticleTypeClassifier.Classify(headers, bodyPrefix)` scans destuffed CRLF lines (8 KiB body prefix). `ObserveLine` is incremental. If `Binary` is already set and the line is body, only yEnc begin markers are considered (`BinaryShortCircuit_SkipsLaterBodyMarkersExceptYenc`). Header Control/Cancel is still observed when `inHeader` is true.

The factory sets `ArtType` with `ArticleTypeClassifier.Classify(parse.HeaderBytes.Span, parse.BodyBytes.Span)` — destuffed parse slices. Materialization does not rewrite Content-Type/CTE/body markers, so those flags match the canonical article.

### Not `NntpArticleType`

The parser still computes a coarse exclusive `NntpArticleType` (`Unknown` / `Text` / `MimeMultipart` / `YEnc` / `BinaryEncoded` / `Malformed`) on `NntpArticleParseResult.ArticleType`. That is **not** `ArticleRecord.ArtType`. Tests assert `record.ArtType.GetType() != typeof(NntpArticleType)`.

### Known limitation

The classifier does **not** implement Diablo `classifyLineAsTypes()` character-table scanning or 8-line UUE/B64/BHX confirmation. Header/yEnc/prefix markers and the Binary short-circuit are what exists. Do not assume full Diablo body-table fidelity.

---

## 7. ArtLines

The parser’s `TryValidateBodyLineLengths` walks the destuffed body while enforcing `MaxHeaderLineBytes` (default 1024). Each physical line increments `BodyLineCount`. The factory copies that to `ArtLines`.

Semantics (tests: `Parse_BodyLineCount_MatchesParserLineWalk`):

| Body | ArtLines |
|---|---|
| Empty | 0 |
| `one\r\ntwo\r\n` | 2 |
| `one\r\ntwo` (no final terminator) | 2 |

Terminators: CR, LF, or CRLF (`FindLineTerminator` / `AdvancePastTerminator`). The last unterminated fragment counts as one line.

Materialization copies body lines and inserts one leading `.` on each line whose first content byte is `.`. `ArticleBodyWireStuffer` uses the same CR, LF, and CRLF line ends as that walk, so a bare CR before a dot-leading line is stuffed. That does not add a line, so the destuffed body line count stays valid for canonical ArtData. Overview `:lines` can use `ArtLines` without another body scan. `:bytes` is the stuffed `ArtSize`. Delivery framing still requires exclusive CRLF; a retained article that used bare CR or bare LF is not frameable.

---

## 8. CanonicalUtc and Date handling

Implementations:

- [`ArticleDateHeaderResolver.cs`](../../src/VectorNNTPD.Common/Articles/DateParser/ArticleDateHeaderResolver.cs)
- [`NewsDateParser.cs`](../../src/VectorNNTPD.Common/Articles/DateParser/NewsDateParser.cs)
- Materializer Date rewrite: [`NntpArticleCanonicalMaterializer.cs`](../../src/VectorNNTPD.Common/Articles/Processing/NntpArticleCanonicalMaterializer.cs)

### Three stages

| Stage | Mutates article? | What it does |
|---|---|---|
| **Validation / resolution** | No | Unfold a Date-family value and parse it to a UTC instant |
| **Selection / fallback** | No | Try candidate headers in a fixed order; skip present-but-unparseable values |
| **Canonicalization / materialization** | Yes | Replace the **winning header’s value** with formatted UTC |

The parser never rewrites Date. `NntpArticleParseResult` carries `CanonicalUtc`, `OriginalDateValue`, and `SelectedDateHeaderName`. `TryFormatCanonicalUtc` writes the canonical form into a caller buffer.

### Fallback order

`ArticleDateHeaderResolver.CandidateHeaderNames`:

1. `Date`
2. `Injection-Date`
3. `NNTP-Posting-Date`
4. `Posted`
5. `X-Date`
6. `Delivery-Date`

The parser rejects a second `Date` header as `NntpArticleParseFailureCode.DuplicateDate` before resolution. `Date` and `date` are the same header. A folded continuation is part of that one header, not a second `Date`. No canonical instant or record is published for that rejection.

For each remaining candidate name, headers are scanned in wire order. The first value of that name that unfolds and `NewsDateParser.TryGetCanonicalUtc` succeeds wins. A missing or unparsable single `Date` does not block a later name such as `Injection-Date`.

### CanonicalUtc

`CanonicalUtc` is that winning instant as `DateTime` (UTC). The factory copies `parse.CanonicalUtc` onto the record.

### Materialized Date text

`NewsDateParser.TryFormatCanonicalRfc5322Utc` writes ASCII:

```text
ddd, dd MMM yyyy HH:mm:ss +0000
```

Example from parser tests: `Fri, 23 Aug 2024 07:30:10 +0200` resolves to `Fri, 23 Aug 2024 05:30:10 +0000`. The materializer overwrites only the winning header’s **value** bytes (not the header name). If Injection-Date won, that value is rewritten; `Fields.Date` then points at that rewritten Injection-Date value.

### Leap seconds

RFC 5322 allows second `60` so a leap second can appear in the time-of-day (the valid range is `00:00:00` through `23:59:60`). .NET `DateTimeOffset` parsing rejects that second. The parser accepts a leap second only when the minutes field is `59` and the seconds field is exactly `60` (for example `Sat, 31 Dec 2016 23:59:60 +0000`, including obsolete two-digit-year forms the parser already supports). It rewrites that second to `00`, parses with the existing paths, converts the offset to UTC, then advances one minute so the canonical instant is the following minute’s second `00` (that example becomes `Sun, 01 Jan 2017 00:00:00 +0000`). Minute `60` (for example `23:60:00`) and second values greater than `60` (for example `12:34:61`) are not leap seconds and remain parse failures.

### Timezone abbreviations

The parser replaces one trailing ASCII letter token, length 2 through 8, with a fixed offset. It does not consult the machine timezone database. RFC 1036 §2.1.2 names `CST` as a North American news-date abbreviation. The local RFC text does not give its numeric offset. The `-06:00` mapping is the Issue #164 convention. `CDT` stays `-05:00`. China Standard Time is the distinct token `BJT` (`+08:00`). `PST` is Pacific `-08:00`; the Philippines are `PHT`. `BST` is British Summer Time `+01:00`, not Bangladesh. `IST` remains the existing India Standard Time mapping `+05:30`; Irish and Israel Standard Time share those letters and are not distinguished.

These collisions are omitted rather than given one region's offset: `AST`/`ADT`, `GST`, `NST`/`NDT`, `AMT`, `ECT`, `WST`, and `SST`. `MSD`, `BRST`, `WAST`, `IRDT`, `AZST`, and `AWDT` stay in the table as historical daylight-time conventions, not current year-round offsets. Civil offsets varied by place and period, so those static values do not reconstruct the original local civil time for every article date. `UTC+0` through `UTC+9` and `UTC-1` through `UTC-9` are trailing tokens matched before the alphabetic letter-run path and rewritten to `±0N:00`. `UTC-0` and multi-digit forms such as `UTC+10` are rejected. Numeric `±hhmm` and `±hh:mm` forms are a separate parse path. The table is a news-date convention, not a complete history of civil offsets.

### Failure

If no candidate parses: `NntpArticleParseFailureCode.MissingOrInvalidDate`. A second `Date` is `DuplicateDate` instead. The factory returns `RejectedParse` and does not materialize.

### Why source offsets cannot be reused

Date (and Path) value lengths can change. Every later header in ArtData may shift. Parser `NntpArticleHeaderEntry` offsets refer to the **destuffed input**. After materialize they are invalid. `ArticleFieldTable.Locate` runs on the **final** ArtData (`ArticleRecordTests.MaterializedRanges_AreNotSourceOffsets`).

### Effect on other fields

| Field | When Date text length changes |
|---|---|
| ArtId | Unchanged (Message-ID value unchanged) |
| ArtSize / ArtHash | Change (ArtData bytes changed) |
| Field ranges | Relocated on the new buffer |
| ArtType / ArtLines | Unchanged (body and type markers unchanged) |

---

## 9. Validation

Validation is `NntpArticleParser.Parse` on destuffed bytes. It is analysis only: slices alias the caller buffer; the buffer is not rewritten.

### Resource limits

[`ArticleResourceLimits.cs`](../../src/VectorNNTPD.Common/Articles/ArticleResourceLimits.cs) and `NntpArticleParserOptions.Default`:

| Limit | Default |
|---|---|
| Max article bytes | 5 MiB (`ArticleResourceLimits.MaxArticleBytes`); options are capped to this |
| Max header section | 256 KiB |
| Max header count | 1024 |
| Max physical line | 1024 bytes |
| Max header name | 128 bytes |
| Max header value | 64 KiB |
| yEnc detection window | 64 KiB of body |
| Newsgroups value | 4096 (`NntpArticleParser.MaxNewsgroupsLength`) |
| From unfold buffer | 2048 (`MaxFromLength`) |
| Message-ID | 3–250 octets (`NntpMessageIdValidation`) |
| Path value | 8192 (`ArticlePathCanonicalizer.MaxPathLength`) |

Empty input → `EmptyArticle`. Oversize article → `ArticleTooLarge`.

### Header syntax and terminators

Headers are split on CR, LF, or CRLF. A zero-length line is the header/body separator. Continuation lines start with SP or HTAB and extend the previous value (folded bytes are kept in the value range). Illegal header bytes: NUL or other controls except TAB → `ContainsIllegalControlByte`. Missing colon / empty name / continuation without a header → `MalformedHeader` / `MalformedHeaderContinuation` / `MissingHeaderBodySeparator`.

### Required vs optional

| Header | Rule |
|---|---|
| Message-ID | Required, not duplicate, unfold + `IsValidMessageId` (no strip in this path) |
| Newsgroups | Required, not duplicate, length 1…4096. A CRLF followed by SP or HTAB unfolds to one SP before comma-token checks. Bare CR, bare LF, and CRLF without that whitespace are `InvalidNewsgroups`. SP and HTAB are allowed before the first name, after the last name, and immediately beside a comma. Whitespace inside a name is `InvalidNewsgroups`. |
| Date-family | At least one resolvable candidate (see §8) |
| Path | At most one; may be absent; invalid tokens/controls/length → `InvalidPath` |
| From | Optional; if present, unfold, must contain `@` not at ends, printable ASCII, non-empty |
| Subject | Optional; identified only |
| References | Optional; identified only; **duplicates are accepted** |
| Content-Type / CTE | Optional; used for coarse `NntpArticleType` and Diablo `ArtType` |

### yEnc

If a body line in the detection window starts with `=ybegin `, `YEncArticleValidator.Validate` runs on the destuffed body. Failure → `YEncDecodingFailed`. Success does **not** replace the body with decoded payload. ArtData keeps encoded yEnc, with dot-leading body lines stuffed.

### Classification during parse

Parse also sets `NntpArticleParseResult.ArticleType` (`NntpArticleType`). Factory ArtType is a **second**, Diablo-flag classification (see §6). Both run only after structural validation succeeds (or yEnc fail still reports `NntpArticleType.YEnc` on the rejected result).

### Validation vs canonicalization

| | Validation (parser) | Canonicalization (materializer) |
|---|---|---|
| Input | Destuffed article | Accepted `NntpArticleParseResult` |
| Mutates bytes | No | Yes: winning Date value, Path value or Path insert, and one stuffing dot on each dot-leading body line |
| Output | Slices + metadata | One new exact-size stuffed `byte[]` |

If parse fails, the factory never materializes.

---

## 10. Path validation and Path manipulation

Implementation: [`ArticlePathCanonicalizer.cs`](../../src/VectorNNTPD.Common/Articles/Parsing/ArticlePathCanonicalizer.cs).  
Materializer applies `TryWriteCanonicalPath`.  
Tests: [`ArticlePathCanonicalizerTests.cs`](../../tests/VectorNNTPD.Common.Tests/Articles/Parsing/ArticlePathCanonicalizerTests.cs), materializer Path tests, `ArticleRecordTests` Path range.

### Validation vs canonicalization

`TryAnalyze` classifies; it does not write Path. `TryWriteCanonicalPath` writes the would-be Path value into a caller span. The parser calls only `TryAnalyze`. The materializer writes the result into ArtData.

### Token rules

- A CRLF followed by SP or HTAB is unfolded before validation only when that fold is in the whitespace immediately before a `!` delimiter. A fold after `!`, a fold inside a hop, a fold after the final hop, bare CR, bare LF, and CRLF without continuation whitespace are `InvalidPath`
- Split on `!`
- Trim SP/HTAB on each token
- One empty token between two hops is RFC 5536 `diag-match` (`!!`) and is kept when both hops are written. If tracker deduplication removes the right-hand hop, the marker is dropped with that hop. A second empty token in that position (`a!!!b`) is `InvalidPath`
- A leading `!` before the first hop is `InvalidPath`. A trailing `!` is omitted. A value that is only separators, such as `!!!`, stays `Empty`
- Tokens must be printable ASCII, not space, not `!` (`IsValidPathComponent`)
- The unfolded Path must be printable 0x20–0x7E. The raw value must be ≤ 8192
- Tracker and local FQDN matching is **case-insensitive complete-token** equality (`AsciiEqualsIgnoreCase`), not substring search

Organizational tracker: `news.usenet.ninja` (`OrganizationalTrackerHost`). After any Vector application has seen the article, that token is present **exactly once**.

Application hop: the parser’s local FQDN (constructor argument). First Vector sighting (tracker absent) writes:

```text
news.usenet.ninja!{application-fqdn}!existing-hops
```

Later sighting (tracker already a token) writes:

```text
{application-fqdn}!existing-hops
```

If the local identity token is already present (`ArticlePathKind.AlreadyContainsLocalIdentity`), the local FQDN is **not** prepended again. If the tracker is already present, it is **not** prepended again.

Missing or empty Path: analyze succeeds (`Missing` / `Empty`); write uses tracker + local FQDN only (no existing hops). The materializer **inserts** a `Path: ` line when the header was absent.

### Parser does not mutate Path

`NntpArticleParseResult.TryWriteCanonicalPath` is a preview write into a caller span. Article bytes stay as received until `NntpArticleCanonicalMaterializer.Materialize`.

### Materializer failures related to Path/Date

`NntpArticleCanonicalFailureCode`: `DuplicatePath`, `InvalidHeaderSeparator`, `ArticleTooLarge`, `DateLineTooLong`, `PathRewriteLineTooLong`, `PathInsertionLineTooLong`, `WriteMismatch`. Line limits are `ArticleResourceLimits.MaxArticleLineBytes` (1024).

### Effect of Path rewrite

| Member | Changes? |
|---|---|
| ArtData, ArtSize, ArtHash | Yes |
| `Fields.Path` and later header offsets | Yes — relocate on final ArtData |
| ArtId | **No** (Message-ID unchanged) |
| ArtType, ArtLines, CanonicalUtc | No (body / type / resolved instant unchanged) |

Reclassification is unnecessary: type markers are not in the Path value.

There is **no** `ArticleRecord` method that applies a later hop. Subsequent-hop text is what `TryWriteCanonicalPath` would produce if invoked with a new local identity and `containsOrganizationalTracker: true`. Building a new record for that hop is not implemented.

---

## 11. ArtData and ownership / lifetime

ArtData is the canonical retained article: unstuffed headers, one blank line, and a body whose dot-leading lines already contain one stuffing dot. It is not the NNTP status line, not the terminating `.CRLF`, and not decoded yEnc or rebuilt MIME. Header lines that begin with `.` are stuffed only by `VectorNNTPD.BackFillerService.ArticleWork.ArticleWorkDeliveryFraming`, in a separate socket image. The listener appends one `.\r\n` after that image. Callers may retain the `ArtData` array. The factory does not mutate it after the record is returned.

`ArticleRecord` is a `readonly struct`. It holds a `byte[]` reference and exposes it as `ReadOnlyMemory<byte>`. Copying the struct copies the reference; it does **not** copy the article. The type system does not enforce unique ownership.

Lifetime is the producer/consumer workflow: the factory receives destuffed memory, materializer allocates one exact-size canonical array, the record references that array, destuffed input is not retained. Callers must keep the record (or the buffer) alive as long as they use the spans.

Defensive article-sized copies are not performed. `ArticleRecordCreateResult.Accepted` does not clone ArtData.

---

## 12. ArticleFieldTable

[`ArticleFieldTable.cs`](../../src/VectorNNTPD.Common/Articles/ArticleFieldTable.cs), [`ArticleByteRange.cs`](../../src/VectorNNTPD.Common/Articles/ArticleByteRange.cs).

```csharp
public readonly struct ArticleByteRange
{
    public static ArticleByteRange Absent { get; } // Offset = -1, Length = 0
    public int Offset { get; }
    public int Length { get; }
    public bool IsPresent => Offset >= 0;
    public ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> artData);
}

public readonly struct ArticleFieldTable
{
    public ArticleByteRange MessageId { get; }
    public ArticleByteRange Newsgroups { get; }
    public ArticleByteRange Subject { get; }
    public ArticleByteRange From { get; }
    public ArticleByteRange Date { get; }        // winning Date-family value
    public ArticleByteRange References { get; }
    public ArticleByteRange Path { get; }
}
```

`Locate(artData, selectedDateHeaderName)` walks canonical headers (same CR/LF/CRLF and folding rules as the parser) and records the **first** occurrence of each name. `Date` is the header whose known name equals the parser’s winning `SelectedDateHeaderName`.

Ranges include folded continuation bytes as stored. Absent fields use `ArticleByteRange.Absent`. Newsgroups is that value range (ArtGroups). Membership checks tokenize `,` and skip SP, HTAB, CR, and LF around each token; the record does not allocate `List<string>` (`TryCreate_DoesNotCreateGroupStringCollection`).

Parser offsets into destuffed source must not be kept after Date/Path rewrite.

---

## 13. ParseStatus / CanonicalV1

[`ArticleParseStatus.cs`](../../src/VectorNNTPD.Common/Articles/ArticleParseStatus.cs):

| Value | Meaning |
|---|---|
| `None` (0) | Record has not completed the canonical pipeline (including factory rejection / default struct) |
| `CanonicalV1` (1) | This record’s pipeline completed |

`CanonicalV1` guarantees **about the record**:

- ArtData is destuffed canonical article bytes (Date/Path rewritten)
- Common parser validation succeeded
- Diablo `ArticleType` classification was performed
- ArtId is BLAKE3 of the Message-ID **value** in that ArtData
- ArtHash is XXH3-64 of that ArtData
- FieldTable ranges refer to that ArtData
- `CanonicalUtc` / `ArtLines` / `ArtSize` match that construction

`CanonicalV1` does **not** mean an IHAVE/ARTICLE **request** Message-ID was compared. The factory has no request parameter. Request matching is ingestion/orchestration. Common already has `NntpArticleIdentity.MatchesRequest` for that comparison; it is not invoked by the factory and is not part of `ParseStatus`.

Versioning: one extra byte (`CanonicalV2`, …) if an incompatible construction contract appears. There is no separate ParserVersion / ValidationVersion.

---

## 14. ArticleRecordFactory

[`ArticleRecordFactory.cs`](../../src/VectorNNTPD.Common/Articles/ArticleRecordFactory.cs).

The factory does **not** destuff. Callers must pass transport-normalized, destuffed article bytes (same contract as `NntpArticleParser`).

Actual `TryCreate` sequence:

```text
destuffed article (caller)
    → NntpArticleParser.Parse          // validate; no mutation
    → NntpArticleCanonicalMaterializer.Materialize
         // one new byte[]: rewrite winning Date value + Path
    → ArticleFieldTable.Locate(canonical, SelectedDateHeaderName)
    → ArticleId.FromMessageId(Message-ID value in ArtData)
    → XxHash3.HashToUInt64(ArtData)
    → ArticleTypeClassifier.Classify(destuffed header/body slices)
    → ArticleRecord(CanonicalV1, ArtLines = parse.BodyLineCount, CanonicalUtc = parse.CanonicalUtc)
```

On parse failure: `ArticleRecordCreateResult.RejectedParse(failure)` — default record, `ParseStatus.None`.  
On materialize failure: `RejectedMaterialize(failure)` — destuffed not retained as ArtData.

Allocation (tests):

- Representative `Parse` after warmup: **0** bytes (`HotPathAllocationTests`)
- `TryCreate` after warmup: one article-sized allocation, `ArtSize ≤ allocated < 2 × ArtSize` (`TryCreate_AllocatesOneArticleSizedBuffer`)
- Materializer: one `new byte[destinationLength]`; no second article-sized copy
- ArtId: no heap for the 32-byte value

`ArticleType` is classified from destuffed parse slices after materialize. Body and type-bearing headers are not rewritten, so flags apply to ArtData.

---

## 15. Performance model

Intended hot-path properties supported by the implementation and tests:

- Parse destuffed articles once; `CanonicalV1` consumers should not re-parse for MID, groups, type, size, lines, or overview ranges
- Protocol data stays bytes (`ReadOnlySpan<byte>` / `ReadOnlyMemory<byte>`); no `GetString` on the factory path
- Header access is integer ranges, not copied strings or `List<string>` groups
- One final article-sized canonical buffer; destuffed input is dropped
- Fixed-size `ArticleId` (32 bytes inline)
- XXH3-64 once over final ArtData
- Parser walk after warmup does not allocate; factory cost is dominated by the materialize `byte[]`

No throughput or latency benchmarks are claimed here.

---

## 16. Example lifecycle

Consistent with Common parser/materializer/ArticleRecord tests (`LocalFqdn = backfiller01.usenet.ninja`).

### Incoming destuffed article

```text
Path: peer.example
Date: Fri, 23 Aug 2024 07:30:10 +0200
Message-ID: <shift@example.test>
Newsgroups: alt.test,alt.binaries.test
From: user@example.test
Subject: after-path
References: <prev@example.test>

line1
line2
```

### Validation (no mutation)

Parser accepts: Message-ID, Newsgroups, From, Date (`+0200` → UTC `2024-08-23 05:30:10`), Path (`NeedsLocalIdentityPrepend`, tracker absent). `BodyLineCount = 2`. Coarse `NntpArticleType` is `Text`.

### Canonicalization

Date value becomes `Fri, 23 Aug 2024 05:30:10 +0000`.  
Path becomes `news.usenet.ninja!backfiller01.usenet.ninja!peer.example`.

### ArticleRecord (CanonicalV1)

| Member | Value |
|---|---|
| ArtId | BLAKE3(`<shift@example.test>`) |
| ArtHash | XXH3-64 of the **new** ArtData |
| ArtSize | Length of rewritten article (≠ destuffed length) |
| ArtType | `Default` |
| ArtLines | 2 |
| CanonicalUtc | 2024-08-23 05:30:10 UTC |
| Message-ID / Newsgroups / Subject / From / References | Unchanged values; possibly new offsets |
| Path | `news.usenet.ninja!backfiller01.usenet.ninja!peer.example` |
| Date range | Rewritten UTC value of the winning `Date` header |

### Later Path hop (rules only; no factory API)

If another application (`storage01.usenet.ninja`) analyzed the **canonical** Path, tracker is already a token. `TryWriteCanonicalPath` would produce:

```text
storage01.usenet.ninja!news.usenet.ninja!backfiller01.usenet.ninja!peer.example
```

A rematerialized buffer would change ArtData, ArtSize, ArtHash, and Path/later ranges. ArtId, ArtType, ArtLines, CanonicalUtc, and Message-ID value would stay the same. `ArticleRecordFactory` does not perform this second hop today.

---

## 17. Current scope / future integration

ArticleRecord v1 is a Common model plus `ArticleRecordFactory`. That factory is the only canonical materializer. BackFiller parse, validation, retention, and the listener all use its stuffed `ArtData`. There is no second BackFiller materializer. `NntpArticleIdentity.MatchesRequest` compares the retained record with the Article Work Message-ID.

This repository does not persist article bodies or `ArtHash` to disk. Retention is an in-memory index in `ArticleRetentionAuthority`. Overview `:bytes` is `ArtSize` of the retained buffer when an application encodes an overview. VATP META stores `record.ArtHash`, and the receiver checks XXH3-64 of the ArtData bytes it actually received. It does not destuff or restuff before that check, and it does not accept a second hash of a different representation. A peer that still transfers unstuffed ArtData will pass that check when its META hash matches those unstuffed bytes. A later delivery that assumes the body is already stuffed would then stuff it again. This repository has no second materializer and no version field that distinguishes the two representations.

It is **not** used by:
- NNTPD IHAVE now constructs CanonicalV1 `ArticleRecord` before queue admission (same factory as TAKETHIS/POST). `IhaveArticleInterpreter.DestuffToArticle` remains a bench/classifier helper only.
- RabbitMQ Article Work JSON / cache URIs
- Retention, storage, XOVER/XHDR
- A serialized envelope of the full ArticleRecord / ArtData
- Transit `MessageTypes` policy matching

NNTPD ingestion encodes the Common OverviewDB protobuf subset (`OverviewArticleV1`) from CanonicalV1 field ranges and publishes those bytes on `overviewdb.queue`. That handoff does not serialize ArtData. RabbitMQ transport stays in NNTPD.

### Future scope (not implemented)

- Request Message-ID match as an orchestration step **after** or **beside** factory construction
- Path prepend that produces a new record without a full reparse
- Serialized envelope of the full record (scalars + ArtData + range table + ArtHash check)
- Completing Diablo `classifyLineAsTypes` body confirmation

---

## Implementation and test index

| Topic | Source | Tests |
|---|---|---|
| Record / factory | `Articles/ArticleRecord.cs`, `ArticleRecordFactory.cs` | `Articles/ArticleRecordTests.cs` |
| ArtId | `Articles/ArticleId.cs` | `Articles/ArticleIdTests.cs`, NNTPD `History/HistoryDbTests.cs` |
| OverviewDB protobuf | `Articles/OverviewDb/OverviewArticleV1.proto`, `OverviewArticleV1.cs`, `OverviewArticleV1Codec.cs` | `Articles/OverviewDb/OverviewArticleV1CodecTests.cs` |
| ArtHash | `System.IO.Hashing.XxHash3` via factory | `Articles/ArticleRecordArtHashTests.cs` |
| ArtType | `Articles/ArticleType.cs`, `ArticleTypeClassifier.cs` | `Articles/ArticleTypeClassifierTests.cs`, NNTPD classifier/IHAVE tests |
| Parse / lines / References | `Articles/Parsing/NntpArticleParser.cs`, `NntpArticleParserContracts.cs` | `Articles/Parsing/NntpArticleParserTests.cs` |
| Date | `Articles/DateParser/*` | `Articles/DateParser/NewsDateParserTests.cs`, parser Date cases |
| Path | `Articles/Parsing/ArticlePathCanonicalizer.cs` | `Articles/Parsing/ArticlePathCanonicalizerTests.cs` |
| Materialize | `Articles/Processing/NntpArticleCanonicalMaterializer.cs` | `Articles/Processing/NntpArticleCanonicalMaterializerTests.cs` |
| yEnc | `Articles/YEnc/YEncArticleValidator.cs` | `Articles/YEnc/YEncArticleValidatorTests.cs` |
| Parse allocations | — | `Articles/HotPathAllocationTests.cs` |
| Request MID compare (not factory) | `Articles/Processing/NntpArticleIdentity.cs` | `Articles/Processing/NntpArticleIdentityTests.cs` |
