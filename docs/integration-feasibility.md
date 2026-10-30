# ED-IT Buddy: whiteboard and MAGIX connection

Investigation date: 2026-10-29. Target: Movie Edit Pro 2021 Plus Steam Edition, Steam app 1363250, executable 20.0.1.80.

The product direction is a whiteboard for scene ideas, reference materials, tasks, and progress. Arranging and editing the video remain in MAGIX. The board can have a full planning view and a compact view of the selected scene while editing. Both an embedded presentation and access to project information are being explored.

The latest [modification research summary](magix-modification-research.md) combines the exact-install binary analysis, private Qt interface findings, original-project inspection and documented extension routes.

## What the investigation establishes

| Capability | Evidence and current limit |
| --- | --- |
| Freeform board with scene, reference, and task cards | Can be implemented in Buddy independently. The new interactive design preview explores it; the existing 0.1.0 executable still provides review notes, not a whiteboard. |
| Extract candidate metadata from a saved MAGIX project | The working offline reader inspected the original 6,387,360-byte MVP: 27,172 chunks and 103 distinct candidate asset references. Across eight snapshots it decoded 683 Unicode records and resolved 568 matching filename-ID links. These are stored references, not verified current timeline assets or live editor state. |
| Understand every track, clip, cut, and effect | Not established. Readable strings do not decode timeline semantics. Track and timing fields require controlled comparison of known project edits before they can be trusted. |
| Put ordinary Buddy controls in MAGIX's Effects area | A plausible experiment: this installation includes an OFX bridge, real OFX binaries, and internal plug-in UI placeholders. Ordinary parameters and a button opening Buddy still need a test effect. |
| Use standard OFX for a drawing canvas | The wrapper explicitly initializes custom interaction and preview overlays to 0. This should not be the assumed canvas route. Live capability values remain unqueried. |
| Change an existing private Qt interface | The resource contains 465 readable QML files, including pages linked to native preview and selected-object context. A valid isolated resource-loading path and successful substitution have not been established. |
| Add an independent dockable whiteboard pane | Not established. MAGIX's internal docking configuration lists its existing panes; no public third-party pane-registration contract was found. |
| Make Buddy's own window sit beside or visually attach to MAGIX | A separate Windows integration experiment. Presentation alone would grant no project or timeline access. It has not been tested. |
| Read or navigate an effect's time | The installed wrapper returns a version-1 OFX timeline suite with forwarding callbacks for current time, seeking, and bounds. Their downstream behavior remains untested. It is not an API for enumerating all clips. |

## How the board should use project information

Keep three kinds of information distinct:

- **Your plan:** scene cards, composition ideas, references, links between cards, and intended work.
- **Your progress:** task checkboxes and a manually selected state such as Idea, In progress, Review, or Done.
- **Saved-project evidence:** a dated, fingerprinted snapshot of fields that the reader actually understands. Stored media references are not proof that the media exists, is on the current timeline, or has been used correctly.

A person links a board card to a media reference or confirmed timeline location. Refreshing a project snapshot can flag changes for review; it must not silently relink cards or mark creative tasks complete. Unknown clip positions stay unknown. Old saved paths are retained as evidence, not automatically rewritten.

## Small experiments that answer the remaining questions

1. **Connect the saved-project reader.** The bounded reader now works on real MVP and MV_ snapshots and has fifteen passing offline tests. An eventual board import should preserve its fingerprint, reference evidence, and explicitly unknown fields. Do not open or probe referenced media automatically.
2. **Controlled timeline comparison.** Later, use disposable project copies with one known difference each: add one clip, move it, trim it, then change its track. Compare bytes and decoded records against the exact actions. This is needed before promising automatic scene-to-timeline links.
3. **Effect communication experiment.** Build a minimal x64 identity OFX effect outside the MAGIX installation. In a future test session, query actual host capabilities and test ordinary controls, a button opening Buddy, instance lifetime, persistence, and clean removal. It may depend on the selected object and add a project dependency. Do not assume custom drawing support.
4. **Private Qt interface experiment.** Establish an isolated resource-loading route, then substitute one noninteractive label in a reconstructed resource copy. Only after that succeeds should read-only context or a canvas be attempted. This is separate from proving independent pane registration.

The existing instruction to avoid computer control remains in force. This investigation uses filesystem reads, local code/tests, and documentation. It does not operate MAGIX or alter its installation. Targeted native disassembly and Qt resource decoding have now been performed; no complete MAGIX source project has been recovered. Further reverse engineering should target one interface or record format whose behavior we can test.

## Evidence

- [Installed plug-in and embedding research](embedding-research.md)
- [Exact-install OFX initialization and timeline callbacks](ofx-host-research.md)
- [Private Qt interface and native context bindings](qt-interface-research.md)
- [Documented extension options in the 2021 Steam manual](documented-extension-options.md)
- [Saved-project inspection findings and usage](project-research.md)
- [Unicode records and original-project findings](project-format-deeper.md)
- [Read-only project inspector](../tools/inspect_magix_project.py)
- [Earlier live accessibility observations](control-map.md) — these were recorded before the instruction to stop computer control; no new live inspection is implied.
- [OpenFX custom interfaces](https://openfx.readthedocs.io/en/main/Reference/ofxInteracts.html) — optional effect overlays and parameter widgets, not general application panels.
- [OpenFX timeline suite](https://openfx.readthedocs.io/en/main/Reference/suites/ofxTimeLineSuiteReference.html) — current time, seek, and bounds.
- [Microsoft SetParent documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent) — native child-window parenting with style and DPI considerations; not a MAGIX extension contract.

The API references explain possible mechanisms. Only the installed-file observations establish evidence for this edition, and only future runtime tests can establish host behavior.
