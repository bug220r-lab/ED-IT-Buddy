"""Durable typed media jobs. Importing this module never loads a model or starts work."""
from __future__ import annotations

import contextlib
import getpass
import hashlib
import json
import math
import os
import re
import socket
import sqlite3
import stat
import threading
import time
import uuid
from pathlib import Path

from . import __version__

FORMAT = "buddy-media-manifest-v1"
ID = re.compile(r"^[a-zA-Z0-9_.-]{1,100}$")
MEDIA = {".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v", ".webm", ".mpg", ".mpeg", ".mts", ".m2ts"}
KINDS = {"download", "index", "search", "preview", "extract", "organize"}
TERMINAL = {"completed", "failed", "cancelled", "needs_review"}
_MAX_JSON = 4 * 1024 * 1024


class BuddyError(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


class JobPaused(Exception):
    pass


class JobCancelled(Exception):
    pass


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False)


def identifier(value, name="ID"):
    if not isinstance(value, str) or not ID.fullmatch(value):
        raise BuddyError("invalid_request", f"Invalid {name}.")
    return value


def ordinary(path, *, directory=None, must_exist=True):
    """Reject link/cloud ancestors before resolving; never follow an untrusted junction."""
    p = Path(os.path.abspath(os.fspath(path)))
    for node in [p, *p.parents]:
        try:
            s = node.lstat()
        except FileNotFoundError:
            if node == p and must_exist:
                raise BuddyError("missing_file", f"File or folder is unavailable: {p}")
            continue
        attrs = getattr(s, "st_file_attributes", 0)
        if stat.S_ISLNK(s.st_mode) or attrs & (0x400 | 0x1000 | 0x40000 | 0x400000):
            raise BuddyError("unsafe_path", f"Linked, offline, or unhydrated path is unavailable: {node}")
    if p.exists():
        if directory is True and not p.is_dir():
            raise BuddyError("unsafe_path", "Expected an ordinary directory.")
        if directory is False and not p.is_file():
            raise BuddyError("unsafe_path", "Expected an ordinary file.")
    return p


def inside(path, root, *, directory=None, must_exist=True):
    p = ordinary(path, directory=directory, must_exist=must_exist)
    r = ordinary(root, directory=True)
    try:
        p.relative_to(r)
    except ValueError:
        raise BuddyError("unsafe_path", "Path is outside the registered scope.")
    return p


def digest(path, check=None):
    p = ordinary(path, directory=False)
    before = p.stat()
    h = hashlib.sha256()
    with p.open("rb") as stream:
        while True:
            if check:
                check()
            chunk = stream.read(4 * 1024 * 1024)
            if not chunk:
                break
            h.update(chunk)
    after = p.stat()
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise BuddyError("source_changed", "Source changed while it was read; retry after the writer closes it.")
    return h.hexdigest(), after.st_size, after.st_mtime_ns


def atomic_json(path, value):
    p = ordinary(path, directory=False, must_exist=False)
    p.parent.mkdir(parents=True, exist_ok=True)
    # Existing state files must not be aliases for another file.
    if p.exists() and p.stat().st_nlink > 1:
        raise BuddyError("unsafe_path", "Hardlinked metadata cannot be replaced.")
    temporary = p.parent / ("." + p.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with temporary.open("x", encoding="utf-8", newline="\n") as out:
            out.write(canonical(value))
            out.flush()
            os.fsync(out.fileno())
        os.replace(temporary, p)
    finally:
        if temporary.exists():
            temporary.unlink()


class Engine:
    def __init__(self, state, config, *, runner=None, encoder=None):
        self.state = ordinary(state, directory=True, must_exist=False)
        self.config = dict(config)
        expected = self.config.get("expected_host", "MSI")
        if expected and socket.gethostname().casefold() != expected.casefold():
            raise BuddyError("wrong_host", f"This media worker is configured for {expected}.")
        self.state.mkdir(parents=True, exist_ok=True)
        owner = {"format": "buddy-media-owner-v1", "machine": socket.gethostname(), "user": getpass.getuser()}
        owner_file = self.state / "OWNER.json"
        if owner_file.exists():
            ordinary(owner_file, directory=False)
            if json.loads(owner_file.read_text("utf-8-sig")) != owner:
                raise BuddyError("ownership", "Media state belongs to another user or machine.")
        else:
            if any(self.state.iterdir()):
                raise BuddyError("ownership", "Refusing an existing unowned state directory.")
            atomic_json(owner_file, owner)
        self.db_path = self.state / "media.sqlite3"
        ordinary(self.db_path, directory=False, must_exist=False)
        if self.db_path.exists() and self.db_path.stat().st_nlink > 1:
            raise BuddyError("ownership", "Hardlinked media database is unavailable.")
        self.runner = runner
        self.encoder = encoder
        self._initialize()

    @contextlib.contextmanager
    def connect(self):
        c = sqlite3.connect(str(self.db_path), timeout=15)
        c.row_factory = sqlite3.Row
        c.execute("PRAGMA foreign_keys=ON")
        c.execute("PRAGMA synchronous=FULL")
        try:
            with c:
                yield c
        finally:
            c.close()

    def _initialize(self):
        with self.connect() as c:
            c.executescript("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS roots(root_id TEXT PRIMARY KEY,path TEXT UNIQUE NOT NULL,label TEXT NOT NULL,created REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS jobs(job_id TEXT PRIMARY KEY,request_id TEXT UNIQUE NOT NULL,fingerprint TEXT NOT NULL,kind TEXT NOT NULL,project_id TEXT NOT NULL,params TEXT NOT NULL,status TEXT NOT NULL,control TEXT NOT NULL DEFAULT '',created REAL NOT NULL,updated REAL NOT NULL,progress TEXT NOT NULL DEFAULT '{}',result TEXT,error TEXT);
            CREATE TABLE IF NOT EXISTS assets(asset_id TEXT PRIMARY KEY,sha256 TEXT UNIQUE NOT NULL,bytes INTEGER NOT NULL,metadata TEXT NOT NULL DEFAULT '{}');
            CREATE TABLE IF NOT EXISTS aliases(root_id TEXT NOT NULL,path TEXT NOT NULL,asset_id TEXT NOT NULL,bytes INTEGER NOT NULL,mtime_ns INTEGER NOT NULL,PRIMARY KEY(root_id,path));
            CREATE INDEX IF NOT EXISTS aliases_asset ON aliases(asset_id);
            CREATE TABLE IF NOT EXISTS generated_assets(asset_id TEXT NOT NULL,path TEXT NOT NULL,job_id TEXT NOT NULL,bytes INTEGER NOT NULL,mtime_ns INTEGER NOT NULL,sha256 TEXT NOT NULL,kind TEXT NOT NULL,PRIMARY KEY(asset_id,path));
            CREATE TABLE IF NOT EXISTS samples(asset_id TEXT NOT NULL,profile TEXT NOT NULL,pts REAL NOT NULL,thumbnail TEXT NOT NULL,embedding TEXT NOT NULL,PRIMARY KEY(asset_id,profile,pts));
            CREATE TABLE IF NOT EXISTS indexes(asset_id TEXT NOT NULL,profile TEXT NOT NULL,next_seconds REAL NOT NULL DEFAULT 0,complete INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(asset_id,profile));
            CREATE TABLE IF NOT EXISTS worker(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            """)

    def handle(self, method, params):
        if not isinstance(params, dict):
            raise BuddyError("invalid_request", "Parameters must be an object.")
        methods = {"get_capabilities": self.capabilities, "get_preflight": self.get_preflight, "register_source_root": self.register_root,
                   "list_source_roots": self.list_roots, "inventory": self.inventory,
                   "submit_job": self.submit, "list_jobs": self.list_jobs, "get_job": self.get_job,
                   "pause_job": lambda p: self.control(p, "pause"), "resume_job": lambda p: self.control(p, "resume"),
                   "cancel_job": lambda p: self.control(p, "cancel"), "get_results": self.get_results}
        if method not in methods:
            raise BuddyError("unsupported", "Unknown media worker method.")
        return methods[method](params)

    def get_preflight(self, params=None):
        path = self.state / "gpu-preflight.json"
        if not path.exists():
            return {"passed": False, "reason": "No GPU preflight receipt in this worker state."}
        ordinary(path, directory=False)
        if path.stat().st_size > 65536:
            raise BuddyError("invalid_receipt", "GPU preflight receipt exceeds its bound.")
        result = json.loads(path.read_text("utf-8"))
        if result.get("model_revision") != self.config.get("model_revision"):
            return {"passed": False, "reason": "GPU receipt belongs to another model revision."}
        return result

    def capabilities(self, params=None):
        with self.connect() as c:
            row = c.execute("SELECT value FROM worker WHERE key='heartbeat'").fetchone()
            heartbeat = json.loads(row[0]) if row else None
        tools = self.tools()
        model_path = self.config.get("model_path", "")
        fresh = bool(heartbeat and time.time() - heartbeat.get("observed_at", 0) < 20)
        return {"format": "buddy-media-capabilities-v1", "host": socket.gethostname(), "version": __version__,
                "worker_online": fresh, "heartbeat": heartbeat,
                "tools": {k: bool(p and Path(p).is_file()) for k, p in tools.items()},
                "model_configured": bool(model_path and Path(model_path).is_dir()),
                "model_revision": self.config.get("model_revision"),
                "preflight": self.get_preflight(),
                "job_kinds": sorted(KINDS), "methods": ["get_preflight", "register_source_root", "list_source_roots", "inventory", "submit_job", "list_jobs", "get_job", "pause_job", "resume_job", "cancel_job", "get_results"]}

    def tools(self):
        root = Path(self.config.get("downloader_root", r"C:\Users\jlewi\Documents\VideoDownloader")) / "Tools"
        return {name: str(self.config.get(name + "_path", root / (name + ".exe"))) for name in ("ffmpeg", "ffprobe", "yt-dlp", "deno")}

    def register_root(self, params):
        if set(params) - {"path", "label", "request_id"}:
            raise BuddyError("invalid_request", "Unknown source-root field.")
        p = ordinary(params.get("path", ""), directory=True)
        allowed = self.config.get("source_allowlist", [])
        if not allowed or not any(self._contained(p, Path(a)) for a in allowed):
            raise BuddyError("scope_required", "Select a source folder inside an administrator-configured media parent.")
        if self._contained(p, self.state) or self._contained(self.state, p):
            raise BuddyError("unsafe_path", "Worker state and source footage must be separate scopes.")
        label = str(params.get("label") or p.name)[:200]
        root_id = "root-" + hashlib.sha256(str(p).casefold().encode()).hexdigest()[:24]
        with self.connect() as c:
            c.execute("INSERT OR IGNORE INTO roots VALUES(?,?,?,?)", (root_id, str(p), label, time.time()))
        return {"root_id": root_id, "path": str(p), "label": label}

    @staticmethod
    def _contained(p, root):
        try:
            p.relative_to(Path(os.path.abspath(root)))
            return True
        except ValueError:
            return False

    def list_roots(self, params):
        with self.connect() as c:
            return {"roots": [dict(r) for r in c.execute("SELECT * FROM roots ORDER BY created")]}

    def root(self, root_id):
        identifier(root_id, "source root")
        with self.connect() as c:
            row = c.execute("SELECT * FROM roots WHERE root_id=?", (root_id,)).fetchone()
        if not row:
            raise BuddyError("unknown_root", "Source folder is not registered.")
        ordinary(row["path"], directory=True)
        return dict(row)

    def media_files(self, root_id):
        root = Path(self.root(root_id)["path"])
        pending = [root]
        while pending:
            folder = pending.pop()
            ordinary(folder, directory=True)
            for p in sorted(folder.iterdir(), key=lambda x: x.name.casefold()):
                try:
                    ordinary(p)
                except BuddyError:
                    continue
                if p.is_dir():
                    pending.append(p)
                elif p.suffix.lower() in MEDIA:
                    yield p

    def inventory(self, params):
        root_id = params.get("root_id")
        limit = max(1, min(int(params.get("limit", 200)), 2000))
        files = []
        for p in self.media_files(root_id):
            if len(files) >= limit:
                return {"files": files, "truncated": True}
            s = p.stat()
            files.append({"root_id": root_id, "relative_path": str(p.relative_to(self.root(root_id)["path"])), "bytes": s.st_size, "modified_at": s.st_mtime})
        return {"files": files, "truncated": False}

    def submit(self, params):
        if set(params) - {"request_id", "kind", "project_id", "params"}:
            raise BuddyError("invalid_request", "Unknown job field.")
        request_id = identifier(params.get("request_id"), "request ID")
        kind = params.get("kind")
        project_id = params.get("project_id")
        if kind not in KINDS or not isinstance(project_id, str) or not project_id.strip() or len(project_id) > 300:
            raise BuddyError("invalid_request", "A supported kind and exact project identity are required.")
        args = params.get("params", {})
        self.validate_params(kind, args)
        payload = canonical({"kind": kind, "project_id": project_id, "params": args})
        if len(payload) > 65536:
            raise BuddyError("invalid_request", "Job request is too large.")
        fingerprint = hashlib.sha256(payload.encode()).hexdigest()
        with self.connect() as c:
            c.execute("BEGIN IMMEDIATE")
            existing = c.execute("SELECT * FROM jobs WHERE request_id=?", (request_id,)).fetchone()
            if existing:
                if existing["fingerprint"] != fingerprint:
                    raise BuddyError("request_conflict", "This request ID already names different work.")
                return self._job(existing)
            job_id = uuid.uuid4().hex
            now = time.time()
            c.execute("INSERT INTO jobs(job_id,request_id,fingerprint,kind,project_id,params,status,created,updated) VALUES(?,?,?,?,?,?,'queued',?,?)", (job_id, request_id, fingerprint, kind, project_id, canonical(args), now, now))
        return self.get_job({"job_id": job_id})

    def validate_params(self, kind, args):
        if not isinstance(args, dict):
            raise BuddyError("invalid_request", "Job parameters must be an object.")
        fields = {"download": {"urls"}, "index": {"root_ids", "sample_seconds", "asset_ids", "start_seconds", "end_seconds"},
                  "search": {"query", "root_ids", "limit"}, "preview": {"asset_id", "start_seconds", "end_seconds"},
                  "extract": {"asset_id", "start_seconds", "end_seconds"}, "organize": {"asset_ids", "group_by", "tag"}}
        if set(args) - fields[kind]:
            raise BuddyError("invalid_request", "Unknown or unsupported job parameter.")
        if kind == "download":
            urls = args.get("urls")
            if not isinstance(urls, list) or not 1 <= len(urls) <= 100:
                raise BuddyError("invalid_request", "Supply 1–100 individual video URLs.")
            from .jobs import validate_url
            for url in urls:
                validate_url(url)
        if kind == "index":
            if not args.get("root_ids"):
                raise BuddyError("invalid_request", "Select at least one source root.")
            interval = args.get("sample_seconds", 2)
            if isinstance(interval, bool) or not isinstance(interval, (int, float)) or not math.isfinite(interval) or not .5 <= interval <= 30:
                raise BuddyError("invalid_request", "Sampling interval must be 0.5–30 seconds.")
            start, end = args.get("start_seconds", 0), args.get("end_seconds")
            if isinstance(start, bool) or not isinstance(start, (int, float)) or not math.isfinite(start) or start < 0 or end is not None and (isinstance(end, bool) or not isinstance(end, (int, float)) or not math.isfinite(end) or end <= start):
                raise BuddyError("invalid_request", "Invalid finer-indexing time range.")
            if "asset_ids" in args:
                if not isinstance(args["asset_ids"], list) or len(args["asset_ids"]) > 1000:
                    raise BuddyError("invalid_request", "Invalid selected asset identities.")
                for asset_id in args["asset_ids"]:
                    identifier(asset_id, "asset")
        if kind in ("index", "search") and "root_ids" in args:
            if not isinstance(args["root_ids"], list) or not 1 <= len(args["root_ids"]) <= 100:
                raise BuddyError("invalid_request", "Invalid selected source roots.")
            for root_id in args["root_ids"]:
                self.root(root_id)
        if kind == "search":
            if not isinstance(args.get("query"), str) or not 1 <= len(args["query"].strip()) <= 1000:
                raise BuddyError("invalid_request", "Enter a visual search phrase.")
        if kind in ("extract", "preview"):
            identifier(args.get("asset_id"), "asset")
            start, end = args.get("start_seconds", 0), args.get("end_seconds")
            if isinstance(start, bool) or isinstance(end, bool) or not isinstance(start, (int, float)) or not isinstance(end, (int, float)) or not math.isfinite(start) or not math.isfinite(end) or start < 0 or end <= start or end - start > (60 if kind == "preview" else 3600):
                raise BuddyError("invalid_request", "Invalid clip range; previews are at most 60 seconds and extracts at most one hour.")
        if kind == "organize":
            ids = args.get("asset_ids")
            if not isinstance(ids, list) or not 1 <= len(ids) <= 1000:
                raise BuddyError("invalid_request", "Select 1–1000 assets.")
            for asset_id in ids:
                identifier(asset_id, "asset")
            if args.get("group_by", "source") not in ("date", "source", "tag"):
                raise BuddyError("invalid_request", "Organization uses date, source, or tag.")
            if args.get("group_by") == "tag" and not str(args.get("tag", "")).strip():
                raise BuddyError("invalid_request", "Enter an organization tag.")

    @staticmethod
    def _job(row):
        value = dict(row)
        for key in ("params", "progress", "result", "error"):
            value[key] = json.loads(value[key]) if value[key] else None
        value.pop("fingerprint", None)
        return value

    def get_job(self, params):
        job_id = identifier(params.get("job_id"), "job")
        with self.connect() as c:
            row = c.execute("SELECT * FROM jobs WHERE job_id=?", (job_id,)).fetchone()
        if not row:
            raise BuddyError("unknown_job", "No job has this identity.")
        return self._job(row)

    def list_jobs(self, params):
        limit = max(1, min(int(params.get("limit", 100)), 1000))
        with self.connect() as c:
            if params.get("project_id"):
                rows = c.execute("SELECT * FROM jobs WHERE project_id=? ORDER BY created DESC LIMIT ?", (params["project_id"], limit))
            else:
                rows = c.execute("SELECT * FROM jobs ORDER BY created DESC LIMIT ?", (limit,))
            return {"jobs": [self._job(r) for r in rows]}

    def control(self, params, action):
        job = self.get_job(params)
        status, control = job["status"], job["control"]
        if action == "resume" and status in ("paused", "failed", "needs_review"):
            if job["kind"] == "index" and status in ("failed", "needs_review"):
                plan = self.job_dir(job["job_id"]) / "index-plan.json"
                if plan.exists():
                    ordinary(plan, directory=False)
                    if plan.stat().st_nlink > 1:
                        raise BuddyError("unsafe_path", "Index checkpoint must not be hardlinked.")
                    plan.unlink()  # Retain committed content indexes; re-enumerate failed sources.
            status, control = "queued", ""
        elif action == "pause" and status == "queued":
            status, control = "paused", "pause"
        elif action == "pause" and status == "running":
            control = "pause"
        elif action == "cancel" and status not in TERMINAL:
            control = "cancel"
            if status != "running":
                status = "cancelled"
        with self.connect() as c:
            c.execute("UPDATE jobs SET status=?,control=?,updated=? WHERE job_id=?", (status, control, time.time(), job["job_id"]))
            if action == "resume" and status == "queued":
                c.execute("UPDATE jobs SET result=NULL,error=NULL WHERE job_id=?", (job["job_id"],))
        return self.get_job(params)

    def check_control(self, job_id):
        job = self.get_job({"job_id": job_id})
        if job["control"] == "cancel":
            raise JobCancelled()
        if job["control"] == "pause":
            raise JobPaused()

    def progress(self, job_id, **updates):
        with self.connect() as c:
            row = c.execute("SELECT progress FROM jobs WHERE job_id=?", (job_id,)).fetchone()
            value = json.loads(row[0])
            value.update(updates)
            c.execute("UPDATE jobs SET progress=?,updated=? WHERE job_id=?", (canonical(value), time.time(), job_id))

    def heartbeat(self):
        value = {"observed_at": time.time(), "pid": os.getpid(), "host": socket.gethostname()}
        with self.connect() as c:
            c.execute("INSERT OR REPLACE INTO worker VALUES('heartbeat',?)", (canonical(value),))

    def job_dir(self, job_id):
        identifier(job_id, "job")
        path = self.state / "jobs" / job_id
        ordinary(path, directory=True, must_exist=False)
        path.mkdir(parents=True, exist_ok=True)
        return path

    def complete(self, job, result):
        manifest = {"format": FORMAT, "job_id": job["job_id"], "project_id": job["project_id"], "kind": job["kind"], "completed_at": time.time(), "assets": [], "results": []}
        manifest.update(result)
        path = self.job_dir(job["job_id"]) / "manifest.json"
        atomic_json(path, manifest)
        stored = {"manifest_path": str(path), "asset_count": len(manifest.get("assets", [])), "result_count": len(manifest.get("results", []))}
        status = "needs_review" if manifest.get("failures") else "completed"
        generated = []
        for asset in manifest.get("assets", []):
            if asset.get("kind") not in ("video", "preview"):
                continue
            candidate = inside(asset["absolute_path"], self.job_dir(job["job_id"]), directory=False)
            if candidate.relative_to(self.job_dir(job["job_id"])).as_posix() != asset.get("relative_path"):
                raise BuddyError("invalid_receipt", "Generated media path does not match its manifest.")
            sha, size, modified = digest(candidate, lambda: self.check_control(job["job_id"]))
            if sha != asset.get("sha256") or size != asset.get("bytes") or asset.get("asset_id") != "asset-" + sha:
                raise BuddyError("output_changed", "Generated media changed before the job committed.")
            generated.append((asset["asset_id"], str(candidate), job["job_id"], size, modified, sha, asset["kind"]))
        with self.connect() as c:
            c.execute("UPDATE jobs SET status=?,control='',result=?,error=NULL,updated=? WHERE job_id=?", (status, canonical(stored), time.time(), job["job_id"]))
            c.executemany("INSERT OR REPLACE INTO generated_assets VALUES(?,?,?,?,?,?,?)", generated)

    def get_results(self, params):
        job = self.get_job(params)
        result = job.get("result")
        if not result:
            return {"job": job, "manifest": None, "manifest_path": None}
        path = inside(result["manifest_path"], self.job_dir(job["job_id"]), directory=False)
        if path.stat().st_size > _MAX_JSON:
            raise BuddyError("invalid_receipt", "Manifest exceeds its bound.")
        return {"job": job, "manifest": json.loads(path.read_text("utf-8")), "manifest_path": str(path)}

    def asset_path(self, asset_id, root_ids=None):
        identifier(asset_id, "asset")
        with self.connect() as c:
            aliases = list(c.execute("SELECT * FROM aliases WHERE asset_id=?", (asset_id,)))
        for alias in aliases:
            if root_ids and alias["root_id"] not in root_ids:
                continue
            try:
                p = inside(alias["path"], self.root(alias["root_id"])["path"], directory=False)
                s = p.stat()
                if (s.st_size, s.st_mtime_ns) == (alias["bytes"], alias["mtime_ns"]):
                    return p, dict(alias)
            except (BuddyError, OSError):
                continue
        if not root_ids:
            # Job outputs are a separate provenance scope, never a source-root
            # registration. Only committed manifest entries can become inputs.
            with self.connect() as c:
                generated = list(c.execute("SELECT * FROM generated_assets WHERE asset_id=?", (asset_id,)))
            for output in generated:
                try:
                    receipt = self.get_results({"job_id": output["job_id"]})
                    if receipt["job"]["status"] not in ("completed", "needs_review") or not receipt["manifest"]:
                        continue
                    p = inside(output["path"], self.job_dir(output["job_id"]), directory=False)
                    relative = p.relative_to(self.job_dir(output["job_id"])).as_posix()
                    matching = [a for a in receipt["manifest"].get("assets", [])
                                if a.get("asset_id") == asset_id and a.get("absolute_path") == str(p)
                                and a.get("relative_path") == relative and a.get("sha256") == output["sha256"]
                                and a.get("bytes") == output["bytes"] and a.get("kind") in ("video", "preview")]
                    if not matching:
                        continue
                    sha, size, modified = digest(p)
                    if sha != output["sha256"] or size != output["bytes"] or asset_id != "asset-" + sha:
                        continue
                    return p, {"root_id": None, "path": str(p), "asset_id": asset_id,
                               "bytes": size, "mtime_ns": modified, "generated": True,
                               "job_id": output["job_id"], "label": "Buddy outputs"}
                except (BuddyError, OSError, ValueError, KeyError):
                    continue
        raise BuddyError("source_changed", "Source is unavailable or changed. Re-index original footage or review the job that produced this output.")

    def register_asset(self, path, root_id, check=None, metadata=None):
        p = inside(path, self.root(root_id)["path"], directory=False)
        sha, size, modified = digest(p, check)
        asset_id = "asset-" + sha
        with self.connect() as c:
            c.execute("INSERT OR IGNORE INTO assets VALUES(?,?,?,?)", (asset_id, sha, size, canonical(metadata or {})))
            c.execute("INSERT OR REPLACE INTO aliases VALUES(?,?,?,?,?)", (root_id, str(p), asset_id, size, modified))
        return asset_id

    def run_next(self):
        """Execute one bounded unit; index jobs yield between source-time chunks."""
        with self.connect() as c:
            c.execute("BEGIN IMMEDIATE")
            row = c.execute("SELECT * FROM jobs WHERE status='queued' ORDER BY CASE kind WHEN 'search' THEN 0 WHEN 'preview' THEN 1 WHEN 'extract' THEN 2 WHEN 'index' THEN 9 ELSE 3 END,created LIMIT 1").fetchone()
            if not row:
                return False
            c.execute("UPDATE jobs SET status='running',updated=? WHERE job_id=?", (time.time(), row["job_id"]))
        job = self._job(row)
        try:
            self.check_control(job["job_id"])
            from .jobs import execute
            result = execute(self, job)
            if result is None:
                with self.connect() as c:
                    c.execute("UPDATE jobs SET status='queued',updated=? WHERE job_id=?", (time.time(), job["job_id"]))
            else:
                self.complete(job, result)
        except (JobPaused, JobCancelled) as error:
            with self.connect() as c:
                c.execute("UPDATE jobs SET status=?,updated=? WHERE job_id=?", ("paused" if isinstance(error, JobPaused) else "cancelled", time.time(), job["job_id"]))
        except Exception as error:
            payload = {"code": getattr(error, "code", "worker_failed"), "message": str(error)[:3000]}
            with self.connect() as c:
                c.execute("UPDATE jobs SET status='failed',error=?,updated=? WHERE job_id=?", (canonical(payload), time.time(), job["job_id"]))
        return True

    def recover(self):
        with self.connect() as c:
            c.execute("UPDATE jobs SET status=CASE control WHEN 'cancel' THEN 'cancelled' WHEN 'pause' THEN 'paused' ELSE 'queued' END,updated=? WHERE status='running'", (time.time(),))


@contextlib.contextmanager
def worker_lock(state):
    """Native process-held lock; a crashed worker releases it without a stale-PID override."""
    p = ordinary(Path(state) / "worker.lock", directory=False, must_exist=False)
    if p.exists() and p.stat().st_nlink > 1:
        raise BuddyError("ownership", "Worker lock must not be hardlinked.")
    with p.open("a+b") as stream:
        if p.stat().st_size == 0:
            stream.write(b"0")
            stream.flush()
        stream.seek(0)
        try:
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            raise BuddyError("worker_running", "Another media worker owns this state.")
        try:
            yield
        finally:
            stream.seek(0)
            if os.name == "nt":
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(stream.fileno(), fcntl.LOCK_UN)
