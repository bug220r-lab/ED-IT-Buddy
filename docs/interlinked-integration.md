# ED-IT Buddy and INTERLINKED integration

This document records the original integration design and inspected baseline. The companion and media workflow have since been implemented; see [current assistant verification and acceptance](assistant-release.md) for installed state and live results. Statements below about missing interfaces describe that original baseline.

Direction agreed October 30, 2026: ED-IT Buddy should manage the busy work around editing. It should find useful moments in band footage, obtain requested internet video, organize media, track tasks and report results into INTERLINKED. INTERLINKED remains responsible for managed project workspaces, storage and verified save-back. MAGIX remains an editing application used within that workflow.

## Responsibilities

| Component | Responsibility |
| --- | --- |
| ED-IT Buddy | Editing requests, footage search and clip candidates, download requests, media preparation, task tracking and understandable reports |
| Downloader worker | Obtain requested URLs and prepare media inside an isolated job directory; return outputs and processing evidence |
| INTERLINKED | Project identities, scoped workspace access, cache/session locks, accepted file ingestion, save, verification and explicit release |
| MAGIX | Creative editing and project/export operations whose support has been established separately |

Clip candidates should retain the source asset identity, time range, selection reason and review state. Suggested highlights do not become approved edits merely because analysis finished.

## Inspected source and installed state

The repository is [bug220r-lab/interlinked](https://github.com/bug220r-lab/interlinked). Its connected GitHub source snapshot was read alongside the local checkout at `C:\Users\Jude\Documents\Codex\INTERLINKED`. The local source includes newer unified project hub, WebView2 desktop, MSI job observer and MASTER integration code that was absent from the fetched GitHub overview. Local source support is not proof that an installed application has those additions.

The installed client kit is `C:\Users\Jude\Documents\MSI-Project-Library`; it is separate from source. Managed local files belong under `%LOCALAPPDATA%\ProjectLibrary\Cache\<original-project-id>\Files`. Canonical shares are `\\MSI\ProjectLibrary` and `\\MSI\ProjectReturns`. INTERLINKED's reviewed client owns fetch, save, verification and release; published library files must not become a downloader output folder.

The [INTERLINKED handoff](https://github.com/bug220r-lab/interlinked/blob/main/repository/handoff/README.md) and its local `AGENTS.md` require preserving active caches, edits, deletions, ownership receipts and intentional identity/hash pins. One active MAGIX working family is supported. Existing session completion, explicit save-back and explicit release remain separate actions.

## Existing interfaces

These are the inspected local source interfaces, not a new external API guarantee. INTERLINKED's Python service binds an ephemeral loopback port. Requests require `X-INTERLINKED-Token` and validated Host/Origin; mutations require the exact same origin. External application registration, discovery and authentication for Buddy still need a supported design. Do not extract tokens from logs or weaken the origin checks.

| Interface | Existing contract |
| --- | --- |
| `GET /api/catalog` | Project identities, capabilities, cache readiness and save/verification observations. General projects have an opaque UI ID and a stable `originalId`; persist the original project identity rather than a display name. |
| `GET /api/state` | Operation, busy state, active session, revision and log directory. |
| `POST /api/action` | `{action, project, entry?, confirmation?, expectedRevision?}`. Reviewed actions include fetch, folder, open, save, verify, release and scoped recovery. The accepted action depends on the selected adapter/capabilities. |
| `POST /api/session` | `{response, operationId, promptGeneration}`. Existing completion responses bind the current operation and prompt generation. |
| `GET /api/msi/jobs` | Last locally cached MSI job observation; reading it does not contact MSI. |
| `GET /api/msi/jobs/log` | Query parameters `job` and `stream`, with stream `stdout` or `stderr`; returns a bounded observed log tail. |
| `POST /api/msi/jobs/refresh` | `{}`; explicitly refreshes the read-only MSI job observation. No submission, cancellation or result-import endpoint exists. |

Source references in the local INTERLINKED checkout:

- `repository/app/interlinked_gui.py`: `Handler.gate`, `Handler.do_GET`, `Handler.do_POST`, `Controller.arguments` and `Controller.session_input` define authentication, routes and typed operations.
- `repository/app/interlinked_controller.py`: `HubController.catalog`, `start`, `_workflow_acquire`, `_folder_session` and `_client_action` coordinate adapters and lifecycle locks. Folder sessions hold `.workflow.lock` and the selected cache's `.client.lock` until completion.
- `repository/app/interlinked_hub.py`: `validate_catalog`, `ProjectHub.folder_path` and `ProjectHub.arguments` validate stable project identities and owned workspace paths.
- `repository/app/interlinked_jobs.py`: `validate_snapshot`, `view`, `refresh` and `log` implement reduced, stale-aware job observations.
- `repository/outputs/Project-Library-2026-10-29/Client/project_library.py`: `fetch`, `save`, `verify` and `release` implement the accepted storage engine. Save includes new files within the owned `Files` tree.
- `repository/outputs/Project-Library-2026-10-29/Server-Setup/Start-MSIJob.ps1` and `msi_job_runner.py` implement explicit, machine-bound MSI jobs. They are a possible worker foundation, not an arbitrary-command API for Buddy.

## Existing downloader on MSI

The downloader was located at `C:\Users\jlewi\Documents\VideoDownloader`. Its PowerShell 5.1 entry point is `Download-Videos.ps1`, with parameters `UrlsFile`, `RepairFile` and `CleanSources`. Portable tools include yt-dlp, FFmpeg, FFprobe and Deno. See [the downloader discovery record](video-downloader-discovery.md) for inspected entry points, behavior and historical results.

The current script writes beneath its own directory: `Footage\<site>`, `Logs\*-results.csv`, and `State\download-archive.txt`, with an exclusive `download.lock`. Results CSV columns are `URL`, `Status` and `Details`. Download metadata includes source URL, description and `info.json`; conversion evidence includes `processing.json` receipts.

The existing workflow can remove converted source files after validation and perform cleanup. Its output root is fixed to `$PSScriptRoot\Footage`. Do not run that launcher unattended against the entire existing downloader directory as an integration shortcut. First extract or wrap the useful download/conversion behavior with an explicit job root and cleanup restricted to files created by that job. Preserve existing footage, archives and receipts. Inventorying the source does not establish a successful new download or a deployed Buddy integration.

## Proposed media handoff

Add an owned, typed ingestion operation to INTERLINKED. Buddy should not copy over active caches or hold a second independent interpretation of their locks.

1. Resolve the selected project through INTERLINKED and retain its original catalog identity. INTERLINKED supplies a scoped destination category and current readiness/session observation.
2. Buddy records a stable request ID and task, then runs the downloader or footage preparation worker in a separate owned staging directory. Inputs are explicit URLs or selected asset references, not arbitrary commands.
3. The worker returns an output manifest with job ID, source URL or source asset identity, ordinary staged relative paths, byte sizes, SHA-256 hashes, media metadata, receipt references and per-item status. Partial files remain incomplete.
4. Buddy submits an ingestion manifest naming the target project, stable request ID, destination category and staged manifest identity. INTERLINKED validates provenance and containment, rechecks the selected workspace/session, and acquires its existing locks. Authentication and scoped staging registration are part of this new contract.
5. INTERLINKED commits accepted files without unrequested replacement, then returns project-relative destination paths and content hashes. A repeated identical request returns the same receipt; an interrupted or changed request is reconciled before retry.
6. Buddy marks the ingest complete only after that receipt. Save to MSI and verification use INTERLINKED's existing lifecycle. Successful download, successful ingest and verified save-back remain distinct task states.

Proposed operation/manifest names are design placeholders; no ingest route or staging-authentication mechanism exists in the inspected API. Begin with additive files and explicit conflict reporting. Rename, move and deletion operations need their own reviewed plans because project references may depend on paths.

## Tasks and reporting

Maintain a separate durable Buddy task/job ledger. Track project identity, request/job IDs, requested work, source assets/URLs, progress, outcomes, output hashes/paths and actionable errors. Useful states include queued, running, needs review, failed, outcome uncertain and completed. Completion requires output evidence appropriate to the task, not a successful process start. Keep user-marked completion distinct from worker-observed completion, with attribution and time. A downloaded file is not thereby ingested into INTERLINKED, imported into MAGIX or used on a timeline.

Reports should show completed work, remaining work, blocked items and the next user action. Counts should distinguish downloaded files, ingested assets, reviewed clip candidates and verified saved outputs. Expose a bounded project summary and report reference to INTERLINKED through a new agreed task/media contract.

Preserve the existing passive `interlinked-buddy-status-v1` channel documented in [the portal notes](portal.md#passive-interlinked-master-status-feed). The Buddy dashboard publishes `%LOCALAPPDATA%\ED-IT Buddy\master-status\status.json`; local INTERLINKED `repository/app/interlinked_master.py` consumes it with its ownership marker and a 15-second freshness limit. It describes MSI editor sequence activity and intentionally excludes paths, IDs and artifacts. It does not provide task tracking, file ingestion or downloader control. Keep richer project work reports separate from that status feed.

## Finding clips

Start with filename, folder, source-date and user-tag search over explicitly selected footage. Add timestamped transcript search for spoken material and checked lyric alignment where suitable. Every result should identify its source and playable time range; unindexed assets and uncertain transcript matches stay visible as limitations.

Optional visual indexing can later support requests such as finding a drummer close-up or a crowd shot. Visual matches and musical highlights are suggestions for review. Start with searchable evidence and manual selections before promising automatic judgments about the best performance moment.

## Implementation milestones

| Milestone | Acceptance |
| --- | --- |
| Isolated downloader adapter | Synthetic tool fixtures prove job isolation, stable IDs, progress/result receipts, bounded cancellation and cleanup limited to owned new files. A selected live URL is tested separately. |
| INTERLINKED workspace handoff | A supported registration/auth mechanism and typed ingest operation accept a fixture asset into one owned project. Held locks, stale sessions, duplicate requests, path escapes and collisions preserve existing files. |
| Complete download task | One requested download produces a verified staged asset, an INTERLINKED ingest receipt and a visible Buddy task report. An interruption can be reconciled without an accidental second download or overwrite. |
| Footage search | Selected band footage is indexed into source-linked candidates. Queries return playable time ranges and reasons; users can review candidates before extraction. |
| Project work report | INTERLINKED displays Buddy's project summary, task outcomes, blockers and evidence links without confusing them with the passive editor status feed. |
| Save-back acceptance | Finish the managed editing session, save through INTERLINKED and verify the resulting version with the newly ingested assets included. Local release remains explicit. |

This note establishes the integration direction and source findings. It does not change INTERLINKED, launch the downloader, alter media or deploy a new Buddy build. The earlier [live-show vertical clips option](band-options.md) remains a possible downstream editing workflow.
