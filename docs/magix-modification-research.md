# What we can modify or extend in MAGIX 2021

Research completed 2026-10-29 against **Movie Edit Pro 2021 Plus Steam Edition**, Steam app **1363250**, executable **20.0.1.80**. This is the current research summary; the linked reports contain the reproducible evidence.

There are credible routes to connect Buddy with MAGIX, and a more concrete lead for changing MAGIX's own interface. A general dockable-panel API and a reliable project writer have not been established. The strongest new UI lead is MAGIX's packaged Qt interface source. The strongest working data connection is the offline project reader.

## Findings that change the plan

### 1. Parts of MAGIX's interface are readable source

The installed `qml/resource.rcc` contains **465 QML source files**, among 2,476 resource entries. A new read-only tool decoded the resource format and validated the bounds and decompressed sizes of all entries. This was resource extraction, not recovery of the editor's C++ source code.

Several pages have concrete private connections to native code: current movie name, preview position, selected-object name and source metadata, and effect controls. The installed executable references relevant pages and context names and imports Qt resource/context registration functions. This makes targeted interface research much more grounded than guessing from screenshots.

**What it could enable:** changing an existing panel's presentation, displaying host context, and potentially placing a Buddy control or canvas in an existing UI surface.

**What remains unknown:** how to load a substituted resource in isolation, whether the relevant page is active in this edition, and which native objects it receives. A new QML file alone does not register a new dockable pane. Some packaged pages belong to other product variants. The video-image area is native, so drawing over playback is a separate problem from drawing in a controls panel.

The smallest future test is one noninteractive label in a reconstructed resource copy, after establishing a valid isolated loading route. It should precede a canvas or any calls into private host objects. [Detailed Qt findings](qt-interface-research.md) · [Read-only resource inspector](../tools/inspect_qt_resource.py)

### 2. Standard OpenFX drawing is a weaker route than we initially thought

Targeted disassembly of the installed OFX wrapper found explicit initialization writes setting **custom parameter interaction = 0** and **preview overlays = 0**. This supersedes the earlier assessment based only on strings in the binary. In particular, a generic table default of 1 for overlays is overridden by MAGIX's initialization code.

We should therefore stop treating a standard OFX canvas as the likely whiteboard implementation. Later runtime changes are still possible, but no live capability query has been performed. Simply forcing those flags to 1 would not establish that the required implementation exists.

Ordinary plug-in parameters and a button that opens Buddy remain a separate experiment. The wrapper also returns an OFX timeline-suite table with real forwarding callbacks for time, seeking and bounds. Their downstream behavior remains unverified, and this suite cannot enumerate the whole project. [Exact binary evidence and addresses](ofx-host-research.md)

### 3. Saved-project access now works on the original project

The reader located and inspected the cached original `.MVP`, beyond the earlier autosave. It contains **103 distinct candidate asset references** in 27,172 parsed chunks. These are stored asset references, not 103 scenes or proof that the media exists or is currently on the timeline.

Across the original, autosave and six installed sample projects, the reader decoded **683 Unicode text records**. All **568 resolved filename-ID links** matched their duplicate legacy text. This establishes a useful part of the format and explains why changing one visible path string would be insufficient: text appears in multiple records tied together by IDs.

The working reader now reports these records and byte-level evidence. **Fifteen offline tests pass**, including malformed data, Unicode characters, ambiguous IDs, read-only behavior and output-overwrite protection. Track numbers, clip timing, trims and effect semantics remain undecoded. No project-writing function was added. [Project-format evidence](project-format-deeper.md) · [Reader and usage](project-research.md)

### 4. There are documented extension paths worth keeping separate

The exact Steam manual documents OFX/video effects, external audio and picture editors, reusable effect/title presets, image masks, and VST audio effects. These offer practical bridges for Buddy-generated assets or focused effects. They do not establish a general application API. The external-editor calling convention and template binary formats remain unresolved. [Official 2021 Steam manual](https://cdn.cloudflare.steamstatic.com/steam/apps/1363250/manuals/Videodeluxe_EN.pdf?t=1599654586) · [Page references and vendor evidence](documented-extension-options.md)

## Options for Buddy

| Route | Useful result | Evidence today | Remaining proof |
| --- | --- | --- | --- |
| Companion whiteboard + saved-project reader | Organize scenes and import candidate asset cards | Reader works on real snapshots; board drawing exists in the separate design preview | Connect the reader to the eventual whiteboard application |
| Export chosen drawings/stills for MAGIX | Turn selected planning work into masks or overlay assets | MAGIX documents image-based workflows | Define the intended export and verify its import; ordinary doodles need not become video assets |
| Effect with ordinary controls and a Buddy button | Open the relevant board/card from a selected effect instance | Exact install has an OFX bridge and reflected controls | Build/load a disposable identity effect; verify control behavior and parameter persistence |
| Private QML resource change | Alter an existing MAGIX interface surface | Readable source, executable references and native context bindings | Isolated loading, one-label substitution, then read-only host context |
| Independent dockable pane | Keep the full board inside MAGIX | Internal docking exists | No third-party pane registration contract found |
| Rewrite a project snapshot | Change saved project data programmatically | Some text records and ID links decoded | Controlled one-change fixtures, complete affected record semantics, and open/save validation |

These are independent choices. A useful companion does not depend on the private UI experiment succeeding, and a successful private UI change would not by itself provide project editing.

## Next research experiments, once application interaction resumes

1. **Verify private UI substitution:** resolve the resource loading path and isolated launch feasibility; alter only one label in a copy and compare every resource entry hash. A visible label would prove that particular substitution route.
2. **Measure the plug-in contract:** use a minimal identity effect to query the two live drawing capabilities and read time/bounds while a person moves the playhead. Then test an ordinary button and one persisted card ID. No guessed private calls or forced capability flags.
3. **Decode one project operation:** compare disposable saved projects differing only by one added clip, move, trim or track change. Validate each interpretation against the known edit before exposing it as a fact or attempting a writer.
4. **Measure external-editor handoff if useful:** a logging adapter and disposable image can establish arguments and save expectations before integrating Buddy with that setting.

These are proposed tests, not completed capabilities. No design choice is required from the user to preserve the findings.

## Work performed and limits

This round used file reads, documentation, resource decoding, targeted native disassembly, and local reader tests. It created research tools and reports in the Buddy workspace. No MAGIX window was controlled, installed DLL loaded, plug-in installed, application resource changed, or MAGIX project modified. The native Buddy 0.1.0 executable was not changed and still provides the earlier review companion rather than the new whiteboard.

The investigation has not recovered a complete buildable MAGIX source project. Addresses, hashes and resource observations apply to this installed build; private interfaces may differ across versions. Full extracted commercial resources and project paths remain in ignored local research outputs, rather than becoming distributable Buddy assets.

## Runtime follow-up on MSI

After the static research above, the user authorized testing a copied installation on MSI. Its first launch redirected through Steam to the installed editor. Original installation-file hashes matched afterward, but normal user settings and a startup log changed; the patched label was not reached. SSH connectivity was subsequently repaired with the user's approval, and 84 relevant files plus MAGIX's user registry settings were backed up. See [the MSI test record](msi-test-session.md) for the precise scope, side effects, and subsequent guard checks. The copy's profile redirection remains unproven as complete isolation.

A subsequent guarded retry ran the copied executable and loaded its Qt modules, then showed startup error -59. All 87 protected-file hashes and the user-registry export matched afterward. MAGIX also created a separate test-specific folder in real system ProgramData despite the environment override; its contents were preserved and that generated folder was removed. The UI label still has no visual validation, and the startup/shared-data routing problem remains unresolved.
