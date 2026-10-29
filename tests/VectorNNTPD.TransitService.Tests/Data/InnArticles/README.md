# INN article test corpus (byte-exact fixtures)

## Provenance

| Field | Value |
|-------|-------|
| Repository | [InterNetNews/inn](https://github.com/InterNetNews/inn) |
| Source path | `tests/data/articles` |
| Upstream ref | `main` |
| Upstream commit | `6843efceb8a15774d23e4e2892d08daf54574314` |
| Retrieval date (UTC) | 2026-09-22 |
| Upstream commit date (UTC) | 2026-09-11 |

Fixtures were imported from the GitHub archive of that exact commit:

`https://github.com/InterNetNews/inn/archive/6843efceb8a15774d23e4e2892d08daf54574314.zip`

Paths under `inn-6843efceb8a15774d23e4e2892d08daf54574314/tests/data/articles/` were copied **byte-for-byte**. No line-ending conversion, text decoding, or content normalization was applied.

Machine-readable per-file hashes and byte characteristics:

- `inn-articles.manifest.json` (same directory)

## License / attribution

INN as a whole (and unmarked code/data in the repository) is covered by the ISC-style license in the upstream `LICENSE` file:

> Copyright (c) 2004-2026 by Internet Systems Consortium, Inc. ("ISC")  
> Copyright (c) 1991, 1994-2003 by The Internet Software Consortium and Rich Salz  
>  
> Permission to use, copy, modify, and distribute this software for any purpose with or without fee is hereby granted, provided that the above copyright notice and this permission notice appear in all copies.  
>  
> THE SOFTWARE IS PROVIDED "AS IS" AND ISC DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL ISC BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.

Full upstream license text: <https://github.com/InterNetNews/inn/blob/6843efceb8a15774d23e4e2892d08daf54574314/LICENSE>

No separate license/notice file was present inside `tests/data/articles` itself at this commit.

## Framing note (VectorNNTP TAKETHIS experiment)

These fixtures are INN parser/spool test inputs. Most numeric and `bad-*` files are **LF-only spool articles** and do **not** include an NNTP multiline terminator (`.\r\n` / `\r\n.\r\n`).

The `wire-*` files are wire-oriented. At this commit, only `wire-7` contains the five-byte `\r\n.\r\n` delimiter and ends with a `.\r\n` terminator suitable for a complete multiline framing benchmark.

Do **not** treat filename categories as NNTP validity by themselves; use the manifest byte characteristics and framing classification.
