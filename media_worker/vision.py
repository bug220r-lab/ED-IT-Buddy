"""Local-only visual retrieval. Model weights are never downloaded implicitly."""
from __future__ import annotations

import hashlib
import json
import math
import re
import shutil
import time
from pathlib import Path

from .core import BuddyError, atomic_json, canonical, digest, inside, ordinary
from .jobs import probe, tool


class SiglipEncoder:
    def __init__(self, config):
        path = config.get("model_path")
        if not path or not config.get("model_revision") or str(config["model_revision"]).startswith("PIN_"):
            raise BuddyError("model_required", "Install and pin the local SigLIP2 model before indexing.")
        ordinary(path, directory=True)
        try:
            import torch
            from transformers import AutoModel, AutoProcessor
        except ImportError as error:
            raise BuddyError("runtime_required", "The isolated worker runtime needs PyTorch, Transformers, and Pillow.") from error
        if not torch.cuda.is_available():
            raise BuddyError("gpu_unavailable", "MSI CUDA inference is unavailable in this worker launch context.")
        self.torch = torch
        self.device = "cuda:0"
        self.model = AutoModel.from_pretrained(path, local_files_only=True, trust_remote_code=False, torch_dtype=torch.float16).to(self.device).eval()
        self.processor = AutoProcessor.from_pretrained(path, local_files_only=True, trust_remote_code=False)
        self.batch_size = 8

    def _features(self, result):
        if hasattr(result, "pooler_output"):
            result = result.pooler_output
        result = result.float()
        result = result / result.norm(dim=-1, keepdim=True).clamp(min=1e-8)
        return result.cpu().tolist()

    def images(self, paths):
        from PIL import Image
        output = []
        offset = 0
        while offset < len(paths):
            size = min(self.batch_size, len(paths) - offset)
            pictures = []
            try:
                for p in paths[offset:offset + size]:
                    with Image.open(p) as picture:
                        pictures.append(picture.convert("RGB"))
                inputs = self.processor(images=pictures, return_tensors="pt").to(self.device)
                with self.torch.inference_mode():
                    output.extend(self._features(self.model.get_image_features(**inputs)))
                offset += size
            except self.torch.cuda.OutOfMemoryError:
                self.torch.cuda.empty_cache()
                if self.batch_size <= 1:
                    raise BuddyError("gpu_memory", "The smallest indexing batch does not fit. Pause other MSI GPU work.")
                self.batch_size = 4 if self.batch_size > 4 else 1
            finally:
                for picture in pictures:
                    picture.close()
        return output

    def text(self, phrase):
        inputs = self.processor(text=[phrase], padding="max_length", return_tensors="pt").to(self.device)
        with self.torch.inference_mode():
            return self._features(self.model.get_text_features(**inputs))[0]

    def facts(self):
        return {"cuda": True, "device": self.torch.cuda.get_device_name(0), "torch": self.torch.__version__, "cuda_runtime": self.torch.version.cuda, "model_loaded": True, "batch_size": self.batch_size, "gpu_allocated_bytes": self.torch.cuda.memory_allocated(0), "gpu_reserved_bytes": self.torch.cuda.memory_reserved(0)}


def encoder(engine):
    if engine.encoder is None:
        engine.encoder = SiglipEncoder(engine.config)
    return engine.encoder


def preflight(engine):
    started = time.monotonic()
    model = encoder(engine)
    vector = model.text("a live band playing on a stage")
    if not vector or not all(math.isfinite(x) for x in vector):
        raise BuddyError("preflight_failed", "The local model did not produce a finite query embedding.")
    from PIL import Image
    picture = ordinary(engine.state / "preflight-frame.png", directory=False, must_exist=False)
    if picture.exists() and picture.stat().st_nlink > 1:
        raise BuddyError("unsafe_path", "Preflight image must not be hardlinked.")
    with Image.new("RGB", (224, 224), color=(32, 48, 64)) as image:
        image.save(picture)
    images = model.images([picture])
    if len(images) != 1 or len(images[0]) != len(vector) or not all(math.isfinite(x) for x in images[0]):
        raise BuddyError("preflight_failed", "The local model did not produce a compatible finite image embedding.")
    return {"passed": True, "observed_at": time.time(), "elapsed_seconds": time.monotonic() - started, "dimensions": len(vector), "model_revision": engine.config.get("model_revision"), "runtime": model.facts() if hasattr(model, "facts") else {"test_encoder": True}}


def _profile(engine, interval, start=0, end=None):
    identity = {"model_revision": engine.config.get("model_revision", "test"), "sample_seconds": float(interval), "start_seconds": float(start), "end_seconds": float(end) if end is not None else None, "pipeline": "pts-select-v1"}
    return hashlib.sha256(canonical(identity).encode()).hexdigest()[:32]


def index_unit(engine, job):
    """One 60-second chunk, so interactive jobs can run between chunks."""
    check = lambda: engine.check_control(job["job_id"])
    directory = engine.job_dir(job["job_id"])
    plan_file = directory / "index-plan.json"
    if plan_file.exists():
        plan = json.loads(plan_file.read_text("utf-8"))
    else:
        items = []
        selected = set(job["params"].get("asset_ids", []))
        for root_id in job["params"]["root_ids"]:
            for p in engine.media_files(root_id):
                check()
                if len(items) >= 100000:
                    raise BuddyError("inventory_limit", "Selected sources exceed 100,000 media files. Select a smaller scope.")
                items.append({"root_id": root_id, "path": str(p)})
        plan = {"items": items, "cursor": 0, "results": [], "failures": [], "selected": sorted(selected)}
        atomic_json(plan_file, plan)
    interval = float(job["params"].get("sample_seconds", 2))
    profile = _profile(engine, interval, job["params"].get("start_seconds", 0), job["params"].get("end_seconds"))
    while plan["cursor"] < len(plan["items"]):
        check()
        item = plan["items"][plan["cursor"]]
        try:
            source = inside(item["path"], engine.root(item["root_id"])["path"], directory=False)
            if "asset_id" not in item:
                meta = probe(engine, job, source)
                item["asset_id"] = engine.register_asset(source, item["root_id"], check, meta)
                item["metadata"] = meta
                atomic_json(plan_file, plan)
            asset_id = item["asset_id"]
            if plan["selected"] and asset_id not in plan["selected"]:
                plan["cursor"] += 1
                continue
            with engine.connect() as c:
                source_alias = c.execute("SELECT * FROM aliases WHERE root_id=? AND path=?", (item["root_id"], str(source))).fetchone()
            if not source_alias:
                raise BuddyError("source_changed", "Indexed source is no longer registered.")
            alias = dict(source_alias)
            source_stat = source.stat()
            if (source_stat.st_size, source_stat.st_mtime_ns) != (alias["bytes"], alias["mtime_ns"]):
                raise BuddyError("source_changed", "Indexed source changed.")
            with engine.connect() as c:
                row = c.execute("SELECT * FROM indexes WHERE asset_id=? AND profile=?", (asset_id, profile)).fetchone()
            if row and row["complete"]:
                plan["results"].append({"asset_id": asset_id, "root_id": item["root_id"], "source_name": source.name, "reused": True})
                plan["cursor"] += 1
                atomic_json(plan_file, plan)
                continue
            duration = item["metadata"]["duration_seconds"]
            start = float(row["next_seconds"]) if row else float(job["params"].get("start_seconds", 0))
            end_limit = min(duration, float(job["params"].get("end_seconds", duration)))
            if start >= end_limit:
                with engine.connect() as c:
                    c.execute("INSERT OR REPLACE INTO indexes VALUES(?,?,?,1)", (asset_id, profile, end_limit))
                plan["results"].append({"asset_id": asset_id, "root_id": item["root_id"], "source_name": source.name, "reused": False})
                plan["cursor"] += 1
                atomic_json(plan_file, plan)
                continue
            end = min(start + 60, end_limit)
            frames_root = engine.state / "index" / asset_id / profile
            frames_root.mkdir(parents=True, exist_ok=True)
            frames = frames_root / ("chunk-" + str(round(start * 1000000)))
            frames.mkdir(exist_ok=True)
            inside(frames, engine.state, directory=True)
            for old in frames.glob("frame-*.jpg"):
                inside(old, frames, directory=False).unlink()
            log = directory / "tools.log"
            offset = log.stat().st_size if log.exists() else 0
            args = ["-hide_banner", "-v", "info", "-nostdin", "-n", "-threads", "2", "-ss", str(start), "-i", str(source), "-t", str(end - start), "-an", "-vf", f"select='isnan(prev_selected_t)+gte(t-prev_selected_t,{interval})',showinfo,scale=384:-2", "-fps_mode", "vfr", "-frames:v", str(math.ceil(60 / interval) + 2), "-q:v", "4", str(frames / "frame-%06d.jpg")]
            tool(engine, job, "ffmpeg", args, timeout=600)
            with log.open("rb") as stream:
                stream.seek(offset)
                observations = stream.read(4 * 1024 * 1024).decode("utf-8", "replace")
            timestamps = [float(x) for x in re.findall(r"\bpts_time:([+-]?[\d.eE]+)", observations)]
            pictures = sorted(frames.glob("frame-*.jpg"))
            if len(pictures) != len(timestamps) or not pictures:
                raise BuddyError("timestamp_evidence", "Could not pair decoded frames with source presentation timestamps.")
            vectors = encoder(engine).images(pictures)
            if len(vectors) != len(pictures):
                raise BuddyError("index_failed", "Model returned an inconsistent number of image embeddings.")
            # Verify source metadata before committing this chunk.
            source_now = source.stat()
            if source_now.st_size != alias["bytes"] or source_now.st_mtime_ns != alias["mtime_ns"]:
                raise BuddyError("source_changed", "Source changed during indexing; previous results remain available.")
            check()
            with engine.connect() as c:
                for picture, pts, vector in zip(pictures, timestamps, vectors):
                    if not vector or not all(math.isfinite(x) for x in vector):
                        raise BuddyError("index_failed", "Non-finite visual embedding.")
                    c.execute("INSERT OR REPLACE INTO samples VALUES(?,?,?,?,?)", (asset_id, profile, round(start + pts, 6), str(picture), canonical(vector)))
                c.execute("INSERT OR REPLACE INTO indexes VALUES(?,?,?,?)", (asset_id, profile, end, int(end >= end_limit)))
            engine.progress(job["job_id"], indexed_files=plan["cursor"], total_files=len(plan["items"]), current_file=source.name, indexed_seconds=end, source_duration_seconds=duration, sample_seconds=interval, profile=profile)
            if end >= end_limit:
                plan["results"].append({"asset_id": asset_id, "root_id": item["root_id"], "source_name": source.name, "reused": False})
                plan["cursor"] += 1
            atomic_json(plan_file, plan)
            return None
        except (BuddyError, OSError, ValueError) as error:
            plan["failures"].append({"source_name": Path(item["path"]).name, "code": getattr(error, "code", "index_failed"), "message": str(error)[:1000]})
            plan["cursor"] += 1
            atomic_json(plan_file, plan)
            # A corrupt asset must not prevent other selected recordings from indexing.
            return None
    return {"results": plan["results"], "failures": plan["failures"], "profile": profile, "sample_seconds": interval, "local_analysis": True}


def search(engine, job):
    params = job["params"]
    phrase = params["query"].strip()
    vector = encoder(engine).text(phrase)
    candidates = []
    roots = set(params.get("root_ids", []))
    limit = max(1, min(int(params.get("limit", 20)), 100))
    model_revision = engine.config.get("model_revision", "test")
    with engine.connect() as c:
        known = list(c.execute("SELECT params FROM jobs WHERE kind='index' AND status IN ('queued','running','paused','completed','needs_review')"))
        expected_profiles = set()
        for entry in known:
            args = json.loads(entry["params"])
            expected_profiles.add(_profile(engine, args.get("sample_seconds", 2), args.get("start_seconds", 0), args.get("end_seconds")))
        rows = c.execute("SELECT * FROM samples")
        for count, row in enumerate(rows):
            if count % 200 == 0:
                engine.check_control(job["job_id"])
            aliases = list(c.execute("SELECT root_id,path,bytes,mtime_ns FROM aliases WHERE asset_id=?", (row["asset_id"],)))
            aliases = [x for x in aliases if not roots or x["root_id"] in roots]
            if not aliases:
                continue
            if row["profile"] not in expected_profiles:
                continue
            try:
                source, alias = engine.asset_path(row["asset_id"], roots)
                picture = inside(row["thumbnail"], engine.state / "index", directory=False)
            except (BuddyError, OSError):
                continue
            values = json.loads(row["embedding"])
            if len(values) != len(vector):
                continue
            score = sum(a * b for a, b in zip(values, vector))
            candidates.append({"asset_id": row["asset_id"], "root_id": alias["root_id"], "source_name": source.name, "source_path": str(source), "timestamp_seconds": row["pts"], "score": score, "thumbnail_path": str(picture), "profile": row["profile"]})
    candidates.sort(key=lambda x: x["score"], reverse=True)
    grouped = []
    for candidate in candidates:
        if any(x["asset_id"] == candidate["asset_id"] and abs(x["timestamp_seconds"] - candidate["timestamp_seconds"]) < 6 for x in grouped):
            continue
        sha, size, _ = digest(candidate["thumbnail_path"])
        candidate.update({"start_seconds": max(0, candidate["timestamp_seconds"] - 3), "end_seconds": candidate["timestamp_seconds"] + 5, "thumbnail_sha256": sha, "thumbnail_bytes": size, "selection_reason": f"Local visual similarity to: {phrase}", "review_state": "suggested"})
        with engine.connect() as c:
            meta = c.execute("SELECT metadata FROM assets WHERE asset_id=?", (candidate["asset_id"],)).fetchone()
        duration = json.loads(meta[0]).get("duration_seconds") if meta else None
        if duration:
            candidate["end_seconds"] = min(candidate["end_seconds"], duration)
        grouped.append(candidate)
        if len(grouped) >= limit:
            break
    return {"results": grouped, "query": phrase, "model_revision": model_revision, "score_meaning": "relative visual similarity, not a probability", "local_analysis": True}
