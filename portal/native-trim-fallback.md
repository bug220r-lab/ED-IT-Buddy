# Native trim fallback research

Evidence is the cached Steam Movie Edit Pro 2021 manual at `.build/research/manual/movie-edit-pro-2021-steam.pdf` and the adjacent `.txt`. Shortcuts require checking against MSI's current installation and focus state.

| Manual pages | Text lines | Documented behavior |
| --- | --- | --- |
| 106–107 | 3464–3534 | Preview an original Media Pool video, set source range with I/O, and apply that range using an insert mode. |
| 66 | 2150–2154 | Automatic application (`1`) appends a video or image selection after the last object on track 1. |
| 66 | 2156–2168 | Single-track ripple (`2`) inserts at the playback marker on the selected track and shifts later objects. |
| 117–118 | 3897–3905 | The monitor time display allows entering Hour:Minute:Second:Frame and moves the playback marker. |
| 110–111 | 3620–3663 | Object trimmer (`Shift+N`) changes lower trim handles, timeline position and source content. Arrow buttons step one frame; Ctrl changes the increment to five frames. |
| 315 | 10545–10551 | Home goes to start; left/right arrows step one frame; Ctrl+arrows step five. |
| 317 | 10610–10613 | Ctrl+W/Q selects the next/previous object. |
| 318 | 10636–10641 | Insert-mode shortcut table confirms `1` through `5`. |

## Recommended native trial

Use only the new disposable project, with the actual original `Clip-A.avi` and `Clip-B.avi` in its workspace.

1. Open A in the source preview and verify its identity. Set its source range from frame 0 to the boundary at frame 60, then use automatic application to append it to the empty first track.
2. Verify the timeline has exactly one object, duration 60 frames and first/last displayed source frames 0/59.
3. Open B in the source preview and verify identity. Select source frames 30 through the boundary at 120, then append using automatic application.
4. Verify the second object starts at timeline frame 60, lasts 90 frames, and displays source frames 30/119 at its boundaries. Total duration must be 150 frames.
5. Use Object trimmer to inspect or correct individual boundaries only after confirming its selection and live control mappings. Re-resolve each control before acting.

At 30 fps the intended boundaries are A `00:00:00:00`–`00:00:02:00` and B `00:00:01:00`–`00:00:04:00`. The manual does not establish whether an Out marker includes the displayed frame. Do not assume that convention: prove it from duration and first/last source-frame displays, then independent render verification. Do not publish `trim_clip` as supported from keyboard input alone.

If the typed import/trim/append operations are compiled as a batch, their receipts must remain one shared native operation until timeline/native evidence verifies the result. A prepared preview range or generated plan must not be recorded as a completed timeline edit.

The trimmer manual labels position and content controls but does not promise editable numeric start, end or duration fields. Those must be discovered live, not invented from the documentation. The source-preview route is documented more directly and avoids assuming custom timeline coordinates.
