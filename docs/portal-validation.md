# Portal validation record

This record separates automated checks from observations in the real MSI desktop. It records the test results reported on October 29, 2026; it does not certify every later source edit or deployed binary. The editing demonstration is complete only after the saved MAGIX project reopens and its exported video passes independent verification.

## Automated results

| Suite | Reported result | What it exercises | What it does not establish |
| --- | --- | --- | --- |
| `tests/run-portal-tests.ps1` | 88 assertions passed | Real coordinator and receipt persistence; typed arguments and path containment; duplicate request IDs; Stop, disconnect, and lease expiry; batch receipts and native-proof requirement; real local named pipes, slow state reads, capacity exhaustion, idle expiry, EOF, and reconnects | Correct native timeline edits, actual desktop locking, or real MAGIX control behavior |
| `tests/run-portal-runner-tests.ps1` | 21 assertions passed | Real action runner with a disposable fake worker; durable intent before input permission; receipt write failure; cancellation between persistence and permission; Stop after simulated input; duplicate/forged/out-of-order protocol; worker cleanup after normal exit and abrupt parent termination | That a MAGIX control accepted input, or that MAGIX's internal export can be cancelled |
| `tests/run-portal-bridge-tests.ps1` | 9 assertions passed | Real bridge relay in a child process with a local fake pipe; roundtrip; host EOF while stdin remains open; stdin EOF while host remains open | The exact stdin handle behavior of Windows OpenSSH on MSI; that requires a separate real SSH probe |
| `tests/run-magix-editing-tests.ps1` | 6 test cases passed | Unsupported capability gates; raw/unknown/extra argument rejection; bounded paths; integer half-open frame ranges; timecode boundaries; JobObject termination of the owned worker while an unrelated fixture survives | Live validation of any editing capability |
| `tests/run-magix-ownership-tests.ps1` | 45 assertions passed | First-save provenance; exact path/hash/process binding; verified unique backups; read-only source validation; externally modified/missing/unowned files; changed process identity; stale and reused save tickets; receipt/backup changes; explicit calibration hash adoption; actual local junction rejection | That the caller's native Save or Save As succeeded; native success must be independently observed before committing a receipt |

These counts use the suites' own reporting units; six editing cases contain multiple checks. The fake-worker “input” is a file marker in a test directory. It never calls Windows input APIs, opens MAGIX, or accesses MSI.

Run named-pipe and process-lifetime tests in a normal Windows process. Restricted process namespaces can remap named pipes and produce a connection failure unrelated to the portal protocol. Each runner creates a fresh `.build` directory. A later source or runtime change needs the relevant suite rerun; an earlier pass is not evidence for an unspecified build.

## Safety behavior and evidence limits

| Condition | Implemented check | Automated evidence | Remaining live evidence |
| --- | --- | --- | --- |
| Owner disconnect or heartbeat loss | Coordinator stops later steps, marks input already begun uncertain, and does not replay | Core lease/disconnect tests; real pipe EOF and idle expiry tests | Interrupt a real disposable edit and inspect MAGIX and its durable receipt |
| Stop while an action is running | Runner stops its disposable worker; MAGIX remains outside its JobObject | Runner Stop fixture; owned/unrelated process lifetime fixture | Stop a real disposable edit; separately observe an export already underway |
| Parent process exits after input | OS JobObject closes and terminates the owned worker | Abrupt-parent fixture exits without managed cleanup and verifies worker termination | Native MAGIX work already dispatched may continue; inspect the saved state before another edit |
| Locked or unavailable desktop | `VerifyProcess` requires MSI, the target's nonzero interactive session, active console session, and input desktop `Default` | No deliberate locked-desktop fault test yet | Lock MSI during a disposable job and verify no subsequent input or replay |
| Process identity changes | PID, start time, session, executable path, and supported version are checked before input | Protocol and policy tests do not replace process-identity fault injection | Deliberately substitute a stale target snapshot without operating on a user project |
| Window becomes invalid or blocked | `VerifyWindow` requires an existing visible enabled window with the expected PID; dialogs must belong to the selected editor, with an explicit owned-menu check | No synthetic stale-HWND test yet | Close/replace the intended test dialog and verify the action stops |
| Unexpected dialog or ambiguous control | Fixed adapters use expected windows; a control must resolve uniquely by the supported semantic identity | Unsupported commands are rejected; actual dialog interception has not been injected | Present an unexpected dialog during a disposable sequence and inspect the result |
| Control changes while input permission is pending | Invoke/Value targets are resolved again after permission; window checks repeat; coordinate clicks recheck bounds, foreground, and hit target | Runner tests permission ordering, not a changing UI tree | Change a test control or focus during the permit wait and verify no stale input is issued |
| Timeout after native input | Result is uncertain; durable job/request identity prevents automatic replay | Core persistence/restart tests and runner post-input interruption fixture | Inspect a timed-out native operation and independently establish its actual outcome |

The UI checks live in `src/MagixEditingUi.cs`; worker permission and lifetime checks live in `src/MagixEditingRunner.cs`, `src/MagixEditingWorker.cs`, and `src/MagixEditingLifetime.cs`. Checks run immediately before input, but a desktop is concurrent: they do not make a multi-input editing action atomic. Stop prevents later work and never promises to undo dispatched input.

The live adapter is being developed while these tests run. No guard extraction was performed during active menu work, and no laptop lock or unexpected-dialog test is claimed. A successful UI Automation call alone is not native outcome evidence: observed provider behavior must be confirmed by MAGIX state or saved/exported artifacts.

Owned-project receipts and backups remain under each disposable workspace's `.buddy` directory, outside versioned application packages. Updates require the same saved path, content hash, and MAGIX PID/start time/session/executable identity. `PrepareUpdate` retains and verifies a unique backup before it returns; `RecheckBeforeSave` checks again after native dialog work. `CommitVerifiedSave` atomically records the observed saved file only after the adapter establishes native success. An externally changed file or a MAGIX restart does not silently inherit overwrite permission. Read-only validation can prove an owned source before Save As to a fresh destination; it creates no backup or receipt. Explicit development adoption requires a separately supplied matching full SHA256 and cannot replace an existing receipt.

## Required provenance for a promotable result

Keep one result report next to the disposable project and export. Include these fields rather than relying on a package version label alone:

| Field | Required content |
| --- | --- |
| `run_id`, `started_utc`, `finished_utc` | Unique validation run and timestamps |
| `source_revision`, `source_dirty`, `source_sha256` | Git revision when available, whether local changes exist, and hashes of every compiled source plus the build/test scripts; record unknown explicitly |
| `build_directory`, `binary_manifest_sha256`, `binary_sha256` | Exact build path and hashes of deployed host, bridge, worker, and configurations |
| `node_version`, `dependency_lock_sha256`, `adapter_sha256` | MCP runtime, pinned dependency lock, and adapter/transport files |
| `host`, `account_sid`, `interactive_session_id` | Actual execution identity; expected host is MSI |
| `magix_executable`, `magix_version`, `magix_sha256` | Normal installed MAGIX build under test; no copied-installation substitution |
| `host_process_id`, `host_start_utc`, `magix_process_id`, `magix_start_utc` | Process identities corresponding to the observed run |
| `request_id`, `job_id`, `workspace`, `steps` | Exact submitted sequence and dedicated disposable workspace |
| `tests` | Suite command, exit status, counts, captured output path, and source/build association |
| `capability_evidence` | Per-operation native observation, artifact hashes, and supported execution mode; preparation alone is not success |
| `fault_tests` | Each injected condition, expected result, observed receipt, and outcome; use `not_run` when appropriate |
| `verification` | Independent video decoder/version, frame-count report, boundary/title observations, saved-project reopen evidence, and output hashes |

Existing automated result counts above were reported during development without a complete source-hash manifest. Do not retroactively assign them to a release manifest. Produce a fresh record for the release candidate and preserve its logs.

## Demonstration acceptance

Use the generated source clips in the Buddy workspace. At 30 fps, the video must contain clip A source frames `[0,60)`, then clip B source frames `[30,120)`. The title `ED-IT Buddy` must appear on output frames `[0,60)` and be absent from frame 60 onward. Save a new project and export a five-second MP4.

Decode the whole MP4 independently and confirm exactly 150 video frames, frame rate, clip order, trim boundaries, and title appearance/disappearance. Check representative frames at and around both source transitions and the title boundary; metadata duration alone cannot establish these properties. Reopen the saved project in the normal MSI installation. Record the project and MP4 hashes, then copy the verified MP4 and report to THIS-MACHINE. Connection, project creation, import, or a preparatory EDL file is an intermediate result.
