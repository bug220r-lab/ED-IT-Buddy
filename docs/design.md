# ED-IT Buddy

ED-IT Buddy is a proposed Windows companion for MAGIX Movie Edit Pro 2021 Steam Edition. It explains the next editing step, connects critique to the relevant part of an edit, and performs selected actions once their behavior has been verified against the installed version.

The first version should help complete one concrete loop: review an export, choose a revision, follow or run the steps in MAGIX, then compare the next export. The user chose a companion that explains steps and can perform selected actions on October 29, 2026.

**Historical proposal:** this document records the original review-companion design. A 0.1.0 review companion has since been built. The user subsequently clarified that the main product should be a whiteboard for planning and progress, with editing performed in MAGIX. See [the current whiteboard and integration investigation](integration-feasibility.md). There is still no AI service or automatic video analyzer. Design preview interactions use example data and do not send commands to MAGIX.

## What the conversations establish

| Source | Need | Product response |
|---|---|---|
| [Video Edit Critique](chatgpt-conversation://6adac757-c844-83ea-af8c-9d29035e387c) | Concrete effect feedback, readable text, less crowded transitions, comparisons between exports | Timestamped revision queue with evidence, explanation, MAGIX steps, and comparison notes |
| [Edit Black Ops Intro](chatgpt-conversation://6ac2f25b-2008-83ea-9133-af9c2578234b) | Classified-document compositions, abrupt movement, brief distortion, news and interview assets | Reusable recipes and an asset notebook that retains source context |
| [Edit Videos Magix Movie Pro](chatgpt-conversation://6abefcdd-04c4-83ea-95f4-959f8d264bac) | Help locating controls and distinguishing preview acceleration from export encoding | Help grounded in the installed edition and observed screen, plus an export check workflow |

The sample review items come from the previous critique. They are not a fresh analysis of the video. Suggestions about effect settings are experiments, not recovered MAGIX preset values. Earlier claims about GPU support must be rechecked before becoming product rules.

## The first useful experience

1. Open Buddy beside MAGIX. Buddy identifies the installation, language, active MAGIX window, and available controls.
2. Select an export and its matching project revision. Review notes remain tied to that export.
3. Choose a note such as “Simplify the news entrance,” around 00:24.2–00:24.8 in Test 3.
4. Buddy explains the intended result: preserve the title, make the RESIST insert readable, and let the news image establish itself.
5. Buddy presents one step at a time. A supported step offers **Do this step**; a step requiring judgment offers **I did this**. An unavailable control gets a visible reason and a manual instruction.
6. The selected action runs, its outcome is checked, and Buddy shows what changed. The user can stop the sequence immediately.
7. A new export is compared with the previous one. The note is marked reviewed only after a person checks the result.

Pressing **Do this step** authorizes that displayed step; it should not trigger another generic confirmation. Separate confirmation belongs only where the action has new consequences, such as replacing an existing export. Launching a long chain requires showing its full scope first.

## Interface

**Companion:** a resizable, optional always-on-top window beside MAGIX. The header identifies connection and observation status. Three destinations cover Guide, Review, and Export. The main area shows the current objective, one next step, the reason for it, and the relevant controls. Chat can be added after the structured workflow works; useful actions should not depend on composing a prompt.

**Review desk:** an expanded workspace for a local video player, frame stepping, timestamped notes, recipe details, and before/after comparisons. The conceptual preview focuses on the note-to-step interaction; it does not include a working video player.

Both views use the same project and action state. A compact view is the default while editing; the larger view is for deciding what to change. These are two views of one product.

## First release scope

| Capability | Initial behavior | Completion criterion |
|---|---|---|
| Installation and connection | Read Steam libraries, executable version and window identity; handle multiple installations | Correct executable and window identified, with a visible disconnected state |
| Screen help | Capture only the chosen MAGIX window on request; provide instructions using known controls | User can identify the target and follow the instruction without guessing |
| Review queue | Import written notes, associate them with a local export, seek the Buddy player to a note | Notes remain tied to the correct export revision and frame rate |
| Guided recipes | Show ordered steps, experimental values, and expected visible outcomes | User completes and compares one edit without searching for a tutorial |
| Selected actions | Begin with validated window activation, transport, and navigation; add effect-panel and numeric changes individually | Each supported action passes the installed-version capability checks |
| Export check | Inspect file properties; guide a representative export and record observed encoder activity separately | Report distinguishes file measurements, settings, and actual encoder evidence |

Initial recipe candidates are a short blur that resolves, a brief positional jump, text contrast, reduced ghosting, image coverage during movement, and a cleaner news reveal. Recipes must name prerequisites and relevant edition features. Exact paths and shortcuts will be filled in after validation in this installation.

Asset collection, transcript search, automatic critique, voice commands, bulk timeline assembly, and generated overlays are later additions. AI critique should distinguish measured properties, visual interpretation, and unverified audio impressions.

## Verified installation and integration boundary

Read-only inspection on October 29, 2026 found:

- Steam app ID: `1363250`.
- Manifest name: `MAGIX Video deluxe 2021 Plus Steam Edition`.
- Steam build ID: `6551777`.
- Executable: `C:\Program Files (x86)\Steam\steamapps\common\MAGIX\Video deluxe 2021 Plus Steam Edition\Videodeluxe.exe`.
- Executable file version: `20,0,1,80`.

The [Steam product listing](https://store.steampowered.com/app/1363250/) identifies the edition and links its [English manual](https://cdn.cloudflare.steamstatic.com/steam/apps/1363250/manuals/Videodeluxe_EN.pdf?t=1599654586). The web reader could not retrieve the full manual because of its size; shortcut mappings have not been verified from it.

No supported timeline scripting interface was verified during this design work. The design therefore does not depend on generating or modifying MAGIX project files. The Windows controller will first test accessibility controls, then verified keyboard commands, with screen-based targeting reserved for operations that can be checked reliably.

[Microsoft UI Automation](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-overview) provides interfaces for discovering and manipulating exposed Windows controls. That establishes a possible integration route, not proof that MAGIX exposes its custom timeline or effects controls. Live inspection is the next engineering gate.

## Proposed implementation

Use a C# WPF desktop application with a separate, narrow MAGIX adapter. This is a proposed implementation choice for a Windows-only companion, keeping window handling and automation close to the platform. Pin a supported .NET version when implementation starts.

```text
Companion window / Review desk
             |
       Project session
       /      |       \
 Review    Recipe     Action coordinator
 store     library          |
    |                       |
Local media worker     MAGIX adapter
                            |
                    UI Automation / verified input
```

- **Project session:** current export, linked project revision, notes, recipes, and observed application state. Store notes locally in a versioned JSON sidecar initially.
- **Media worker:** use FFprobe/FFmpeg for metadata, selected frame extraction, proxies, and optional audio measurements. Keep processing cancellable and check distribution requirements before packaging these dependencies.
- **Recipe library:** edition-specific procedures with evidence, prerequisites, parameters, expected outcomes, and support status. Avoid offering presets that are merely inferred from an export.
- **MAGIX adapter:** fixed, named operations. A language model proposes operations through validated data; it does not emit arbitrary shell commands or unrestricted input sequences.
- **Optional AI provider:** instructions and critique can call a configured service later. The application should work with manual notes first. A standalone app must not assume access to this chat's tools, history, or authentication. Cloud analysis needs an explicit data-sharing setup; store any credentials in Windows credential storage.

## Action execution

Each action records its target installation, process and window, prerequisite state, parameters, support status, intended result, and outcome. The coordinator follows:

```text
Planned → Ready → Running → Verified
             ↘ Needs manual step
                       Running → Stopped / Failed / Outcome uncertain
```

Revalidate focus, the current selection, and modal dialogs immediately before sending input. Do not continue when the selected object is ambiguous, the window changed, or the result cannot be established. Do not automatically retry an operation such as split or delete when its first result is uncertain. Stop cancels queued actions and releases held input; it does not promise to cancel an export already running in MAGIX.

Create a separately named project copy through MAGIX before the first editing action in a session and verify that it exists. Undo is available only for actions known to support it and only while the observed undo state is still valid. Saving files and exporting are not covered by a universal rollback promise.

Input-based automation pauses when the user changes context. The companion must distinguish **Suggested**, **Ready**, **Applied**, **Verified**, and **Needs manual step**; sending a click does not establish success.

## Time and evidence

Store frame rates as rational numbers and positions as frame indices or presentation timestamps. Treat 60 and 60000/1001 as different rates. Preserve source/export timestamps separately from project positions. Variable-frame-rate inputs need timestamp-aware handling before frame-accurate navigation is enabled.

An export timestamp does not identify a MAGIX object. Require a confirmed project mapping or a manual selection before acting on a critique note. After trims or rearrangements, invalidate affected mappings and re-anchor notes against the new revision.

Each review item should retain the export fingerprint, time range, observation, proposed change, source, confidence/verification status, and completion state. File analysis can report dimensions, frame rate, bitrate, and audio measurements; it cannot by itself prove which GPU encoded the export or recover the original effect parameters.

## Build sequence and acceptance gates

1. **Connection experiment.** Inspect MAGIX's live accessibility tree and screen state. On a disposable project, validate activation, playback, navigation, modal detection, focus loss and stop behavior. Record working controls for version 20.0.1.80, interface language, and display scaling. Proceed with automation only for operations that pass.
2. **Usable companion.** Build the window, project session, local export player, note queue, and guided/manual steps. Success: complete one Test 3 revision using the companion from note to comparison.
3. **Selected editing actions.** Add one reversible edit at a time, with a project copy, selection checks, and an outcome check. Success: an operation either produces its expected result or stops without subsequent input. Test against layout changes, multiple windows, and unexpected dialogs.
4. **Analysis and reuse.** Add measured export comparisons, optional AI interpretation, recipe reuse, and source-linked assets. Success: analysis distinguishes measurements from suggestions and sends only the data selected by the user.

The immediate implementation milestone is a small live connection experiment plus the compact companion. Broad automatic timeline editing remains dependent on that experiment.
