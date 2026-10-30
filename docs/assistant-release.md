# ED-IT Buddy 0.3.1 assistant preview

ED-IT Buddy now provides a desktop Manager, durable task broker, MSI media worker, Codex chat, and an installed INTERLINKED companion. Live checks include the normal desktop connection and chat workflow, delivery during a managed MAGIX edit, and explicit verified save-back. The preview remains `release_verified: false` while the real full-corpus retrieval evaluation remains incomplete.

## Start and data

Run `./package-assistant.ps1` to create a separate portable package, then open its `bin/ED-IT Buddy.exe`. The historical `package.ps1` entry point forwards to this complete packager so the Manager always includes its broker. Existing `bin` and earlier packages are not replaced. The desktop file version is `0.3.1.0`, with informational version `0.3.1-preview`.

The Manager is the default tab. Guide, Review, and Controls remain available. The Manager starts disconnected; **Connect / refresh** starts the local broker and checks MSI, INTERLINKED, and the existing Codex sign-in. **Sign in to Codex** and frame attachments are explicit actions. The legacy MAGIX review connection is separate and does not start automatically from the Manager.

Windows x64, .NET Framework 4.8, and Node 22.13 or newer are required. `EDIT_BUDDY_NODE` can select an existing Node executable. The broker reads `assistant/config.example.json` unless `EDIT_BUDDY_ASSISTANT_CONFIG` selects a private configuration file. The example targets the established MSI identity and immutable `v0.3.1` media-worker directory; it contains no credentials. Private `assistant/config.json` is ignored by Git.

Tasks and per-project conversation IDs live under `%LOCALAPPDATA%\ED-IT Buddy\assistant`. Keep this default location outside source repositories so unrelated project-specific tools are not inherited by chat. Staging and preview caches are separate from original footage and managed project files. Execution, delivery, save-back, and human review are separate observations. Reports remain available locally if INTERLINKED publication fails.

## Current behavior

The desktop retains a requested search selection when a task poll overlaps submission, retries completed result retrieval after a transient failure, and restores the latest search after reconnect. Search results use a separate two-minute retrieval timeout for verified thumbnail transfers. Regression coverage exercises delayed task visibility, a failed result fetch, recovery without a status change, and reconnect without submitting a new search.

- Choose an INTERLINKED project, queue supplied URLs, register selected MSI footage folders, index and search visual moments, preview candidates, extract clips, and organize copies by date, source, or tag.
- Clear requests can run through chat or normal controls. Copies retain existing original paths. Source files and the established downloader's shared archive and cleanup workflow remain intact.
- Media jobs run on MSI independently of the SSH client. Buddy retains offline tasks and reconciles stable request IDs after reconnect. Existing library index jobs appear as **Library index** across project task views while retaining their original worker identity.
- SigLIP2 analysis stays on MSI. Search returns source-linked candidate ranges and relative similarity scores, not certainty. Only a frame deliberately attached to chat is sent with that message.
- Codex uses its required isolated tool-dispatch host, with command, editing, app, plugin, browser, and computer capabilities disabled separately. Before each inference, the complete inherited MCP catalog is checked. A late catalog triggers one process re-isolation with those exact servers disabled, followed by another empty-catalog check; persistent callable connector tools still cause a refusal before any user input is submitted.
- INTERLINKED owns project selection, workspace locks, accepted file ingestion, and verified save-back. Automatic delivery never implies that a clip has been imported onto a MAGIX timeline or saved back to MSI.

## Verification recorded on October 30, 2026

| Check | Observed result and limit |
| --- | --- |
| Automated checks | 41 Node broker/contract tests, 22 media-worker tests, and 32 WPF protocol/layout checks passed. Existing companion-state and build-preservation tests also passed. The companion suite passed 45 Python native/HTTP/ownership/availability cases and 18 JavaScript report-renderer checks. |
| Codex live connection | Existing sign-in succeeded. Replies streamed, one permitted read-only Buddy task tool executed, an explicitly attached red/blue synthetic image was recognized, and a shell request was refused without command, file, or MCP execution. No new login or credential inspection was needed. |
| MSI GPU preflight | The scheduled-user `pythonw.exe` context passed image/text inference on the RTX 2070 Max-Q with CUDA 12.6, batch eight, and 768-dimensional features. This proves inference in the actual launch context, not library throughput. |
| Real footage indexing and search | Three selected MSI sources were registered. The latest captured index observation was running at 89 of 122 files; searches already returned previewable ranges from real band footage. This is partial indexing, not a completed more-than-five-hour benchmark or a labeled retrieval-quality evaluation. |
| Pause and reconnect | A running index paused at a checkpoint; the paused receipt survived a new connection. The resume request returned the same job queued, without creating duplicate work. |
| Internet download | A supplied public W3C Sintel trailer URL completed on MSI, producing a verified H.264/AAC MP4 and source metadata. This tests that URL and the observed tools, not every supported website. |
| Preview and extraction | An eight-second range from real band footage produced separate preview and extracted H.264/AAC outputs. Their byte sizes and SHA-256 values were verified after transfer to THIS-MACHINE. |
| Connected broker and reporting | The real broker reported MSI, INTERLINKED, and Codex connected; discovered 135 projects, three sources, and one ongoing library index; returned 20 search candidates; streamed 62 Codex reply chunks; and published a project report. Chat read the delivered clip and library task without starting or changing work. |
| Normal desktop workflow | A cold start of the compiled Manager connected all three services and displayed an accurate streamed read-only chat reply about the completed search, delivered clip, and ongoing library index. Tasks were not changed. This was repeated after the bounded chat-isolation recovery and delivery-reconnection fixes. |
| Active-session delivery | The broker extracted an eight-second clip on MSI and automatically transferred it through INTERLINKED into a disposable managed MAGIX session while both native workflow/cache locks remained held. Physical and mapped-path readback matched its content hash. The original project bytes were preserved. This proves additive file availability, not automatic timeline import. |
| Explicit save-back | The disposable MVP was saved and MAGIX closed normally, then the managed session finished. Explicit INTERLINKED Save + Verify and an independent Verify completed. The saved manifest retained all 264 original file contents and references, added the delivered clip and disposable MVP, and contained 266 files with no tombstones. Local cache release was not automatic. |
| Sealed-runtime availability | The installed companion's guarded retained-read preflight passed after a fresh app start and again in a separate child process, without external manual warming. It validates the unchanged accepted 52-file runtime and its seal before holding ordinary read handles for the action. Actual offline, reparse, recall, link, and hash failures still refuse the operation. |
| Packaging | The complete runtime allowlist, file hashes, x64/.NET metadata, broker version, and `v0.3.1` target passed. The actual packaged broker bootstrapped disconnected. Tampered-runtime and missing-broker fixture packages were rejected. |

The isolated MSI runtime is under `C:\Users\jlewi\AppData\Local\ED-IT-Buddy-Media`: Python 3.12.10, PyTorch 2.8.0+cu126, Transformers 4.57.1, Pillow 11.3.0, and NumPy 2.2.6. Model `google/siglip2-base-patch16-224` is pinned to revision `75de2d55ec2d0b4efc50b3e9ad70dba96a7b2fa2`. The deployment directory/task is `v0.3.1`; the existing capabilities engine's descriptive version field still reports `0.3.0`. The preflight records exact loaded source hashes, so deployment identity must not be inferred from that descriptive field alone. Existing downloader scripts and INTERLINKED's sealed base runtime were preserved.

Private evidence remains in the developer workspace's ignored `.build` directory and is excluded from packages and Git. Principal records are `msi-preflight-v031.json`, `chat-live-1793371862868/validation.json` with adjacent provenance, `live-band-index-current.json`, `live-band-search-results.json`, `live-worker-controls.json`, the download/preview/extract result manifests and `live-copied-artifacts.json`, `live-broker-connect-result.json`, `live-broker-delivery-result.json`, `live-manager-ui-acceptance.json`, and `live-interlinked-acceptance.json`. The final companion publication receipt is under `interlinked-buddy-publish-kit-20261030T153949Z-50638278/receipt.json`; saved-version audit evidence is in `interlinked-saved-manifest-audit.json`.

## Remaining acceptance work

Finish indexing the selected real corpus and evaluate more than five hours of recordings with labeled queries such as crowd shots, drummer close-ups, and wide stage views. Record processing speed, memory use, and usefulness of the top matches. The six-hour automated fixture uses synthetic timestamps and fake inference.

The earlier MSI MAGIX timeline-automation experiment remains stopped. This assistant does not depend on it and does not claim automatic timeline editing.

## Build and verification commands

```powershell
node --test assistant/test/*.test.mjs
python -B -m unittest discover -s tests -p test_media_worker.py -v
.\tests\run-assistant-client-tests.ps1
.\tests\run-companion-state-tests.ps1
.\tests\run-build-tests.ps1
.\package-assistant.ps1
.\tests\check-assistant-package.ps1 -PackageDirectory '<new package folder>'
```

The assistant packager includes only the explicit runtime/documentation allowlist and its complete SHA-256 manifest. It excludes keys, personal task state, media, models, private catalogs, and generated INTERLINKED snapshots. Unrelated pre-existing portal, MASTER-status, and MAGIX ownership work remains separate from this assistant release's changes.
