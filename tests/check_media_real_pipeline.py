"""Opt-in real FFmpeg/PTS pipeline check; generated fixture is not band acceptance.

Uses a deterministic injected encoder unless --model-config is supplied. Source
files are read-only. Artifacts go only to the explicitly selected fresh folder.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from media_worker.core import Engine, atomic_json, digest, ordinary


class TimestampTestEncoder:
    def images(self, paths):
        return [[1.0, 0.0] for p in paths]

    def text(self, query):
        return [1.0, 0.0]


def run(command):
    result = subprocess.run(list(map(str, command)), capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=300)
    if result.returncode:
        raise RuntimeError(result.stderr[-5000:])
    return result.stdout


def finish(engine, job, limit=20000):
    for _ in range(limit):
        current = engine.get_job({"job_id": job["job_id"]})
        if current["status"] in ("completed", "needs_review", "failed", "paused", "cancelled"):
            if current["status"] != "completed":
                result = engine.get_results({"job_id": job["job_id"]})
                raise RuntimeError(json.dumps(result))
            return engine.get_results({"job_id": job["job_id"]})
        engine.run_next()
    raise RuntimeError("Job did not finish within its unit bound")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--ffmpeg", required=True)
    parser.add_argument("--ffprobe", required=True)
    parser.add_argument("--output", required=True, help="Fresh absolute folder; never an existing source/workspace")
    parser.add_argument("--source", help="Optional existing recording, read-only; default generates a130s VFR test recording")
    parser.add_argument("--model-config", help="Opt into actual local model with supplied isolated runtime/config")
    args = parser.parse_args()
    output = ordinary(args.output, directory=True, must_exist=False)
    if output.exists():
        raise RuntimeError("Choose a fresh output folder")
    output.mkdir(parents=True)
    if args.source:
        source = ordinary(args.source, directory=False)
    else:
        source_root = output / "sources"
        source_root.mkdir()
        source = source_root / "vfr-test.mp4"
        run([args.ffmpeg, "-hide_banner", "-v", "error", "-nostdin", "-n", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30:duration=130", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=130", "-vf", "select='not(mod(n,7))+not(mod(n,11))'", "-fps_mode", "vfr", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "35", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "64k", str(source)])
    before = source.stat()
    source_scope = source.parent
    config = json.loads(Path(args.model_config).read_text("utf-8-sig")) if args.model_config else {"model_revision": "injected-timestamp-test-encoder"}
    config.update({"expected_host": "", "source_allowlist": [str(source_scope)], "ffmpeg_path": args.ffmpeg, "ffprobe_path": args.ffprobe})
    # The source scope must contain only the selected recording for this check.
    # Register its actual parent but explicitly limit index work to its content ID.
    engine = Engine(output / "state", config, encoder=None if args.model_config else TimestampTestEncoder())
    root = engine.register_root({"path": str(source_scope), "label": "Explicit pipeline test source"})["root_id"]
    source_asset = "asset-" + digest(source)[0]
    sequence = 0
    def submit(kind, params):
        nonlocal sequence
        sequence += 1
        return engine.submit({"request_id": "pipeline-" + str(sequence), "kind": kind, "project_id": "disposable-pipeline-check", "params": params})
    started = time.monotonic()
    indexed = finish(engine, submit("index", {"root_ids": [root], "asset_ids": [source_asset]}))
    index_seconds = time.monotonic() - started
    probe = json.loads(run([args.ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "frame=best_effort_timestamp_time", "-of", "json", str(source)]))
    actual = [float(f["best_effort_timestamp_time"]) for f in probe["frames"] if "best_effort_timestamp_time" in f]
    with engine.connect() as c:
        indexed_pts = [r[0] for r in c.execute("SELECT pts FROM samples ORDER BY pts")]
        metadata = json.loads(c.execute("SELECT metadata FROM assets WHERE asset_id=?", (source_asset,)).fetchone()[0])
    mismatches = [pts for pts in indexed_pts if min(abs(pts - original) for original in actual) > .00002]
    if not indexed_pts or mismatches:
        raise RuntimeError("Indexed timestamps differ from independently decoded source PTS: " + str(mismatches[:20]))
    duration = metadata.get("duration_seconds")
    if not duration:
        # register_asset above has no metadata; the probe/index path fills the record.
        raw = json.loads(run([args.ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "json", str(source)]))
        duration = float(raw["format"]["duration"])
    search = finish(engine, submit("search", {"query": "a wide live band stage view", "root_ids": [root], "limit": 5}))
    preview_end = min(8.13, duration)
    if preview_end <= 1.13:
        raise RuntimeError("Test recording must be longer than two seconds")
    preview = finish(engine, submit("preview", {"asset_id": source_asset, "start_seconds": 1.13, "end_seconds": preview_end}))
    extracted = finish(engine, submit("extract", {"asset_id": source_asset, "start_seconds": 1.13, "end_seconds": preview_end}))
    for manifest in (preview["manifest"], extracted["manifest"]):
        clip = manifest["assets"][0]
        run([args.ffmpeg, "-v", "error", "-xerror", "-nostdin", "-i", clip["absolute_path"], "-map", "0:v:0", "-map", "0:a?", "-f", "null", "-"])
        if digest(clip["absolute_path"])[0] != clip["sha256"]:
            raise RuntimeError("Clip content differs from manifest hash")
    after = source.stat()
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise RuntimeError("Source recording metadata changed during read-only pipeline check")
    result = {"passed": True, "scope": "actual FFmpeg decode/PTS/preview/extraction, " + ("actual configured model" if args.model_config else "injected deterministic encoder"), "source": str(source), "synthetic_source": not bool(args.source), "source_duration_seconds": duration, "indexed_samples": len(indexed_pts), "independent_source_frames": len(actual), "pts_mismatches": len(mismatches), "index_elapsed_seconds": index_seconds, "search_results": len(search["manifest"]["results"]), "preview": preview["manifest"]["assets"], "extracted": extracted["manifest"]["assets"], "source_unchanged": True}
    atomic_json(output / "result.json", result)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
