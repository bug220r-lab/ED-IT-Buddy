# Media inbox — 0.4.1 preview

The new Media inbox accepts selected local videos and watches explicitly chosen
folders. It retains an independent full copy on MSI, then queues visual indexing.
Storage, searchability, project delivery and project save-back have separate receipts.
This source implementation has isolated verification; MSI deployment and real-media
acceptance remain pending. Do not interpret package creation as installation.

## Use

Open the new package's `bin/ED-IT Buddy.exe`. Choose **Send videos to MSI…** or
**Watch folder…** in Media inbox. A project and Codex sign-in are optional. Initial
watch contents are skipped. New or changed videos are discovered while Buddy runs,
including a reconciliation scan when it next opens. Subfolders are opt-in. The
inbox reconnects to MSI independently of chat after you opt into sending or watching.
Other Manager services retain their explicit Connect action.

The indexing status bar stays above the Manager tabs and tracks existing MSI jobs.
It shows the current file, file-based progress and review counts. Offline and stale
observations are labelled as last known progress. The percentage represents file
progress, rather than elapsed time or verified coverage of the full library.

New candidates need three observations over at least 30 seconds. A native read
handle excludes writers/deletion throughout hashing and transfer. An active writer,
linked path, cloud placeholder or unavailable file remains waiting with an error;
Buddy does not hydrate or relocate sources. Fully cloud-managed reparse paths can
also be refused by the existing ordinary-path guards; use an available ordinary
local folder. Original videos are never deleted or overwritten.

Minimizing keeps monitoring running and shows a tray indicator. Closing exits it;
there is no service or Windows startup registration. Pause/remove watches in the
inbox. Removing a watch retains prior tasks and MSI copies. Pause/cancel transfer
controls apply at checkpoints and do not undo a committed copy.

After storage, **Assign to project** changes an MSI association only. **Add to
project** separately downloads a verified copy into private staging and uses
INTERLINKED's guarded ingestion. Save and close MAGIX, finish its managed session,
then use INTERLINKED Save + verify when a project version is needed. No save-back
or cache release is automatic.

## Storage and recovery

The default MSI archive is `C:\Users\jlewi\Documents\ED-IT Buddy Media Library`.
It must be an ordinary physical directory outside cloud, worker state and managed
project caches. The archive has an MSI/user ownership marker. `Incoming` contains
incomplete uploads, `Media` contains committed content-addressed full copies,
`Receipts` contains per-upload provenance and commit evidence, and `Associations`
contains project links. Only Media is registered as an indexing source. Broad
parent-folder scans also exclude the archive's uncommitted and metadata subtrees.

Transfers use 64 MiB chunks over the existing host-key-pinned native SFTP route.
Per-chunk verification and full-file assembly/readback run in durable worker jobs.
One local chunk is staged at a time. Resume reconciles stable request IDs and
verified chunk metadata; final assembly rechecks every chunk. Space reservation
includes uploaded chunks, assembly and other incomplete sessions. After commit,
redundant upload chunks are removed; committed media and receipts remain.

Duplicates share a verified archived object while retaining source occurrences.
Different content with the same filename remains distinct. A failed index leaves
the stored bytes intact; Retry indexing reuses them. Receipts and associations
remain readable if the worker database is lost; the commit operation can rebuild
source aliases and queue indexing again. The archive itself still needs its own
independent backup policy.

The existing task ledger format and SQLite tables are retained. New watch/inbox
fields and new job kinds are additive. Library jobs carry explicit library scope
and do not fabricate INTERLINKED project IDs. Earlier workers display an upgrade
requirement. The new package targets an immutable v0.4.1 worker and its separate
configuration snapshot; existing v0.3.2 files/configuration remain intact.

## Rollout gates

1. Coordinate with the band-footage recovery owner. Do not stop, replace or create
   competing MSI workers while that work is active. Preserve its originals,
   diagnostics, jobs and v0.3.2 baseline. MSI SSH timed out during this implementation.
2. With MSI reachable and its worker idle/stopped by its owner, copy this complete
   hash-verified package to an ordinary MSI folder. Run `tools/Install-Media-Inbox.ps1`
   normally on MSI to stage the immutable worker. `-Activate` additionally runs a
   fresh scheduled-user GPU preflight and starts the worker; it refuses live old
   workers or unfinished jobs and never stops them itself.
3. Publish the changed INTERLINKED report reducer/UI through the existing
   `integrations/interlinked/prepare_candidate.py` and guarded publisher, between
   managed sessions. Do not overlay the installed kit or refresh unrelated seals.
4. Launch the new Buddy package separately. Verify one ordinary local video against
   an independent MSI hash, interrupt/resume a transfer, complete actual CUDA
   indexing and inspect a useful visual-search result. Then assign it and separately
   deliver into a disposable managed project; verify project bytes are preserved
   and save-back did not occur automatically.

Until these gates pass, Stored on MSI, Searchable and Delivered claims apply only
to the explicitly identified isolated fixtures, not production user footage.

## Isolated verification

October 30, 2026: the broker suite includes a native Windows read-lock test and a
64 MiB-plus-tail transfer interrupted after its first verified chunk, resumed
against the actual archive engine, then indexed with synthetic media tools and
embeddings. This verifies byte preservation and queue/receipt integration; it is
not a real-media decode, SFTP-to-MSI or CUDA acceptance result.

Worker fixtures cover content deduplication, changed/corrupt chunks, disk space,
ownership, SQLite loss, separate indexing failure/retry and search. WPF tests
render the inbox offscreen and keep stored/indexing/delivery outcomes distinct.
The existing media-worker, MAGIX-proxy handling and native INTERLINKED companion
regressions also pass. Loopback/hardlink companion fixtures require a normal
Windows process; sandbox restrictions are not application acceptance evidence.

Test work also corrected a cancellation race: a job-owned media process is now
reaped after its Windows job is closed, before logs close and cancellation is
reported. Existing regression coverage exercises the real native cancellation.
