# Deeper offline MAGIX project findings

The saved project contains readable, indexed text records as well as binary timeline and effect data. The reader now decodes a specific Unicode table layout and reports exact links between those records and repeated legacy strings. This strengthens project-data access, but it does not yet validate writing a project that MAGIX will accept.

All research was offline and read-only. It covered the default autosave, the cached original project identified by an autosave string, and six installed demo/template projects. No MAGIX instance, UI automation, media file, or stored network path was opened.

## Independent samples

| Snapshot | Bytes | Unicode records decoded | `sfnm` ID links | Legacy text equals Unicode text |
|---|---:|---:|---:|---:|
| `_Demo.MVP` | 1,292,376 | 208 | 154 | 154 |
| `_Demo_3D.MVP` | 369,496 | 100 | 42 | 42 |
| `_Studio-Preview.MVD` | 94,674 | 34 | 6 | 6 |
| `_TV-Anti-Cropping.MVD` | 89,818 | 24 | 9 | 9 |
| `_TV-Anti-Cropping_HD.MVD` | 93,308 | 24 | 9 | 9 |
| `_DiveIn_wide_alt.MVD` | 591,240 | 0 | 0 | 0 |
| `Autosave.MV_` | 665,684 | 17 | 3 | 3 |
| `redacted sample test .MVP` | 6,387,360 | 276 | 345 | 345 |

The legacy `_DiveIn_wide_alt.MVD` has no trailing `RIFF/ustr` container. Its unresolved references are not evidence that it has no assets.

Source paths, exact SHA-256 hashes, timestamps, and full decoded strings are kept in the ignored reports `.build/research/deeper-format-reports.json` and `.build/research/original-project-deeper-report.json`. The first report records that the original project exceeded the old 20,000-chunk limit; the separate original-project report completes that inspection with a 50,000-chunk bound.

## Unicode table layout established by the bytes

In seven snapshots, a second top-level `RIFF/ustr` container follows the main `RIFF/SEKD` container. Its recognized nested layout is:

```text
RIFF form ustr
  LIST form ustl
    LIST form 0x00000000
      repeat until this LIST payload ends:
        uint32 little-endian record ID
        uint32 little-endian text byte length
        exactly that many UTF-16LE bytes
```

All 683 records in these samples consume their respective table payloads exactly, with no leftover bytes. IDs are sparse identifiers, not array positions. The original project has 276 records but includes an ID of 1089. The counted text has no implied trailing NUL; a two-byte text payload is one UTF-16 code unit, not two characters.

The implementation decodes strict UTF-16LE, including surrogate pairs, and checks record lengths, duplicate IDs, record count, and exact container bounds. If any record is malformed, the complete table is reported as undecoded instead of exposing a trusted partial mapping. Real samples in this set contain ASCII-valued text; non-ASCII and supplementary characters are covered by synthetic byte tests, not by an independently edited MAGIX project.

## What the IDs appear to reference

All 568 resolved `sfnm` payloads in the table-bearing samples have this observed layout:

```text
legacy byte string
one NUL byte
uint64 little-endian value matching a unique Unicode table ID
```

In every one of those 568 cases, the complete ASCII legacy string equals the UTF-16LE string stored under that ID. The reader reports the legacy byte offset, ID offset, Unicode record offset, and equality result. This is strong byte-level evidence of a repeated string plus an index. It does not establish how MAGIX handles edits, missing IDs, media relocation, or every older format variant.

The `SVIP` and `file` chunks also contain exact ASCII-text + NUL + uint64-ID sequences matching Unicode records. Eleven observed name-like `SVIP` strings begin at payload offset 1292. One is `BlurWipe_07.ufx`, so treating every `SVIP` container as a top-level movie would be incorrect. The reader reports the exact matches and locations rather than assigning movie identities.

## Original project inventory

The cached original project contains 27,172 parsed chunks, 345 `sfnm` chunks, 347 `LIST:objs` containers, 157 `LIST:trck` containers, and four `SVIP` chunks. These are tag counts, not validated visible clip or track counts.

The existing full-string inspection produces **103 distinct candidate asset references** for that original project. This is a substantially more useful source for whiteboard asset cards than the two assets found in the autosave. The paths are stored values; their existence, current use, and availability have not been checked. The original project may also contain nested effect/project structures, so these candidates should not be presented as 103 visible scenes.

## Reader changes and compatibility

`tools/inspect_magix_project.py` retains its prior reference and whiteboard fields and adds:

- `experimentalUnicodeTables`: exact length-delimited records, offsets, and decode status.
- `experimentalStringLinks`: resolved `sfnm` trailing IDs and exact duplicate text-and-ID matches in selected chunks.
- `inspectionLimits`: the limits used for that report.

Existing whiteboard candidates are still derived from complete directly observed strings. A Unicode-table link does not silently replace a candidate's stored reference. Duplicate IDs across separate tables are left ambiguous and are not resolved.

The chunk limit is now 50,000 so the actual 27,172-chunk project can be inspected. The 64 MiB file bound, 32-level nesting bound, string/reference bounds, changed-source checks, exclusive report creation, and read-only source access remain in place. Unicode decoding adds a 5,000-record bound and a 65,536-byte per-string bound. Link matching scans bounded payloads rather than comparing every possible record with every chunk.

Fifteen offline tests pass, including the earlier read-only and malformed-container checks plus counted Unicode text, noncontiguous IDs, surrogate pairs, exact duplicate matches, mismatched legacy/Unicode text, ambiguous IDs, and rejection of partial malformed tables.

## What this means for modifying MAGIX projects

The immediate useful connection is an offline project inventory that supplies whiteboard cards with source-hash and byte-offset provenance. That does not require changing the project.

A future project writer must account for repeated text, the Unicode table, ID references, RIFF lengths and padding, and any further representations of a media reference. Updating only one visible path string could leave the project inconsistent. `sfnm` strings are only one location where related information appears; `file` records also contain names and directory data.

Before offering a modification, a controlled disposable-copy experiment would need to establish exactly which fields MAGIX changes for that operation, then verify that MAGIX opens and saves the rewritten copy correctly. The current user restriction excludes that UI validation. A binary file that still parses as RIFF is not proof of a correct edit.

Numeric timing, track indexes, clip trims, keyframes, effect parameters, and embedded-object semantics remain undecoded. No project-writing function was added, and no project was modified.
