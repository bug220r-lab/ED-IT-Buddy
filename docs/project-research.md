# Read only MAGIX project inspection

The offline reader now has evidence from both the initial MAGIX autosave and a subsequently located original `.MVP` snapshot. The latter yielded 103 candidate media paths and a recognized Unicode string table. These can support source-asset cards with provenance; they do not reconstruct MAGIX's timeline or establish that every stored reference is currently used.

## Original MVP: subsequent inspection

The original `redacted sample test .MVP` was located in the cached project-library material. It is a saved-file snapshot, not evidence of what is currently open in MAGIX. Its full private source path is retained in `.build/research/original-project-deeper-report.json`.

| Evidence | Observed value |
|---|---|
| File size | 6,387,360 bytes |
| Last write | October 23, 2026, 22:40:58.957720 UTC |
| SHA-256 | `e35401797e782602860d2f03da15fcf619b6907fff773b53a6c15dc733a62ea0` |
| Parsed chunks | 27,172 |
| Distinct stored reference strings | 223 |
| Candidate assets from bounded `sfnm` strings under `LIST:objs` | 103 |
| Recognized Unicode table | 276 records |
| Experimental `sfnm` trailing-ID links | 345 |
| Exact duplicate-text-and-ID matches | 941 |

The last two counts are evidence occurrences, not distinct assets, clips or tracks. The Unicode table and link observations are described below. The source project was read without modification. Referenced media was neither opened for playback nor checked for existence.

## Initial autosave proof: historical snapshot

The first proof used `Autosave.MV_` from the default MAGIX 2021 Plus Steam documents folder, before the original `.MVP` was located. An autosave can be incomplete or differ from the intended saved project. These results remain valid for that specific snapshot and should not be confused with the larger original-project report above.

| Evidence | Observed value |
|---|---|
| File size | 665,684 bytes |
| Last write | October 29, 2026, 22:01:43 UTC |
| SHA-256 | `791a8160141818b94f4b4a13383758a78678c553177b7040ec88219cb4ec03bc` |
| First container | `RIFF/SEKD`, byte 0, payload length 663,750 |
| Second container | `RIFF/ustr`, byte 663,758, payload length 1,918 |
| Parsed chunks | 2,484 |
| Distinct stored reference strings | 9 |
| Candidate assets from `sfnm` strings inside `LIST:objs` | 2 |

The source's size, identity, and modification time were unchanged across the bounded read. The source was opened only for reading. No media paths from the snapshot were opened or checked.

## Autosave strings that can support asset cards

| Candidate asset | String byte offsets | Basis |
|---|---|---|
| `redacted sample test.mp4` | 30,344 and 34,990 | Two exact stored full-path strings in `sfnm` chunks under `LIST:objs` |
| `[REDACTED] 4.28.26.mp3` | 144,804 | One exact stored full-path string in an `sfnm` chunk under `LIST:objs` |

The report retains the complete stored paths privately under `.build/research/`. Some refer to an older Windows user directory. Their presence does not prove that the files still exist at those paths.

The autosave chunk tree also contains two `LIST:VIPL`, 78 `LIST:trck`, and three `LIST:objs` containers. Those are verified byte-level tag counts. Their mapping to current movies, track numbers, or visible clip counts has not been validated. The reader does not decode numeric timing, trims, transitions, or effect values in either snapshot.

## Experimental Unicode tables and string links

The reader recognizes the observed `RIFF:ustr` → `LIST:ustl` → binary-form `LIST` layout. Records contain a 32-bit ID, a 32-bit byte length and strictly decoded UTF-16LE text. IDs need not be contiguous. Declared bounds, even byte lengths, valid Unicode and uniqueness within a table are checked. A malformed or unfamiliar table stays undecoded; partially read records are not exposed as trusted results.

`experimentalStringLinks.sfnmIdLinks` reports an observed trailing 64-bit value after the legacy NUL-terminated string when it matches exactly one decoded table ID. `duplicateTextAndIdMatches` records exact duplicate ASCII text, NUL and ID sequences in selected chunk types. Ambiguous IDs across tables are not resolved. These observations do not establish that rewriting either copy is valid, complete or sufficient for MAGIX to accept a project.

Unicode text is additional evidence. It does not silently replace a legacy path in `whiteboardCandidates`, and an ID match does not verify current timeline use. The reader remains read-only; no project-writing or repair mechanism is implemented.

## Reusable reader

`tools/inspect_magix_project.py` uses only the Python standard library. It requires Python 3.10 or later. Run it against an explicitly chosen saved snapshot:

```powershell
python tools/inspect_magix_project.py "C:\path\Project.MVP" --output ".build\research\project-report-new.json"
```

On this PC, the successful test used the Python runtime bundled with Codex, which is not on the normal shell path:

```powershell
& 'C:\Users\Jude\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' -B tools/inspect_magix_project.py 'C:\path\Project.MVP' --output '.build\research\project-report-new.json'
```

Omit `--output` to print JSON. Output paths must end in `.json`; existing files and the input file are refused. The initial proof report is `.build/research/autosave-project-report-final.json`; the subsequent report is `.build/research/original-project-deeper-report.json`. Both contain private filesystem references. Older reports may reflect the reader version at the time they were created; the current reader includes the Unicode extensions and explicit inspection limits.

The current default limits are 64 MiB input, 50,000 chunks, nesting depth 32, 2,000 printable-reference evidence occurrences, 65,536 bytes per string and 5,000 Unicode records. Duplicate-text link evidence is also bounded. Declared child sizes cannot escape their parent. The reader refuses a source whose observed size, identity, or modification time changes during reading. Unknown binary-form `LIST` payloads remain undecoded; only the recognized Unicode layout is interpreted further.

`storedReferences` includes the exact printable candidate, byte offset, encoding and containing chunk path. A general candidate may be a fragment of a non-ASCII filename. File-extension classification is a heuristic. `whiteboardCandidates` requires complete string start and end boundaries within one `sfnm` payload under `objs`, deduplicates identical stored paths, and leaves `track`, `start`, and `duration` null. `currentTimelineUseVerified` remains false.

General string discovery covers printable ASCII and ASCII-compatible UTF-16LE; recognized length-delimited Unicode tables additionally support non-ASCII text. Other layouts and filenames may still be missed, and the candidate-asset pipeline remains based on directly observed bounded strings. This is an evidence-producing inspection spike, not a complete parser for every MAGIX project version.

## Validation

The current suite has 15 offline tests in `tests/test_project_inspector.py`; the latest verification passed all 15. Coverage includes exact byte evidence, cross-chunk and filename-fragment rejection, concatenated containers, opaque unknown tables, unsupported/truncated input, parent bounds, resource limits, changed snapshot metadata, no source mutation, output overwrite refusal, counted Unicode text with noncontiguous IDs, ambiguous-ID rejection, exact duplicate matches, and preservation of legacy candidates when experimental Unicode links differ.

```powershell
python -m unittest discover -s tests -p test_project_inspector.py -v
```

The next useful integration is to let the user import these candidate asset cards with their snapshot hash and stored-path provenance. Editing, playback, and opening media remain separate explicit operations. Timeline decoding would need further samples and independent validation before exposing track or timing fields as facts. The broader [integration assessment](integration-feasibility.md) and [private Qt interface research](qt-interface-research.md) describe separate live-host possibilities; neither changes the saved-snapshot limits here.
