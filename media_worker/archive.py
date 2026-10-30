"""Independent inbox storage. Payloads never live in worker caches or library shares."""
from __future__ import annotations
import getpass
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shutil
import socket
import time

from .core import BuddyError, MEDIA, atomic_json, canonical, digest, inside, ordinary, worker_lock

CHUNK = 64 * 1024 * 1024
HEX = re.compile(r"^[a-f0-9]{64}$")
REQUEST = re.compile(r"^[a-f0-9]{32}$")


def need(ok, message):
    if not ok:
        raise BuddyError("archive_invalid", message)


def read(p):
    ordinary(p, directory=False)
    need(p.stat().st_nlink == 1 and p.stat().st_size <= 4 * 1024 * 1024, "Invalid archive metadata.")
    try:
        return json.loads(p.read_text("utf-8"))
    except (ValueError, UnicodeError) as error:
        raise BuddyError("archive_invalid", "Archive metadata is malformed.") from error


def validate_manifest(m):
    need(isinstance(m, dict) and set(m) == {"sha256", "bytes", "name", "source"}, "Invalid upload manifest.")
    need(isinstance(m["sha256"], str) and HEX.fullmatch(m["sha256"]), "Invalid content hash.")
    need(type(m["bytes"]) is int and 0 < m["bytes"] <= 16 * 1024**4, "Invalid video length.")
    name = m["name"]
    need(isinstance(name, str) and 0 < len(name) <= 255 and not any(c in name for c in '/\\:\x00\r\n') and Path(name).suffix.lower() in MEDIA, "Unsupported video filename.")
    need(isinstance(m["source"], dict), "Invalid source provenance.")
    try:
        need(len(canonical(m["source"])) <= 8192, "Invalid source provenance.")
    except (TypeError, ValueError) as error:
        raise BuddyError("archive_invalid", "Invalid source provenance.") from error
    return m


def validate_receipt(base, rid, manifest, value):
    need(isinstance(value, dict) and value.get("format") == "buddy-archive-commit-v1", "Invalid commit receipt.")
    need(request_id(value) == rid and validate_manifest(value.get("manifest")) == manifest, "Stored receipt identity differs.")
    need(value.get("sha256") == manifest["sha256"] and type(value.get("bytes")) is int and value["bytes"] == manifest["bytes"] and value.get("asset_id") == "asset-" + manifest["sha256"], "Stored receipt content identity differs.")
    verified = value.get("verified_at")
    need(value.get("kind") == "archived_video" and type(verified) in (int, float) and math.isfinite(verified) and verified > 0, "Invalid stored receipt verification.")
    need(isinstance(value.get("absolute_path"), str), "Invalid stored payload path.")
    target_root = base / "Media" / manifest["sha256"]
    target = inside(value["absolute_path"], target_root, directory=False)
    need(target.parent == target_root and target.name == "video" + target.suffix.lower() and target.suffix.lower() in MEDIA, "Stored payload is outside its content object.")
    need(target.stat().st_nlink == 1 and target.stat().st_size == manifest["bytes"], "Stored payload metadata differs.")
    return value


def root(engine):
    value = engine.config.get("archive_root")
    need(isinstance(value, str) and Path(value).is_absolute(), "Inbox requires a configured physical archive_root on MSI.")
    p = ordinary(value, directory=True, must_exist=False)
    forbidden = [engine.state, *map(Path, engine.config.get("archive_excluded_roots", []))]
    need(not any(engine._contained(p, x) or engine._contained(x, p) for x in forbidden), "Archive overlaps a protected scope.")
    need(not any(x.casefold().startswith("onedrive") or x.casefold() in ("projectlibrary", "projectreturns", "librarycache") for x in p.parts), "Archive must be outside cloud and project libraries.")
    need(not str(p).startswith("\\\\"), "Archive must be local to MSI.")
    p.mkdir(parents=True, exist_ok=True)
    owner = {"format": "buddy-archive-owner-v1", "machine": socket.gethostname(), "user": getpass.getuser()}
    marker = p / "OWNER.json"
    if marker.exists():
        need(read(marker) == owner, "Archive owner differs.")
    else:
        need(not any(p.iterdir()), "Refusing an existing unowned archive.")
        atomic_json(marker, owner)
    for name in ("Incoming", "Media", "Receipts", "Associations"):
        folder = ordinary(p / name, directory=True, must_exist=False)
        folder.mkdir(exist_ok=True)
    return p


def request_id(params):
    value = params.get("request_id", "")
    need(isinstance(value, str) and REQUEST.fullmatch(value), "Invalid upload request identity.")
    return value


def session(engine, params):
    rid = request_id(params)
    base = root(engine)
    folder = inside(base / "Incoming" / rid, base / "Incoming", directory=True)
    value = read(folder / "session.json")
    need(isinstance(value, dict) and set(value) == {"request_id", "manifest", "created_at"}, "Invalid persisted upload session.")
    need(request_id(value) == rid == folder.name, "Persisted upload session identity differs.")
    validate_manifest(value["manifest"])
    created = value["created_at"]
    need(type(created) in (int, float) and math.isfinite(created) and created > 0, "Invalid persisted upload timestamp.")
    return folder, value


def validate_chunk(s, v):
    need(isinstance(v, dict) and set(v) in ({"request_id", "index", "sha256", "bytes"}, {"request_id", "index", "sha256", "bytes", "mtime_ns"}), "Invalid chunk metadata.")
    need(request_id(v) == s["request_id"], "Chunk request identity differs.")
    i, size, sha = v["index"], v["bytes"], v["sha256"]
    need(type(i) is int and 0 <= i < math.ceil(s["manifest"]["bytes"] / CHUNK), "Invalid chunk index.")
    need(type(size) is int and size == min(CHUNK, s["manifest"]["bytes"] - i * CHUNK), "Invalid chunk length.")
    need(isinstance(sha, str) and HEX.fullmatch(sha), "Invalid chunk digest.")
    if "mtime_ns" in v:
        need(type(v["mtime_ns"]) is int and v["mtime_ns"] > 0, "Invalid chunk timestamp.")
    return v


def proof_matches(folder, s, v):
    try:
        proof = validate_chunk(s, read(folder / ("chunk-%06d.json" % v["index"])))
        part = ordinary(folder / ("chunk-%06d.part" % v["index"]), directory=False)
        return all(proof[k] == v[k] for k in ("request_id", "index", "bytes", "sha256")) and part.stat().st_nlink == 1 and part.stat().st_size == v["bytes"] and part.stat().st_mtime_ns == proof.get("mtime_ns")
    except (BuddyError, OSError):
        return False


def begin(engine, params):
    need(set(params) == {"request_id", "manifest"}, "Unexpected upload fields.")
    rid = request_id(params)
    m = validate_manifest(params["manifest"])
    base = root(engine)
    receipt = base / "Receipts" / (rid + ".json")
    if receipt.exists():
        prior = validate_receipt(base, rid, m, read(receipt))
        return {"status": "stored", "receipt": prior}
    folder = base / "Incoming" / rid
    ordinary(folder, directory=True, must_exist=False)
    if folder.exists():
        _, saved = session(engine, params)
        need(saved["manifest"] == m, "Upload ID already names different work.")
    else:
        # Space includes unassembled bytes of all other incomplete sessions.
        reserved = 0
        for path in (base / "Incoming").glob("*/session.json"):
            other_id = request_id({"request_id": path.parent.name})
            if not (base / "Receipts" / (other_id + ".json")).exists():
                _, pending = session(engine, {"request_id": other_id})
                reserved += pending["manifest"]["bytes"] * 2
        need(shutil.disk_usage(base).free >= m["bytes"] * 2 + reserved + 64 * 1024**2, "Insufficient MSI archive space; upload remains queued.")
        folder.mkdir()
        atomic_json(folder / "session.json", {"request_id": rid, "manifest": m, "created_at": time.time()})
    for child in folder.iterdir():
        ordinary(child)
        if child.is_file():
            need(child.stat().st_nlink == 1, "Linked upload state is unavailable.")
    verified = []
    for f in folder.glob("chunk-*.json"):
        try:
            v = validate_chunk({"request_id": rid, "manifest": m}, read(f))
            if f.name == "chunk-%06d.json" % v["index"] and proof_matches(folder, {"request_id": rid, "manifest": m}, v):
                verified.append(v)
        except BuddyError:
            pass  # Invalid proof never authorizes skipping bytes; reconfirm revalidates them.
    return {"status": "uploading", "upload_directory": str(folder), "chunk_bytes": CHUNK, "verified": verified}


def confirm(engine, params):
    need(set(params) == {"request_id", "index", "sha256", "bytes"}, "Unexpected chunk fields.")
    folder, s = session(engine, params)
    validate_chunk(s, params)
    i = params["index"]
    fingerprint = hashlib.sha256(canonical(params).encode()).hexdigest()
    # Revalidation is a new durable verification generation, never a rewrite of
    # a completed job. Lost acknowledgements reuse the pending generation.
    with worker_lock(folder):
        intent = folder / ("verification-%06d.json" % i)
        record = read(intent) if intent.exists() else {"generation": 0, "fingerprint": fingerprint}
        need(isinstance(record, dict) and set(record) == {"generation", "fingerprint"} and type(record["generation"]) is int and 0 <= record["generation"] < 1000000 and record["fingerprint"] == fingerprint, "Chunk verification intent differs.")
        def submit():
            suffix = "-check-" + str(record["generation"]) if record["generation"] else ""
            return engine.submit({"request_id": s["request_id"] + "-chunk-" + str(i) + suffix, "kind": "archive_chunk", "scope": "library", "project_id": None, "params": dict(params)})
        atomic_json(intent, record)
        job = submit()
        if job["status"] == "completed" and not proof_matches(folder, s, params):
            record["generation"] += 1
            atomic_json(intent, record)
            job = submit()
        return job


def finalize(engine, params):
    need(set(params) == {"request_id"}, "Unexpected commit fields.")
    folder, s = session(engine, params)
    return engine.submit({"request_id": s["request_id"] + "-commit", "kind": "archive_commit", "scope": "library", "project_id": None, "params": params})


def execute(engine, job):
    folder, s = session(engine, job["params"])
    check = lambda: engine.check_control(job["job_id"])
    if job["kind"] == "archive_chunk":
        v = job["params"]
        validate_chunk(s, v)
        p = ordinary(folder / ("chunk-%06d.part" % v["index"]), directory=False)
        need(p.stat().st_nlink == 1, "Hardlinked upload chunk.")
        sha, size, _ = digest(p, check)
        need((sha, size) == (v["sha256"], v["bytes"]), "Uploaded chunk verification failed.")
        atomic_json(folder / ("chunk-%06d.json" % v["index"]), dict(v, mtime_ns=p.stat().st_mtime_ns))
        return {"verified_chunk": v}
    base = root(engine)
    m = s["manifest"]
    receipt_path = base / "Receipts" / (s["request_id"] + ".json")
    if receipt_path.exists():
        receipt = validate_receipt(base, s["request_id"], m, read(receipt_path))
    else:
        target_dir = base / "Media" / m["sha256"]
        ordinary(target_dir, directory=True, must_exist=False)
        temp = folder / "assembled"
        ordinary(temp, directory=True, must_exist=False)
        temp.mkdir(exist_ok=True)
        payload = temp / ("video" + Path(m["name"]).suffix.lower())
        ordinary(payload, directory=False, must_exist=False)
        need(not payload.exists() or payload.stat().st_nlink == 1, "Hardlinked assembly.")
        h = hashlib.sha256()
        total = 0
        with payload.open("wb") as out:
            for i in range(math.ceil(m["bytes"] / CHUNK)):
                check()
                v = validate_chunk(s, read(folder / ("chunk-%06d.json" % i)))
                need(v["index"] == i, "Chunk position differs.")
                part = ordinary(folder / ("chunk-%06d.part" % i), directory=False)
                need(part.stat().st_nlink == 1, "Hardlinked upload chunk.")
                ch = hashlib.sha256()
                size = 0
                with part.open("rb") as incoming:
                    while True:
                        check()
                        b = incoming.read(4 * 1024**2)
                        if not b:
                            break
                        out.write(b); h.update(b); ch.update(b); size += len(b); total += len(b)
                if (ch.hexdigest(), size) != (v["sha256"], v["bytes"]):
                    (folder / ("chunk-%06d.json" % i)).unlink()
                    raise BuddyError("chunk_changed", "Chunk changed after verification; resume to retransmit it.")
            out.flush(); os.fsync(out.fileno())
        need((h.hexdigest(), total) == (m["sha256"], m["bytes"]), "Assembled video differs from original.")
        # Verify persisted bytes before publishing, including an existing deduplicated copy.
        need(digest(payload, check)[:2] == (m["sha256"], m["bytes"]), "Assembly readback failed.")
        if target_dir.exists():
            obj = read(target_dir / "object.json")
            need(isinstance(obj, dict) and set(obj) == {"sha256", "bytes", "filename"} and obj["sha256"] == m["sha256"] and obj["bytes"] == m["bytes"] and isinstance(obj["filename"], str), "Invalid stored object metadata.")
            target = inside(target_dir / obj["filename"], target_dir, directory=False)
            need(target.stat().st_nlink == 1 and digest(target, check)[:2] == (m["sha256"], m["bytes"]), "Existing archived content failed verification.")
        else:
            atomic_json(temp / "object.json", {"sha256": m["sha256"], "bytes": total, "filename": payload.name})
            os.rename(temp, target_dir)
            target = target_dir / payload.name
        receipt = {"format": "buddy-archive-commit-v1", "request_id": s["request_id"], "manifest": m, "asset_id": "asset-" + m["sha256"], "sha256": m["sha256"], "bytes": total,
                   "absolute_path": str(target), "verified_at": time.time(), "kind": "archived_video"}
        atomic_json(receipt_path, receipt)
    media = base / "Media"
    config = dict(engine.config)
    # Archive is its own explicitly configured source parent, never worker state.
    engine.config["source_allowlist"] = [*config.get("source_allowlist", []), str(media)]
    try:
        root_id = engine.register_root({"path": str(media), "label": "Media inbox"})["root_id"]
    finally:
        engine.config = config
    asset_id = engine.register_asset(receipt["absolute_path"], root_id, check, {"original_name": receipt["manifest"]["name"], "archived": True})
    index_job = engine.submit({"request_id": s["request_id"] + "-index", "kind": "index", "scope": "library", "project_id": None,
                               "params": {"root_ids": [root_id], "asset_ids": [asset_id]}})
    receipt = dict(receipt, root_id=root_id, index_job_id=index_job["job_id"])
    atomic_json(receipt_path, receipt)
    # Release only this upload's redundant chunks after a durable verified receipt.
    for part in folder.glob("chunk-*"):
        ordinary(part, directory=False)
        need(part.stat().st_nlink == 1, "Refusing linked upload cleanup.")
        part.unlink()
    return {"archive_receipt": receipt}


def listing(engine, params):
    base = root(engine)
    offset = int(params.get("offset", 0))
    need(offset >= 0, "Invalid inbox offset.")
    paths = sorted((base / "Receipts").glob("*.json"))
    rows = []
    for p in paths[offset:offset + 100]:
        rid = request_id({"request_id": p.stem})
        r = read(p)
        need(isinstance(r, dict), "Invalid inbox receipt.")
        r = validate_receipt(base, rid, validate_manifest(r.get("manifest")), r)
        a = base / "Associations" / (r["asset_id"] + ".json")
        r["project_ids"] = read(a)["project_ids"] if a.exists() else []
        rows.append(r)
    return {"items": rows, "next_offset": offset + 100 if len(paths) > offset + 100 else None}


def associate(engine, params):
    need(set(params) == {"asset_id", "project_id"}, "Unexpected assignment fields.")
    asset = params["asset_id"]
    need(isinstance(asset, str) and asset.startswith("asset-") and HEX.fullmatch(asset[6:]), "Invalid archive asset.")
    project = params["project_id"]
    need(isinstance(project, str) and 0 < len(project) <= 300, "Invalid project identity.")
    base = root(engine)
    need((base / "Media" / asset[6:] / "object.json").exists(), "Unknown archived asset.")
    with worker_lock(base):
        p = base / "Associations" / (asset + ".json")
        v = read(p) if p.exists() else {"asset_id": asset, "project_ids": []}
        if project not in v["project_ids"]:
            v["project_ids"].append(project)
        atomic_json(p, v)
    return v
