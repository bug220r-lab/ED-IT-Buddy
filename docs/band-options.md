# ED-IT Buddy band options

This list holds possible directions for making ED-IT Buddy useful to the band. Saving an option does not select it for implementation.

On October 30, 2026, the user chose the main product direction: an editing manager and assistant for footage discovery, internet video acquisition, file management, task tracking, and reports, feeding into INTERLINKED. See the [integration plan](interlinked-integration.md). The vertical clip workflow below remains an option within that broader direction.

## Live show footage into vertical clips

Saved October 30, 2026. Status: retained for consideration; no implementation requested.

Supply a live-show recording and produce a batch of draft videos for Shorts, Reels, and TikTok. The first version would use one camera, three user-selected moments, and reusable band branding to generate three vertical drafts at 1080 × 1920, roughly 20–40 seconds each.

Proposed workflow:

1. Add the recording and, optionally, a separate audio recording.
2. Select timestamps for choruses, solos, crowd interactions, or other highlights.
3. Choose a vertical crop around a performer or preserve the full stage within a vertical background.
4. Render the selections with band and song text, adjusted audio levels, and optional checked lyric captions.
5. Review framing and timing, then render the final batch.

A dedicated FFmpeg rendering engine inside Buddy was proposed for repeatable trimming, framing, overlays, and audio processing. MAGIX could remain available for further editing. This renderer is not an existing Buddy feature.

Automatic highlight selection, tracking moving performers, synchronizing separate recordings, and editing between camera angles would require further development. Supplied lyrics should be checked before use as captions.

The first practical trial would use one real show recording to assess whether the resulting three clips are useful and how much manual correction they need. The usual footage setup—one phone, multiple cameras, or video plus soundboard audio—remains to be established.
