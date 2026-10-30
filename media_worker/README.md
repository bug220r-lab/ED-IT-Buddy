# MSI media worker

This independent Python worker implements durable media jobs for Buddy. It does not operate MAGIX or change INTERLINKED's sealed Python runtime. It must be deployed and preflighted separately; source tests do not establish GPU or site availability.

## Processes and state

`rpc.py --state <absolute-directory> --config <absolute-json>` is a UTF-8 JSON-lines bridge. Each request is `{ "id": "correlation", "method": "get_capabilities", "params": {} }`; each response has the same `id`, `result`, and `error`. It never starts work itself. `worker.py` with the same arguments owns the execution queue and continues when an SSH bridge closes. A native lock prevents two workers from executing the same queue. Run it under the scheduled MSI user context used for the GPU preflight.

State is separate from sources and tools. `OWNER.json` binds the state to machine/user, `media.sqlite3` holds durable jobs/index records, `jobs/<job-id>` holds outputs, logs, checkpoints and manifests, and `index` holds visual embeddings' thumbnails. The example configuration intentionally contains an unpinned model marker and cannot enable model loading until deployment supplies a real revision. `source_allowlist` must contain reviewed physical source parents; registration selects a folder within those parents. Links, unhydrated paths, drive-wide default scans, and overlapping source/state roots are refused.

The worker requires Python 3.10 or newer. Ordinary bridge/task operations use the standard library only. Visual jobs lazily load an isolated PyTorch/Transformers/Pillow environment and a locally installed SigLIP2 checkpoint. Model loading uses `local_files_only=True` and `trust_remote_code=False`. No model download, runtime installation or driver update occurs automatically.

`runtime-installation.json` and `requirements.installation.txt` record the successful isolated MSI setup's Python/package versions and exact model revision. Installation records do not replace runtime/tool seals or a GPU-preflight receipt. The scheduled MSI `v0.3.1` deployment subsequently passed real CUDA image/text inference; see the [assistant verification record](../docs/assistant-release.md) for its scope and the remaining footage evaluation.

`worker.py --preflight` tests text and image inference in that launch context and writes `gpu-preflight.json`. `get_preflight` reads the receipt and refuses a different model revision. A missing receipt is reported as unverified.

## Request methods

| Method | Parameters |
| --- | --- |
| `get_capabilities`, `get_preflight`, `list_source_roots` | `{}` |
| `register_source_root` | `{path,label?}` |
| `inventory` | `{root_id,limit?}`; bounded ordinary media-file metadata |
| `submit_job` | `{request_id,kind,project_id,params}` |
| `get_job`, `get_results`, `pause_job`, `resume_job`, `cancel_job` | `{job_id}` |
| `list_jobs` | `{project_id?,limit?}` |

Identical submissions return their original durable job. Reusing a request ID for different work is rejected. `get_results` returns `{job,manifest,manifest_path}`. Completed artifacts include `absolute_path`, job-relative `relative_path`, byte size and SHA-256. Search matches include source identity/time range and thumbnail path/hash/size. Consumers must verify transferred bytes and use INTERLINKED's ingestion contract rather than directly overwriting a cache.

Committed video and preview artifacts can themselves be selected for preview, extraction or organization. Their provenance is recorded in a separate generated-assets table: each reuse revalidates the completed job manifest, owned job-relative path, byte size and content hash. This does not permit source-folder registration of worker state or arbitrary state files. Metadata sidecars are not accepted as media inputs.

## Job kinds

- `download`: `{urls:[...]}`. Uses supplied individual links, a per-job archive and source receipts, isolated yt-dlp outputs, editor conversion and complete output decode. It preserves downloaded sources and existing downloader data. Conversion retries reuse the already downloaded source. No source cleanup script is invoked.
- `index`: `{root_ids:[...],sample_seconds?:2,asset_ids?:[...],start_seconds?:0,end_seconds?}`. Extracts actual presentation timestamps in 60-second chunks, stores checkpoints and yields to queued interactive work. One decoder and one GPU model are used. The model starts at batch eight and reduces to four/one after CUDA memory exhaustion.
- `search`: `{query,root_ids?:[...],limit?:20}`. Runs local text/image retrieval and returns grouped candidate ranges with relative similarity scores, not probabilities. Results remain suggestions.
- `preview`, `extract`: `{asset_id,start_seconds,end_seconds}`. Preview ranges are bounded to 60 seconds and extracts to one hour. Produces verified H.264/AAC output without modifying source footage.
- `organize`: `{asset_ids:[...],group_by?:"source",tag?}`. `group_by` is `source`, `date` (file modification date), or `tag`. Copies to an organized job tree, checks hashes, retains originals, and returns an organization map. It never deletes duplicates.

Index jobs reuse identical-content samples while retaining source aliases. Changed source metadata excludes stale search results until reindexing. Changed model revisions cannot reuse previous embeddings. Finer-range profiles do not incorrectly mark a whole recording indexed.

## MAGIX proxy handling

Worker `0.3.2` skips a redundant proxy during whole-source indexing only when its filename follows the MAGIX pattern `<original-stem-or-prefix>_<extension>_proxy_<32-hex>_<32-hex>.mpg` and there is one unambiguous matching original in the same directory. Truncated MAGIX stems are accepted only when the prefix identifies one candidate. An arbitrary MPG, a missing original, or an ambiguous filename does not qualify.

The original must already have a complete index for the current model and whole-source sampling profile. Its registered path, byte size, modification timestamp, and content hash must still match. Partial indexes, finer-range indexes, old model profiles, changed files, or unavailable evidence do not establish coverage. The normal media validation then remains in charge, including genuine failures that need review.

Originals are processed before proxy candidates. Pending checkpoints retain completed work, and an unfinished job can reconsider a proxy failure after its original becomes fully indexed. Terminal historical receipts are not silently rewritten. The final manifest records `skipped` separately from `failures`, with the proxy path, original path and asset identity, profile, and `magix_proxy_original_indexed` reason. A skipped derivative alone does not make the job need review. Both the proxy and original remain intact; this rule never deletes a file, substitutes a media path, or changes a MAGIX project.

The worker package and `get_capabilities.version` now share `__version__` (`0.3.2`). Desktop and local broker versions remain independent. This source/configuration update requires the separate scheduled MSI rollout and GPU preflight; see the assistant verification record for live acceptance.

## Verification

Run `python -B -m unittest discover -s tests -p test_media_worker.py -v` from the Buddy repository. The fixtures exercise six hours of virtual source duration, VFR-like timestamps, recovery, idempotency, isolated download/conversion retry, source preservation, copy-first organization, source/model invalidation, queue controls, protocol correlation and owned process cancellation.

Run `python -B -m unittest discover -s tests -p test_media_proxy_skip.py -v` for proxy skip regressions, including ambiguous/missing originals, stale metadata and content, profile mismatches, checkpoint recovery, source preservation, and separate skip receipts.

Those fixtures use fake media tools and embeddings except the bounded cancellation subprocess. They are not a real six-hour decode, useful visual-search evaluation, new internet-download test, CUDA benchmark, or installed MSI acceptance result. Separate live checks have now established scheduled GPU inference, a supplied-URL download, partial band-footage indexing/search, preview/extraction, active-session delivery, and explicit verified INTERLINKED save-back. Full-corpus retrieval quality remains a separate gate in the [assistant verification record](../docs/assistant-release.md).

## Media inbox (0.4.1 source)

See [the inbox guide](../docs/media-inbox.md) for rollout gates and actual proof
limits. `begin_upload`, `confirm_upload_chunk`, `finalize_upload`, `list_inbox`
and `assign_inbox` are typed archive operations. Begin accepts a stable request ID
and content/source manifest. Chunk confirmation and finalization return durable
job identities; bytes transfer separately through native SFTP.

`archive_root` must be an owned ordinary MSI directory outside worker state,
cloud and managed caches. Scheduled preflight validates this root before model
inference. Only committed Media is indexed. Library-scoped jobs explicitly use
`scope: library` and null project identity; existing project-scoped callers retain
their contract. Archive receipts/associations survive worker-database loss.
