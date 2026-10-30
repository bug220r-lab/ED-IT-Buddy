# ED-IT Buddy portal transport

This local Node process exposes five MCP tools and sends typed requests over the existing SSH connection to Buddy's interactive worker on MSI. It never sends keyboard or mouse input on THIS-MACHINE.

## Components

- `server.mjs`: official MCP SDK server for Codex.
- `client.mjs --rpc`: equivalent newline JSON bridge for Buddy's local WPF status/Stop interface.
- `transport.mjs`: strict-host-checked persistent SSH transport, heartbeats, request deadlines, and no automatic mutation replay.
- `schemas.mjs`: allowlisted action arguments and frame conventions.
- `config.example.json`: this installation's endpoint and fixed remote bridge executable.
- `codex-config.example.toml`: project MCP registration fragment; it does not change other servers or approval settings.

The adapter uses pinned `@modelcontextprotocol/server` 2.4.0 and Zod 4.6.7. Node 22.13 or newer is required; this machine's installed runtime is Node 24.19.0. `pnpm-lock.yaml` retains package integrity values. Dependencies can be restored with `pnpm install --frozen-lockfile --ignore-scripts`.

Set `EDIT_BUDDY_PORTAL_CONFIG` to an absolute JSON configuration path to override the example. Keep private keys outside the repository. Only the fixed trusted executable path is placed in the encoded PowerShell launch command; every job field travels separately as JSON on stdin. Password prompts and unknown host keys fail rather than becoming interactive prompts.

## Shared protocol

Each request and response occupies one UTF-8 line:

```json
{"id":"status-1","method":"get_state","params":{}}
{"id":"status-1","result":{"host":"MSI"},"error":null}
```

Methods are `get_state`, `get_capabilities`, `run_sequence`, `get_job`, `stop_job`, plus internal `ping`. Core results are returned without reinterpretation. Error responses use `{ "code": "...", "message": "..." }`; local transport errors additionally say whether an edit outcome is `uncertain`.

`run_sequence` takes `{request_id, workspace, steps:[{action,args}]}`. `get_job` and `stop_job` take `{job_id}`. Request/job IDs use letters, digits, underscore, hyphen or dot, at most 80 characters. Clip IDs use letters, digits, underscore or hyphen, at most 64 characters. Paths are limited to 240 characters, titles to 500, and frame values to 100,000,000. Project dimensions are at most 7680×4320; a schema-valid setting still requires a demonstrated worker capability. Frames are zero-based integers; trim ranges are `[source_in_frame, source_out_frame)`. Track numbers start at one. Every action path must stay inside the MSI job workspace; the worker enforces filesystem containment.

The CLI sends `ping` every five seconds while connected. The worker owns the 15-second lease and stops scheduling further steps when the owning connection disappears. Do not use a short-lived one-shot process to start jobs: use `--rpc` or `--method run_sequence --wait`. Reconnecting to read a job never resubmits it. Stop does not mean undo, and native export may continue after Stop.

Read-only readiness command:

```powershell
node .\portal\client.mjs --method get_state
```

Persistent GUI connection:

```powershell
node .\portal\client.mjs --rpc
```

Write request lines to that process's stdin and read matching response IDs from stdout. Diagnostics go exclusively to stderr. Closing stdin ends that connection. The GUI must keep the process alive while its owned job runs. Shutdown sends EOF first so the remote bridge can release its named pipe and job lease; only an SSH child that has not exited after two seconds is terminated. `shutdownGraceMs` can be configured between 100 and 5000 ms. Shutdown never terminates MAGIX.

## Validation

Run `node --test test/*.test.mjs` from this directory. Tests cover strict SSH arguments, response correlation, fragmented lines, uncertain disconnects/timeouts, reconnect without replay, remote rejection, invalid protocol output, heartbeat shutdown, graceful EOF and bounded SSH-only fallback termination, unknown methods, action schema validation, and the real official SDK initialize/tools-list flow. They use fake SSH children and never operate MAGIX.

Live transport and editing capability validation is separate. An MCP tool appearing in the list does not prove that the corresponding MAGIX editing operation works.

### Windows SSH stdin regression

The first live bridge request succeeded, while later requests timed out and remote bridge processes accumulated. A bounded echo executable reproduced the problem without Buddy or MAGIX. On MSI, .NET Framework `Console.In` and `Console.OpenStandardInput()` read the first request but not subsequent input or EOF through OpenSSH. Node overlapped pipes and Python's subprocess pipes did not change that result. Reading a `FileStream` built directly from `GetStdHandle(STD_INPUT_HANDLE)` received both delayed lines and EOF correctly. `PortalBridgeMain.cs` now uses that raw handle reader.

With that bridge fix and EOF-first local cleanup, the live read-only soak in `test/ssh-readonly-soak.mjs` passed on MSI: one SSH connection remained usable for 41 seconds, eight five-second heartbeats succeeded, and SSH exited with code 0 within 30 ms of EOF without a forced kill. The recorded report is `.build/portal-ssh-soak-20261030.json` at the project root. This proves persistent transport and cleanup, not editing capabilities.

The optional `test/ssh-native-stdin-diagnostic.mjs` deploys only its dedicated echo helper, which self-exits after five seconds. It is not part of the ordinary test suite. Do not replace its timeout with an unbounded remote console loop.
