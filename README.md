# ED-IT Buddy

A working Windows companion for MAGIX Movie Edit Pro 2021 Plus Steam Edition. It keeps review notes beside your editor, walks through revisions, previews local exports, and inspects the controls MAGIX exposes.

The companion, export preview, and MSI portal share a yellow Matrix theme inspired by [INTERLINKED](https://github.com/bug220r-lab/interlinked): dark olive panels, monospace text, yellow focus and selection states, and a quiet binary/scanline header. The theme is bundled in the executable and needs no extra assets.

**Product direction:** ED-IT Buddy should manage the busy work around editing: find clips in supplied footage, acquire requested internet videos through the existing MSI downloader, organize project assets, track tasks, and report progress. It should feed assets and work results into INTERLINKED, which owns project identity, managed workspaces, and verified save-back. See the [INTERLINKED integration plan](docs/interlinked-integration.md) and [MSI downloader discovery](docs/video-downloader-discovery.md). These are planned additions; the existing 0.1.0 executable remains the review companion described below. The earlier [whiteboard and MAGIX investigation](docs/integration-feasibility.md) is background research.

**Manager preview in source:** The desktop Manager, durable task broker, MSI media engine, chat adapter and INTERLINKED companion contribution are now implemented as a development preview. Run `./package-assistant.ps1` to build a separate package. The original `bin` stays unchanged. See [assistant status and release gates](docs/assistant-release.md) for actual verification and the remaining live acceptance work. The INTERLINKED contribution is a candidate overlay, not a deployed update.

## Start

Open **`bin\ED-IT Buddy.exe`** inside the portable folder. Keep the `bin` and `data` folders together. Buddy creates `user-data` beside them. No SDK or dependency installation is required to run this build on the current PC.

1. A new copy starts in **Work offline** mode. Use **Review** to choose a local video export, edit or add timestamped notes, and save. The included Test 3 notes are imported suggestions from the earlier critique, not a new analysis.
2. In **Guide**, select a note and work through its instructions. Offline, **Next instruction** skips an assisted action without marking it completed.
3. When ready, open MAGIX through Steam and finish any startup or missing-media dialogs. Choose **Connect to MAGIX** in Buddy. The header reports the actual connection and dialog state.
4. Use **Controls** to inspect MAGIX or bring it forward. Confirm the project/export match before a note can run an assisted action. Manual steps remain explicitly manual.

**Work offline** disables inspection, launch, and focus actions. This preference is remembered. You can also start the executable with `--offline` to force an offline session. The local video preview uses Windows media decoding; it neither uploads the file nor controls MAGIX.

## What this build does

- Detects the installed Steam app and its running process by executable path.
- Reads a bounded accessibility snapshot in a separate helper process with a timeout.
- Identifies the native editor window and reports modal or secondary-window state.
- Brings MAGIX or its current dialog forward, then verifies foreground focus.
- Stores editable review notes locally, with JSON import/export and automatic backups.
- Preserves source attribution and records when a suggestion has been edited.
- Previews supported local video files and jumps to a note's time. Seeking depends on the Windows media decoder; it is not frame-accurate MAGIX timeline navigation.
- Clears project mapping when the observed project/window changes, on disconnect, in offline mode, and across restarts.

There is no cloud AI service or automatic critique in this build. It does not modify MAGIX project files, split or delete objects, change effects, start an export, or send arbitrary keyboard commands. Notes do not automatically identify timeline objects.

## Local data

`user-data\review-notes.json` holds your notes. Previous versions are retained as backup files. Saves check whether another program has changed the notes file and stop instead of silently replacing that change. `user-data\preferences.json` stores offline mode. `user-data\buddy.log` records operations and errors; `user-data\latest-controls.txt` contains the most recent accessible-control snapshot. These files stay local. The directory is ignored by Git.

The sample note schema and recipes are in `data`. Imported notes can request only the predefined focus and inspection actions. Unknown action IDs and inconsistent manual/assisted steps are rejected. File size, timestamps, duplicate IDs, and collection sizes are checked before import.

## Build and test

From PowerShell in this folder:

```powershell
.\build.ps1
.\tests\run-store-tests.ps1
.\tests\run-companion-state-tests.ps1
.\tests\run-controller-tests.ps1
.\tests\run-build-tests.ps1
.\package.ps1
```

These commands apply to the source workspace. Close Buddy yourself before rebuilding `bin`, or use `build.ps1 -OutputDirectory .build\my-build` for a separate build. The build uses the Windows .NET Framework C# compiler and WPF assemblies already on this PC. The source is compatible with C# 5 and uses no NuGet packages.

Packaging creates a new `dist\ED-IT-Buddy-0.1.0` folder and ZIP, leaving `bin` untouched. It includes the executables, seed data, and documentation; personal notes, logs, media, and test fixtures are excluded. Its SHA-256 manifest and file allowlist are verified before publishing the folder. An existing package is never overwritten; choose a new `-PackageName` for a later build.

`ED-IT Buddy.Probe.exe` is a private helper used by the companion. Launch the main `ED-IT Buddy.exe` application.

## Development notes

- [Original design](docs/design.md)
- [Whiteboard and MAGIX integration feasibility](docs/integration-feasibility.md)
- [Latest MAGIX modification research](docs/magix-modification-research.md)
- [Private Qt interface investigation](docs/qt-interface-research.md)
- [Exact-install OFX capabilities and timeline callbacks](docs/ofx-host-research.md)
- [Documented extension options](docs/documented-extension-options.md)
- [Installed plug-in and embedding research](docs/embedding-research.md)
- [Saved-project reader: findings and usage](docs/project-research.md)
- [Unicode records and original-project investigation](docs/project-format-deeper.md)
- [Observed MAGIX controls and validation](docs/control-map.md)
- [Review data and workflow](docs/review-workflow.md)

The current target is Steam app **1363250**, executable version **20.0.1.80**. Live inspection of the earlier build read MAGIX controls and recognized the missing-media dialog. The current offline, storage, and targeting changes are covered by compilation and tests that do not interact with Windows applications. Updated focus behavior, video playback, and panel activation still need live validation. Panel activation is disabled in this release.

A control exposing an accessibility pattern is a candidate for automation, not proof that changing it is reliable. More editing actions should be added one at a time with target checks and visible result checks.
