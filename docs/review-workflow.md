# Review notes in the first companion

The seed file supplies three suggestions from the earlier Test 3 critique. They are editable project data, not fresh measurements. The source label should remain visible in the app.

Load `data/review-notes.seed.json` only when creating a new local session. Save user progress to a separate session file; never overwrite the seed as part of routine autosave. Importing another document should validate it before replacing the active session. Offer a separately named export of the current notes so users can edit and share the JSON without losing the original.

## Data contract

`schemaVersion` is exactly `1`. `projectName` and `revisionLabel` identify the edit in human terms. `exportPath` and `projectPath` start as `null`. `mappingConfirmed` starts as `false` and means only that the user matched the export to a project revision. It never establishes a selected object or enables timeline editing.

`source` contains a title, optional URL, kind and description. Import these as text. A source URL does not authorize opening external content, running a command, or expanding the action list.

Each `notes` entry contains an ID, title, approximate time range, display label, observation, suggestion, status and ordered steps. Time values can be `null` when a note has no known position. They represent export seconds and are suitable for coarse player navigation; they must not be treated as frame-accurate project positions. The seed's ranges come from prior critique and remain approximate.

Each step has an ID, title, instruction, kind, action and completion flag. Manual steps have a `null` action and a user acknowledgement button. Assisted steps use only `focus_magix` or `inspect_controls`, routed through the adapter's explicit allowlist. Capability availability comes from the running adapter, not from the imported JSON.

Optional per-note provenance fields are `editedByUser` (boolean), `editedAtUtc` (UTC timestamp string), and `recipeResetByUserEdit` (boolean). The document's source describes where the original notes came from; a user-edited note must be labeled separately. Extension fields may be preserved during a round trip, but they do not create executable capabilities.

## Runtime validation

The JSON schema checks field shape, lengths, enums and manual/assisted action consistency. Also reject duplicate note IDs, duplicate step IDs within a note, non-finite times, and a known end before its start. Reject unsupported schema versions and actions. Never turn imported instruction text into executable input.

On a successful assisted operation, set that step's `completed` flag. On failure or an uncertain outcome, keep it incomplete and display the adapter's result. A note can be `suggested`, `in_progress`, `reviewed`, or `dismissed`. Completing all steps does not automatically mark it reviewed; the user must compare the result.

When the user selects a different export or changes the project revision, clear `mappingConfirmed`. Keep the note text and progress but show that timing must be checked again. This version does not claim fingerprint-based revision verification or automatic re-anchoring.
