# INTERLINKED Buddy companion contribution

This is the Buddy companion source contribution and its guarded publication tooling. Publication receipts and live-test evidence stay in the Buddy workspace's ignored `.build` directory. `publish_current.py` merges only hash-checked current source and publishes the installed kit only while the existing GUI/workflow locks are unheld. It preserves MASTER, the frozen workspace base, historical launcher source, and existing project files. Never overlay a running installation.

Generated `candidate/` snapshots are intentionally ignored by Git: they incorporate the shared checkout's unrelated concurrent source. Run the versioned overlay generator against the actual checkout to reproduce them; merge the contribution with its owner rather than publishing a copied snapshot as Buddy source.

`interlinked_buddy.py` implements a native-client capability, project discovery, staged additive file delivery, receipts, and project-work report storage. `prepare_candidate.py` reads the current external checkout and produces a hash-guarded source overlay inside `candidate`. It never publishes those files. `source-overlay.json` records the source baseline and exact proposed bytes. Regenerate it when the shared source changes, then rerun tests.

The proposed launcher is a new `repository/work/magix-buddy/candidate/magix_projects.py`. The historical `magix-all-projects` launcher, frozen `magix_workspace.py`, original client, and historical manifests remain unchanged. Publication installs the new reviewed launcher as the kit's `magix_projects.py` only between sessions, updates the GUI's intentional launcher pin, and seals the new module with GUI checksum format v5. The current distribution manifest needs the new/changed source hashes at merge time, not a reset to an older manifest.

## Companion interface

The running production GUI publishes `%LOCALAPPDATA%\INTERLINKED\buddy-companion\endpoint.json`, with `format: interlinked-buddy-endpoint-v1`, `base_url`, per-run `token`, and process ID. Its directory uses a protected Windows DACL allowing the current SID and SYSTEM only, plus a kit/user/machine ownership marker. The descriptor is removed on graceful exit only when its token still matches.

Native clients send `X-INTERLINKED-Buddy-Token`. Companion routes reject browser Origin/Sec-Fetch-Site headers and do not accept the GUI capability. Existing GUI authentication is unchanged.

- `GET /api/buddy/projects` returns stable `original_id` project identities and the active operation.
- `GET /api/buddy/project?project_id=<original ID>` returns enumerated opaque entry IDs and labels. It does not accept arbitrary executable/project paths.
- `POST /api/buddy/action` accepts `{request_id, project_id, action}` for explicit `save` or `verify`, or those fields plus `entry_id` for `open`. Only currently enumerated entries can open. These calls delegate to INTERLINKED's existing controller and locks; duplicate identical requests return the same lifecycle receipt. Release, recover, quit, and shell commands are unavailable.
- `POST /api/buddy/session` accepts `{project_id, operation_id, prompt_generation, response: "DONE"}`. Project, operation, and prompt identity must match the current session. The existing launcher verifies MAGIX is closed. Ingestion never sends DONE or saves automatically.
- `POST /api/buddy/ingest` accepts exactly `{request_id, project_id, manifest_path}`. Request IDs are 32 lowercase hex characters. It returns a queued receipt; the client polls rather than assuming delivery.
- `GET /api/buddy/operation?request_id=<id>` returns QUEUED, COPYING, COMPLETED, FAILED, UNCERTAIN, or NOT_FOUND.
- `POST /api/buddy/report` accepts exactly `{project_id, summary}` and stores a bounded report outside project Files.
- `GET /api/buddy/reports` reads those stored project reports for the native companion.
- `GET /api/buddy-report?project=<UI-ID>` uses the existing GUI token and origin gate, maps the selected UI ID to its stable original identity, and returns a reduced report. It never exposes the native companion token.

The candidate web overlay adds a selected-project Buddy report panel. It shows prepared/delivered/attention/queued counts, prioritizes attention items, retains the source observation time, and lists plain-text output references with short hashes. Project changes and delayed requests are reconciled by selection generation. Refresh only reads the local stored report; it never contacts MSI or starts work. The display keeps delivery separate from verified save-back and from the passive editor-status feed. Errors retain an explicitly historical same-project report. No report field is interpreted as HTML, a command, or an arbitrary filesystem link.

Staging is fixed to `%LOCALAPPDATA%\ED-IT Buddy\assistant\staging`. It must have a protected current-user/SYSTEM DACL and an `OWNER.json` equal to `{format: "buddy-assistant-staging-v1", sid: "<current SID>", computer: "<COMPUTERNAME>"}`. An asset manifest beneath that root is `{format: "buddy-asset-manifest-v1", request_id, project_id, files: [{path, bytes, sha256}]}`. File paths are portable relative paths beneath the manifest's parent. All assets are checked for ordinary path ancestry, non-hardlinked files, lengths, and SHA-256 hashes.

COMPLETED receipts contain request/project identity, manifest hash, final directory, per-file original relative path, project-relative path, physical path, optional mapped MAGIX path, and content hashes. They explicitly report no automatic save or release.

## Active-session ownership

The new launcher accepts an explicit `--buddy-session` operation ID. Its prompt wrapper handles only `BUDDY_INGEST <opaque request ID>`, reads the owned request, checks the selected READY cache and BOUND aliases, and performs the copy while the original workspace/cache lock contexts remain held. Existing DONE/CANCEL behavior is retained. An older running session does not acquire this capability.

Assets publish under a new `BuddyAssets/<request ID>` folder in an existing mapped media root. Existing files and project bytes are never replaced. Copying uses an owned incoming directory, full readback hashes, atomic directory publication, and a durable receipt. Lost acknowledgement after publication is reconciled by verifying the published directory. A changed source, collision, wrong session, or missing mapped root stops that request.

The GUI serializes delivery against session completion. The primary edit remains WAITING_FOR_MAGIX; a delivered asset does not complete the edit, save, or release it. Generic folder sessions use the parent's existing held locks. Idle ingestion obtains the existing workflow/cache locks.

## Validation

Run `prepare_candidate.py`, then `test_buddy.py` with a normal Windows Python process. The 45 synthetic tests cover real loopback capability checks, private endpoint discovery and inherited staging ACLs, separate report storage, GUI-token report access, UI-to-original project mapping, reduced output references and bounded report counts, enumerated lifecycle actions, duplicate lifecycle receipts, stale DONE prompts, rejected destructive actions, staging escapes, source preservation, hardlinks, source changes, collision handling, partial-copy resumption, lost acknowledgement, incorrect sessions, session-completion serialization, launcher exit uncertainty, and additive publication while two real Windows share locks stay held. They also cover exact sealed-runtime read retention for open/save/verify, hash/length/seal mismatch, path escape, hardlinks, and true native offline/recall rejection. They never launch MAGIX or read production project payloads.

Run `node integrations/interlinked/test_report_ui.cjs` from the Buddy workspace for 18 synthetic DOM/model checks. They cover safe text rendering, separate outcome counts, bounded references, failed-refresh history, delayed project selections, local GET-only refresh, and the generated HTML/JavaScript hooks. `node --check` also validates the complete generated app script. These checks do not launch a browser or establish visual acceptance of a deployed panel.

The restricted shell can block loopback connections and hardlink fixture creation. A normal namespace test run is needed for those cases. Actual live acceptance evidence is stored separately in ignored `.build/live-interlinked-acceptance.json`: generated media was delivered while the disposable managed MVP stayed open, followed by native Save, normal close/DONE, explicit save-back and independent verification. Those receipts establish the tested installation and generated test assets; they do not establish automatic timeline editing or representative band-search quality.

The new launcher retains ordinary, native-attribute-checked read handles for the exact immutable 52-file client checksum set throughout every action. Each file is checked against the existing accepted seal, length and SHA-256; true native offline, reparse or recall flags still fail before reading. This prepares available runtime bytes and prevents dehydration during the unchanged frozen launcher guard. It does not change runtime bytes, ACLs, file availability settings or version pins, and never applies to source media. A genuinely unavailable runtime remains a visible failure requiring environment repair.
