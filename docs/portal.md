# ED-IT Buddy MSI portal

The portal lets this Codex chat submit semantic editing requests to Buddy running beside MAGIX on MSI. THIS-MACHINE runs the MCP adapter and a status/Stop window; it does not receive editing keyboard or mouse input. Whiteboard development is separate and paused.

The portal connection and editing capability are separate results. A working connection does not establish that trimming, title creation, saving, or export has passed live validation. Query `get_capabilities` for the running build. The end-to-end milestone remains pending until a saved project and a five-second, 150-frame export pass independent verification.

## Runtime layout

The portable package contains:

- `windows/ED-IT Buddy.Portal.exe`: remote dashboard by default; `--host` enables the interactive MSI worker host.
- `windows/ED-IT Buddy.Bridge.exe`: fixed SSH-to-local-pipe bridge, restricted to MSI, with no command-line editing actions.
- `windows/ED-IT Buddy.EditorWorker.exe`: bounded action/probe subprocess.
- `portal/`: MCP server, persistent dashboard client, configuration examples, pinned production dependencies, and third-party license files.
- `tools/deploy-portal.ps1`: this user's fixed, strict-host-checked MSI deployment helper.
- `package-manifest.json`: hashes of package members. No private key, source footage, job receipt, personal configuration override, or MAGIX binary is packaged.

Windows x64 and .NET Framework 4.8 are required. The local adapter requires an installed Node runtime at least 22.13; Node is not bundled. The source workspace uses the existing Codex Node runtime. Set `EDIT_BUDDY_NODE` to another installed Node executable if needed.

The host keeps mutable state outside version folders:

```text
C:\Users\jlewi\AppData\Local\ED-IT Buddy\portal\
  v0.2.0\               Host, bridge, editor worker, configs, binary manifest
  state\endpoint.json   Current host process, account, and session identity
  state\jobs\           Durable receipts; retain these across upgrades
  workspaces\demo-*\    Disposable demo media, projects, and exports
```

## Build and deployment

From the source workspace, `build-portal.ps1 -OutputDirectory .build\portal-<unique-version>` creates a fresh build. It refuses to overwrite an existing build. `package-portal.ps1 -PackageName <new-name>` builds and packages the Windows components plus the three pinned production Node packages. Use `-BuildDirectory <existing-build>` to package an already validated build. The packager checks binary hashes and dependency versions, rejects filesystem links in dependency contents, and tests that the packaged MCP dependencies load. It does not contact MSI.

Restore development dependencies, when necessary, from `portal` with `pnpm install --frozen-lockfile --ignore-scripts`. No lifecycle scripts are required.

Deployment is scoped to the already configured MSI account and SSH identity. It does not change SSH, firewall settings, MAGIX binaries, Steam licensing, or existing projects. From the source workspace:

```powershell
.\tools\deploy-portal.ps1 -BuildDirectory '.build\portal-<unique-version>' -Version 'v0.2.0'
```

For an extracted portable package, use `-BuildDirectory 'windows'`. The target version folder and task must not already exist; choose a fresh version for an upgrade. Keep MSI awake and `MSI\jlewi` logged in with the desktop unlocked. Steam/MAGIX first-run or authentication prompts need to be completed in that desktop.

The helper copies only the explicit runtime files, verifies their manifest hashes, then creates and starts the dedicated on-demand task `ED-IT Buddy Portal v0.2.0`. Its action is the versioned `ED-IT Buddy.Portal.exe --host`, principal is `MSI\jlewi`, logon type is Interactive, and run level is Limited. It stores no password, adds no login trigger, and does not run as SYSTEM. Do not reuse the earlier copied-MAGIX launcher or its guard scripts.

Only one portal host may run in a desktop session. Close the existing Buddy host normally before starting a newer version. Do not terminate MAGIX to upgrade Buddy. Point `remoteBridge` in the adapter configuration to the new version only after its host is ready.

## Connect and operate

The configuration example is specific to this installation: `jlewi@192.168.247.1`, the dedicated key outside the workspace, explicit `known_hosts`, and `HostKeyAlias=msi`. Keep strict host checking and batch authentication enabled. No port forwarding is needed. Create a separate local configuration override if paths differ, and set `EDIT_BUDDY_PORTAL_CONFIG` to its absolute path. Never include private-key contents or passwords in configuration or requests.

Merge only the named `[mcp_servers.edit_buddy]` entry from `portal/codex-config.example.toml` into the trusted project's `.codex/config.toml`, updating absolute package paths as necessary. Preserve other servers and approval settings. Reload/restart MCP servers and check the tool list. This uses the current Codex chat; it does not create a second AI service or require another model API key.

Open `windows/ED-IT Buddy.Portal.exe` without `--host` on THIS-MACHINE for the remote status/Stop dashboard. Its parent-directory search finds the sibling `portal/client.mjs`; `EDIT_BUDDY_PORTAL_CLIENT` can supply an explicit path. The MSI host is the only component that performs editor input.

For a read-only connection check, run `node portal/client.mjs --method get_state`. For editing, keep the MCP connection alive, use `client.mjs --rpc`, or submit with `--method run_sequence --wait`. A one-shot submission cannot maintain the job lease.

The five MCP tools are `get_state`, `get_capabilities`, `run_sequence`, `get_job`, and `stop_job`. Sequence parameters are `{request_id, workspace, steps:[{action,args}]}`. Job queries use `{job_id}`. The action allowlist covers create, import, trim, append, title, save, and MP4 export. Frames are zero-based integers; trims use an exclusive out-frame. All paths must remain inside the selected MSI Buddy workspace. There is no arbitrary command, key-string, or element-ID endpoint.

The bridge gives each connection its own lease. Pings every five seconds renew the controlling connection's 15-second lease. Closing an unrelated dashboard connection does not revoke another connection's lease. The dashboard's Stop button explicitly addresses the shared job, and its Disconnect button first stops any displayed active job, including one submitted by this chat. Stop, host offline, or owner connection loss prevents later steps. An operation already in progress may finish, especially native export; Stop does not imply undo or kill MAGIX.

Request IDs and receipts prevent replay. Repeating the same request returns the original job; changing a sequence under an existing ID is rejected. Read `get_job` after a timeout. An after-input error or interrupted startup recovery is uncertain and must be inspected before any replacement operation. The worker re-resolves controls and checks outcomes; a successful button invocation alone is not a verified edit.

## Validation and removal

`tests/run-portal-tests.ps1` checks the coordinator, persistent receipts, request validation, path containment, Stop, owner disconnect, leases, and actual named-pipe roundtrips. Named-pipe integration tests must run in a normal Windows process: the restricted Codex shell remaps native pipes into an AppContainer namespace. These tests use a fake editor and never control MAGIX. `node --test test/*.test.mjs` in `portal` checks the transport and MCP adapter.

The live demonstration uses only generated clips and a new Buddy workspace: A frames `[0,60)`, B frames `[30,120)`, in that order at 30 fps, with an “ED-IT Buddy” title over the first 60 output frames. Completion requires the saved project to reopen and the MP4 to decode as 150 frames with the expected clip boundaries and title timing. Existing footage transfers and INTERLINKED project saves are excluded.

To disconnect, use the dashboard's Disconnect button or put the MSI host offline. To remove the portal, first Stop any job and close Buddy normally; preserve the project and receipts. Remove only the `edit_buddy` MCP configuration entry and the exact dedicated task for the installed version. Confirm the task action points into the expected `%LOCALAPPDATA%\ED-IT Buddy\portal\<version>` folder before unregistering it. The temporary development task `ED-IT Buddy Portal Probe 020`, if still present, has its own ownership description and should be removed only after confirming it is this portal's probe.

No blanket deletion command is provided. Before removing a version folder, resolve its absolute path, verify that its parent is exactly `%LOCALAPPDATA%\ED-IT Buddy\portal`, confirm no portal process is using it, and use that checked literal path. Retain `state`, `workspaces`, normal MAGIX folders, SSH credentials, and unrelated tasks. A version rollback changes only the host/bridge version and its dedicated task; job receipts remain shared to prevent duplicate edits.
