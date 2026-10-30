# MSI video downloader integration baseline

Read-only inspection on October 30, 2026 located the existing Codex-created downloader at `C:\Users\jlewi\Documents\VideoDownloader` on MSI. Its README names it **Lewis's Video Downloader**. The inspection read source, tool-install metadata, and historical result summaries; it did not start downloads, conversions, cleanup, or MAGIX.

## Existing workflow

`Download Videos.bat` launches `Download-Videos.ps1`. The script accepts `-UrlsFile`, `-RepairFile`, and `-CleanSources`. Ordinary downloads read one individual video/post URL per line, reject supported-site collection links, skip live streams, and select at most the first entry of multi-video posts. Site extraction can fail; current site availability was not tested.

Portable tools under `Tools` include yt-dlp, FFmpeg, FFprobe, and Deno. The recorded tool versions are installation metadata, not a fresh binary or update check. No global tool installation is needed by the existing workflow.

Successful output is kept under `Footage\<site>`. `Convert-ForEditor.ps1` keeps compatible H.264/AAC-LC MP4s or creates a paired `_EDIT.mp4`; compatible streams are copied and other streams are converted. A silent source remains silent. The converter checks output streams, dimensions, duration, and full output decoding before accepting a newly converted file.

The workflow retains source `.info.json`, `.description`, and `.url` sidecars. Converted files also have `.processing.json` receipts describing the source/output relationship and checks. Source metadata identifies where footage came from; it does not establish permission to reuse it.

## Existing status and duplicate handling

- `Logs\<run>-results.csv` contains `URL`, `Status`, and `Details`. Statuses are `Downloaded`, `Skipped`, and `Failed`; completed file paths are currently joined inside the human-readable `Details` field.
- Timestamped text logs hold tool output and errors.
- `State\download-archive.txt` prevents repeat downloads. New archive entries are committed after final local processing succeeds.
- An exclusive `State\download.lock` prevents concurrent launchers from racing on shared files and archive state.

The latest result CSV observed was `2026-10-06_21-19-01-957-results.csv`: eight rows, comprising three Downloaded, two Skipped, and three Failed. These are historical records, not current source availability or independent output verification. Five MP4 files were present under Footage during discovery.

## Changes needed for Buddy

The main script derives Footage, Logs, State, and Tools from its own directory. It does not currently accept a project identity, an independent output root, or a stable external request ID. It exposes no structured progress events, cancellation contract, INTERLINKED ingestion call, or task ledger.

The default download and repair paths call the converter with `-DeleteSource`. The launcher also runs `Clean-CompletedSources.ps1` against the shared Footage tree after a batch. This existing behavior must remain separate from a new Buddy job: do not invoke the launcher unattended against unrelated existing footage, and do not apply its source-removal rules to original band recordings or INTERLINKED library files.

A Buddy adapter should run the reusable downloader/converter logic within an owned staging job, keeping tool binaries separate from job data. It needs structured per-asset results, a stable request/job identity, explicit cleanup policy, and cancellation that distinguishes interrupted downloads from verified outputs. Existing source files and archive records should remain intact while this mode is developed.

Only verified final assets and their relevant sidecars should be offered to INTERLINKED through its owned workspace lifecycle. A successful download does not establish that a file has been copied into a selected project, saved to MSI, or imported into MAGIX. The [integration plan](interlinked-integration.md) defines those separate boundaries.
