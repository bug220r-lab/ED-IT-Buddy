# ED-IT Buddy

A Windows editing assistant for INTERLINKED projects and MAGIX media workflows. The Manager queues downloads, searches footage, prepares clips, organizes copies, and tracks work while media processing runs on MSI. The original review notes, guides, and control inspection remain available.

The companion, export preview, and MSI portal share a yellow Matrix theme inspired by [INTERLINKED](https://github.com/bug220r-lab/interlinked): dark olive panels, monospace text, yellow focus and selection states, and a quiet binary/scanline header. The theme is bundled in the executable and needs no extra assets.

**Product role:** Buddy manages the busy work around editing and feeds prepared assets and reports into INTERLINKED. INTERLINKED owns project identity, managed workspaces, and verified save-back. See the [integration design](docs/interlinked-integration.md), [downloader baseline](docs/video-downloader-discovery.md), and [current assistant verification](docs/assistant-release.md). The earlier [whiteboard and MAGIX investigation](docs/integration-feasibility.md) remains background research.

**Current source version: 0.4.1 preview.** The new [Media inbox](docs/media-inbox.md) adds local-to-MSI archive transfer and watched folders. It has isolated verification; production deployment and real-media acceptance are pending MSI connectivity. The previously installed 0.3.1 preview workflow remains the live baseline. The normal desktop workflow, MSI GPU inference, Codex streaming/tools/frame input, supplied-link downloading, partial real-footage search, preview, extraction, active-session asset delivery, and explicit INTERLINKED save-back have passed live checks. Current verification is tracked in the [assistant status](docs/assistant-release.md). The full-corpus real-footage benchmark is still pending, so the portable manifest retains `release_verified: false`.

## Manager start

Open **`bin\ED-IT Buddy.exe`** from the source workspace or a complete assistant package. Keep all package folders together, including `assistant` and `data`. Manager requires the existing Node runtime; `EDIT_BUDDY_NODE` can select it explicitly.

1. Choose **Connect / refresh** to check MSI, INTERLINKED, and the supported Codex sign-in. The app starts disconnected.
2. Select an INTERLINKED project. Source footage and processing remain on MSI.
3. Register a selected MSI source folder and start indexing, or paste supplied video links into the download queue.
4. Search for a visual moment, preview a candidate, then extract or organize copies. Original source paths remain intact.
5. Use **Tasks** for progress and review, and **Report** for completed work, blockers, and delivery state. INTERLINKED save-back remains explicit.

Chat and normal controls use the same work queue. Only deliberately attached frames are shared with chat. Existing library indexing stays visible across projects as **Library index**. When MSI is unavailable, tasks stay queued and cached observations remain available.

Run `./package-assistant.ps1` to create a separate, hash-checked portable package. The historical `package.ps1` command now forwards to the complete assistant packager. Packaging does not overwrite existing `bin` or earlier packages.

## Review workflow

The retained review tools store notes under `user-data` beside `bin` and `data`. They can preview local exports without starting the Manager broker or installing another SDK.

1. A new copy starts in **Work offline** mode. Use **Review** to choose a local video export, edit or add timestamped notes, and save. The included Test 3 notes are imported suggestions from the earlier critique, not a new analysis.
2. In **Guide**, select a note and work through its instructions. Offline, **Next instruction** skips an assisted action without marking it completed.
3. When ready, open MAGIX through Steam and finish any startup or missing-media dialogs. Choose **Connect to MAGIX** in Buddy. The header reports the actual connection and dialog state.
4. Use **Controls** to inspect MAGIX or bring it forward. Confirm the project/export match before a note can run an assisted action. Manual steps remain explicitly manual.

**Keep MAGIX review offline** disables review inspection, launch, and focus actions. This preference is remembered. `--offline` forces that review setting; the Manager has its own explicit connection action. The local review video player uses Windows decoding and neither uploads the file nor controls MAGIX.

## Review tools

- Detects the installed Steam app and its running process by executable path.
- Reads a bounded accessibility snapshot in a separate helper process with a timeout.
- Identifies the native editor window and reports modal or secondary-window state.
- Brings MAGIX or its current dialog forward, then verifies foreground focus.
- Stores editable review notes locally, with JSON import/export and automatic backups.
- Preserves source attribution and records when a suggestion has been edited.
- Previews supported local video files and jumps to a note's time. Seeking depends on the Windows media decoder; it is not frame-accurate MAGIX timeline navigation.
- Clears project mapping when the observed project/window changes, on disconnect, in offline mode, and across restarts.

The review tools have no automatic critique and do not modify MAGIX project files, split or delete objects, change effects, start exports, or send arbitrary keyboard commands. Notes do not automatically identify timeline objects. Manager chat and media jobs are separate capabilities described in the [assistant status](docs/assistant-release.md).

## Local data

`user-data\review-notes.json` holds your notes. Previous versions are retained as backup files. Saves check whether another program has changed the notes file and stop instead of silently replacing that change. `user-data\preferences.json` stores offline mode. `user-data\buddy.log` records operations and errors; `user-data\latest-controls.txt` contains the most recent accessible-control snapshot. These files stay local. The directory is ignored by Git.

Manager tasks, cached results, staging, and per-project conversation IDs live under `%LOCALAPPDATA%\ED-IT Buddy\assistant` and are shared between package versions. They are excluded from packages and Git.

The sample note schema and recipes are in `data`. Imported notes can request only the predefined focus and inspection actions. Unknown action IDs and inconsistent manual/assisted steps are rejected. File size, timestamps, duplicate IDs, and collection sizes are checked before import.

## Build and test

From PowerShell in this folder:

```powershell
.\build.ps1
.\tests\run-store-tests.ps1
.\tests\run-companion-state-tests.ps1
.\tests\run-controller-tests.ps1
.\tests\run-build-tests.ps1
.\tests\run-assistant-client-tests.ps1
node --test assistant/test/*.test.mjs
.\package.ps1
```

These commands apply to the source workspace. Close Buddy yourself before rebuilding `bin`, or use `build.ps1 -OutputDirectory .build\my-build` for a separate build. The build uses the Windows .NET Framework C# compiler and WPF assemblies already on this PC. The source is compatible with C# 5 and uses no NuGet packages.

Packaging creates a new `dist\ED-IT-Buddy-0.4.1-preview-<timestamp>` folder and ZIP, leaving `bin` untouched. It includes the executables, assistant broker, media-worker contract files, seed data, and documentation. Personal notes, task state, keys, media, models, and test fixtures are excluded. Its SHA-256 manifest, executable versions, worker target, and complete runtime allowlist are checked before publication. An existing package is never overwritten; choose a new `-PackageName` for another build.

`ED-IT Buddy.Probe.exe` is a private helper used by the companion. Launch the main `ED-IT Buddy.exe` application.

## Development notes

- [Original design](docs/design.md)
- [Assistant verification and remaining acceptance](docs/assistant-release.md)
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
