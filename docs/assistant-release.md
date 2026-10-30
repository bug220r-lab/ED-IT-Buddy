# ED-IT Buddy assistant preview

The source now contains the Manager desktop interface, local task broker, MSI media engine, Codex chat adapter, and an INTERLINKED companion contribution. This is a development preview. The complete workflow has not passed live release acceptance.

## Start and data

Build a separate portable preview with `./package-assistant.ps1` and open its `bin/ED-IT Buddy.exe`. The existing `bin` application is not overwritten. The Manager is the default tab; Guide, Review and Controls remain available. It starts disconnected. **Connect / refresh** starts the local broker and attempts the configured services. Sign-in and frame attachments are explicit actions.

The desktop requires Windows x64, .NET Framework 4.8, and Node 22.13 or newer. `EDIT_BUDDY_NODE` can select an existing Node executable. The broker reads `assistant/config.example.json` unless `EDIT_BUDDY_ASSISTANT_CONFIG` selects a private configuration file. `assistant/config.json` is ignored by Git. The example uses the established MSI account and SSH identity; it contains no credentials.

Tasks and per-project conversation IDs live under `%LOCALAPPDATA%\ED-IT Buddy\assistant`. Staging and preview caches are separate from original footage and INTERLINKED project files. The task ledger records execution, delivery and human review separately. Reports remain available locally if publication to INTERLINKED fails.

## Implemented source

- The WPF Manager provides project selection, MSI folder registration/indexing, visual search results, preview playback, extraction, supplied-link download jobs, date/source/tag organization, task controls, chat attachments, and Markdown/JSON report exports.
- The local broker queues work while MSI is unavailable and reconciles stable request IDs after a lost acknowledgement. UI controls and chat share task validation. The Codex adapter disables command/app/browser capabilities and rejects tool calls outside its Buddy dispatcher; real inference/tool isolation still needs acceptance.
- The independent MSI engine uses SQLite jobs and receipts, source-scoped inventories, content fingerprints, isolated tool processes, per-job downloads, verified editor-compatible outputs, copy-first organization, and resumable 60-second indexing units. Completed generated media can be reused only through a verified committed output manifest.
- Local SigLIP2 retrieval samples actual presentation timestamps, starts at two-second spacing, adapts GPU batches on memory exhaustion, groups results, and produces bounded preview proxies. The six-hour test uses synthetic timestamps and fake inference; it is not a real footage or GPU benchmark.
- The INTERLINKED contribution adds scoped same-user discovery, additive ingestion through the active lock owner, durable delivery receipts and a separate project-report channel. Its candidate overlay is not merged or deployed into the shared checkout or installed kit.

## Actual verification

Verification passed: 28 Node broker/contract tests (including a real disposable Python RPC/SQLite engine), 22 media-worker tests, 32 WPF protocol/layout checks, 29 INTERLINKED companion tests and 18 report-renderer checks. The existing companion state suite also passed. The desktop layout was rendered and inspected without launching MAGIX. The companion fixtures use actual native locks/HTTP/ACL checks with synthetic project data; they do not establish a live MAGIX session.

The real Codex smoke attempt exited before an authentication reply. No successful streamed reply, live Buddy tool call, or attached-frame inference is claimed.

The earlier MSI runtime setup completed successfully at `C:\Users\jlewi\AppData\Local\ED-IT-Buddy-Media`. It installed separate Python 3.12.10, PyTorch 2.8.0+cu126, Transformers 4.57.1, Pillow 11.3.0 and NumPy 2.2.6. Model `google/siglip2-base-patch16-224` was downloaded at revision `75de2d55ec2d0b4efc50b3e9ad70dba96a7b2fa2`. Installation logs and package reports remain on MSI. This establishes installation, not CUDA inference, worker deployment, or performance. Existing downloader scripts and INTERLINKED's sealed runtime were not changed.

## Remaining release gates

1. Establish a supported Codex connection and verify streaming, one allowed task tool, explicit frame input and refusal of unrelated tools in the actual application context.
2. Review the INTERLINKED contribution with the shared app owner, update the current distribution/GUI seals, and deploy between sessions. Preserve concurrent MASTER/cooler work and historical pinned components.
3. Deploy a fresh media-worker version, run the scheduled-user GPU preflight, and retain its receipt before enabling library indexing. `tools/Deploy-MsiMediaWorker.ps1` prepares this operation; it has been syntax-checked but not executed.
4. Select actual MSI footage sources and evaluate more than five hours of real recordings and labeled visual queries. Record speed, memory use and retrieval usefulness.
5. Test one selected URL against the actual source site, preview/extract its output, and deliver generated media during a disposable managed MAGIX session. Then complete the session and verify explicit INTERLINKED save-back.

The stopped MSI MAGIX timeline-automation experiment remains stopped. This preview does not depend on that experiment or claim automatic timeline editing.

## Implementation handoff

`package-assistant.ps1` creates an allowlisted portable preview with SHA-256 package hashes and `release_verified: false`. No personal task state, keys, media, models or source catalogs are packaged. Generated INTERLINKED candidate snapshots are ignored by Git because they contain the shared checkout's concurrent source. The reviewed extension, report component, overlay generator, hash manifest and tests are versioned; regenerate the candidate against the actual shared checkout before merging.

Files added or changed for this implementation:

- Desktop: `src/App.cs`, `src/AssistantView.cs`, `src/AssistantProcessClient.cs`.
- Local broker: `assistant/client.mjs`, `assistant/codex.mjs`, `assistant/interlinked.mjs`, `assistant/remote.mjs`, `assistant/state.mjs`, `assistant/config.example.json`, `assistant/package.json`.
- Broker verification: `assistant/test/client.test.mjs`, `assistant/test/rpc.test.mjs`, `assistant/test/interlinked.test.mjs`.
- MSI worker: `media_worker/__init__.py`, `media_worker/core.py`, `media_worker/jobs.py`, `media_worker/vision.py`, `media_worker/rpc.py`, `media_worker/worker.py`, `media_worker/config.example.json`, `media_worker/README.md`, `media_worker/runtime-installation.json`, `media_worker/requirements.installation.txt`.
- INTERLINKED contribution: `integrations/interlinked/interlinked_buddy.py`, `prepare_candidate.py`, `source-overlay.json`, `buddy_report_panel.html`, `buddy_report_style.css`, `buddy_report_ui.js`, `test_buddy.py`, `test_report_ui.cjs`, and `README.md` within that directory.
- Build/setup: `package-assistant.ps1`, `tools/Initialize-MsiMediaRuntime.ps1`, `tools/Deploy-MsiMediaWorker.ps1`.
- Desktop/worker verification: `tests/AssistantProcessClientTests.cs`, `tests/run-assistant-client-tests.ps1`, `tests/run-companion-state-tests.ps1`, `tests/test_media_worker.py`.
- Documentation and ignore rules: `README.md`, `.gitignore`, this status document, and the related band-options/integration/downloader-discovery notes.

The existing changes to `PortalMain`, `PortalMasterStatus`, MAGIX ownership and portal validation belong to earlier work and are not part of this implementation's GitHub commit.
