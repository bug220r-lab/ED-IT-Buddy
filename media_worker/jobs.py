"""Allowlisted media operations. Tools receive arguments as arrays, never shell text."""
from __future__ import annotations

import hashlib
import json
import math
import os
import re
import shutil
import subprocess
import time
import urllib.parse
from pathlib import Path

from .core import BuddyError, JobCancelled, JobPaused, atomic_json, canonical, digest, inside, ordinary


def validate_url(url):
    if not isinstance(url, str) or len(url) > 4000 or any(c.isspace() or ord(c) < 32 for c in url):
        raise BuddyError("invalid_url", "Supply a complete individual video URL.")
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme not in ("http", "https") or not parsed.hostname or parsed.username or parsed.password:
        raise BuddyError("invalid_url", "Only HTTP(S) video URLs without embedded credentials are accepted.")
    host = parsed.hostname.lower()
    if host in ("localhost", "127.0.0.1", "::1") or host.endswith(".local"):
        raise BuddyError("invalid_url", "Local network URLs are not video sources.")
    if host.endswith("youtube.com") and not (parsed.path.startswith(("/shorts/", "/live/", "/embed/")) or parsed.path == "/watch" and urllib.parse.parse_qs(parsed.query).get("v")):
        raise BuddyError("invalid_url", "Use an individual YouTube video, not a channel or playlist.")
    if host in ("x.com", "twitter.com", "www.x.com", "www.twitter.com") and "/status/" not in parsed.path:
        raise BuddyError("invalid_url", "Use an individual post, not a profile or feed.")
    if host.endswith("reddit.com") and "/comments/" not in parsed.path:
        raise BuddyError("invalid_url", "Use an individual Reddit post.")
    return parsed


def _slug(value):
    value = re.sub(r"[^\w .-]+", "_", str(value), flags=re.UNICODE).strip(" .")[:100]
    if not value or value.split(".")[0].upper() in {"CON", "PRN", "AUX", "NUL", *["COM" + str(x) for x in range(1, 10)], *["LPT" + str(x) for x in range(1, 10)]}:
        return "media"
    return value


class _WindowsJob:
    """Keep FFmpeg/yt-dlp descendants owned by this tool invocation."""
    def __init__(self, process):
        self.handle = None
        if os.name != "nt":
            return
        import ctypes
        from ctypes import wintypes
        class BASIC(ctypes.Structure):
            _fields_ = [("PerProcessUserTimeLimit", ctypes.c_int64), ("PerJobUserTimeLimit", ctypes.c_int64), ("LimitFlags", wintypes.DWORD), ("MinimumWorkingSetSize", ctypes.c_size_t), ("MaximumWorkingSetSize", ctypes.c_size_t), ("ActiveProcessLimit", wintypes.DWORD), ("Affinity", ctypes.c_size_t), ("PriorityClass", wintypes.DWORD), ("SchedulingClass", wintypes.DWORD)]
        class IO(ctypes.Structure):
            _fields_ = [(name, ctypes.c_uint64) for name in ("ReadOperationCount", "WriteOperationCount", "OtherOperationCount", "ReadTransferCount", "WriteTransferCount", "OtherTransferCount")]
        class EXTENDED(ctypes.Structure):
            _fields_ = [("BasicLimitInformation", BASIC), ("IoInfo", IO), ("ProcessMemoryLimit", ctypes.c_size_t), ("JobMemoryLimit", ctypes.c_size_t), ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t)]
        k = ctypes.WinDLL("kernel32", use_last_error=True)
        k.CreateJobObjectW.restype = wintypes.HANDLE
        k.CreateJobObjectW.argtypes = (ctypes.c_void_p, wintypes.LPCWSTR)
        k.SetInformationJobObject.argtypes = (wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD)
        k.AssignProcessToJobObject.argtypes = (wintypes.HANDLE, wintypes.HANDLE)
        k.CloseHandle.argtypes = (wintypes.HANDLE,)
        handle = k.CreateJobObjectW(None, None)
        info = EXTENDED()
        info.BasicLimitInformation.LimitFlags = 0x2000  # KILL_ON_JOB_CLOSE
        if not handle or not k.SetInformationJobObject(handle, 9, ctypes.byref(info), ctypes.sizeof(info)) or not k.AssignProcessToJobObject(handle, wintypes.HANDLE(int(process._handle))):
            if handle:
                k.CloseHandle(handle)
            process.kill()
            raise BuddyError("process_ownership", "Unable to retain ownership of the media-tool process tree.")
        self.handle, self.kernel = handle, k

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None


def tool(engine, job, name, args, *, stdout_path=None, timeout=3600):
    if engine.runner:
        engine.check_control(job["job_id"])
        return engine.runner(engine, job, name, list(args), stdout_path=stdout_path, timeout=timeout)
    exe = ordinary(engine.tools().get(name, ""), directory=False)
    job_root = engine.job_dir(job["job_id"])
    log_path = job_root / "tools.log"
    ordinary(log_path, directory=False, must_exist=False)
    if log_path.exists() and log_path.stat().st_nlink > 1:
        raise BuddyError("unsafe_path", "Tool log is hardlinked.")
    # Each operation has bounded stderr. Preserve a previous large log instead of growing it.
    if log_path.exists() and log_path.stat().st_size > 16 * 1024 * 1024:
        raise BuddyError("log_limit", "Tool log reached its size bound; review this job before retrying.")
    with log_path.open("ab") as log:
        output = None
        process = None
        lifetime = None
        try:
            if stdout_path:
                output = inside(stdout_path, job_root, directory=False, must_exist=False).open("wb")
            process = subprocess.Popen([str(exe), *map(str, args)], stdin=subprocess.DEVNULL, stdout=output or log, stderr=log, cwd=str(job_root), creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            lifetime = _WindowsJob(process)
            started = time.monotonic()
            while process.poll() is None:
                engine.check_control(job["job_id"])
                if time.monotonic() - started > timeout:
                    raise BuddyError("tool_timeout", f"{name} exceeded its time bound.")
                if log.tell() > 32 * 1024 * 1024:
                    raise BuddyError("log_limit", "Media tool exceeded its log bound.")
                if output and output.tell() > 4 * 1024 * 1024:
                    raise BuddyError("output_limit", "Media metadata exceeded its output bound.")
                time.sleep(.2)
            if process.returncode:
                raise BuddyError("tool_failed", f"{name} exited with code {process.returncode}. See the job tool log.")
            return 0
        finally:
            if process and process.poll() is None:
                if lifetime:
                    lifetime.close()
                process.kill()
                process.wait(timeout=10)
            if lifetime:
                lifetime.close()
            if output:
                output.close()


def probe(engine, job, path):
    target = engine.job_dir(job["job_id"]) / "probe.json"
    tool(engine, job, "ffprobe", ["-v", "error", "-show_streams", "-show_format", "-of", "json", str(path)], stdout_path=target, timeout=120)
    if target.stat().st_size > 1024 * 1024:
        raise BuddyError("probe_failed", "Unexpectedly large media metadata.")
    info = json.loads(target.read_text("utf-8-sig"))
    streams = info.get("streams", [])
    videos = [x for x in streams if x.get("codec_type") == "video"]
    if len(videos) != 1:
        raise BuddyError("unsupported_media", "Expected exactly one video stream.")
    duration = float(info.get("format", {}).get("duration", 0))
    if not math.isfinite(duration) or duration <= 0:
        raise BuddyError("unsupported_media", "Source has no finite positive duration.")
    return {"duration_seconds": duration, "width": int(videos[0].get("width", 0)), "height": int(videos[0].get("height", 0)), "video_codec": videos[0].get("codec_name"), "pixel_format": videos[0].get("pix_fmt"), "audio_streams": [x for x in streams if x.get("codec_type") == "audio"], "frame_rate": videos[0].get("avg_frame_rate")}


def verify(engine, job, output, expected_duration=None):
    meta = probe(engine, job, output)
    if expected_duration is not None and abs(meta["duration_seconds"] - expected_duration) > max(1, expected_duration * .001):
        raise BuddyError("verification_failed", "Output duration differs from the requested source range.")
    if meta["video_codec"] != "h264" or meta["pixel_format"] != "yuv420p" or any(a.get("codec_name") != "aac" for a in meta["audio_streams"]):
        raise BuddyError("verification_failed", "Editor output is not H.264/yuv420p with AAC audio.")
    tool(engine, job, "ffmpeg", ["-v", "error", "-xerror", "-nostdin", "-i", str(output), "-map", "0:v:0", "-map", "0:a?", "-f", "null", "-"], timeout=max(120, int(meta["duration_seconds"] * 5)))
    return meta


def artifact(engine, job, path, kind, **details):
    p = inside(path, engine.job_dir(job["job_id"]), directory=False)
    sha, size, _ = digest(p, lambda: engine.check_control(job["job_id"]))
    if not size:
        raise BuddyError("verification_failed", "An empty file is not a completed output.")
    value = {"asset_id": "asset-" + sha, "relative_path": p.relative_to(engine.job_dir(job["job_id"])).as_posix(), "absolute_path": str(p), "bytes": size, "sha256": sha, "kind": kind}
    value.update(details)
    return value


def download(engine, job):
    job_root = engine.job_dir(job["job_id"])
    checkpoint = job_root / "download-checkpoint.json"
    completed = json.loads(checkpoint.read_text("utf-8")) if checkpoint.exists() else {}
    failures = []
    for number, url in enumerate(job["params"]["urls"]):
        engine.check_control(job["job_id"])
        key = hashlib.sha256(url.encode()).hexdigest()
        if key in completed:
            # A receipt is reused only if its actual output bytes are unchanged.
            if all(digest(inside(a["absolute_path"], job_root, directory=False))[0] == a["sha256"] for a in completed[key]):
                continue
            raise BuddyError("output_changed", "A previously completed download output changed.")
        directory = job_root / "downloads" / key[:20]
        directory.mkdir(parents=True, exist_ok=True)
        path_list = directory / "paths.txt"
        try:
            parsed = validate_url(url)
            host = parsed.hostname.lower()
            # Resolve unknown sites first, refusing collections without downloading media.
            if not any(host == x or host.endswith("." + x) for x in ("youtube.com", "youtu.be", "reddit.com", "redd.it", "twitter.com", "x.com", "t.co")):
                inspection = directory / "source-probe.json"
                tool(engine, job, "yt-dlp", ["--ignore-config", "--no-plugin-dirs", "--flat-playlist", "--skip-download", "--dump-single-json", "--socket-timeout", "30", "--", url], stdout_path=inspection, timeout=180)
                if inspection.stat().st_size > 4 * 1024 * 1024:
                    raise BuddyError("collection_refused", "Source metadata exceeds an individual-video bound.")
                info = json.loads(inspection.read_text("utf-8-sig"))
                if info.get("_type") in ("playlist", "multi_video"):
                    raise BuddyError("collection_refused", "Use an individual video URL.")
            args = ["--ignore-config", "--no-plugin-dirs", "--no-playlist", "--playlist-items", "1", "--match-filters", "!is_live", "--format", "bv*+ba/b", "--merge-output-format", "mp4/mkv", "--ffmpeg-location", str(Path(engine.tools()["ffmpeg"]).parent), "--windows-filenames", "--trim-filenames", "170", "--output", str(directory / "%(upload_date,release_date|undated)s_%(uploader_id,uploader|unknown).24s_%(title).70s_[%(id)s].%(ext)s"), "--write-info-json", "--write-description", "--write-url-link", "--no-write-playlist-metafiles", "--embed-metadata", "--download-archive", str(directory / "archive.txt"), "--continue", "--no-overwrites", "--socket-timeout", "30", "--retries", "3", "--fragment-retries", "3", "--abort-on-unavailable-fragments", "--newline", "--no-colors", "--print-to-file", "after_move:filepath", str(path_list)]
            deno = engine.tools().get("deno")
            if deno and Path(deno).is_file():
                args += ["--js-runtimes", "deno:" + deno]
            if host.endswith("youtube.com") or host == "youtu.be":
                args += ["--http-chunk-size", "1M"]
            # yt-dlp commits its archive before our conversion. Preserve its completed
            # path receipt so a retry repairs that exact source without fetching twice.
            previous_paths = []
            if path_list.exists():
                inside(path_list, directory, directory=False)
                previous_paths = [line.strip() for line in path_list.read_text("utf-8-sig").splitlines() if line.strip()]
            if not previous_paths:
                tool(engine, job, "yt-dlp", [*args, "--", url], timeout=12 * 3600)
            if not path_list.exists():
                raise BuddyError("no_media", "Source was skipped or no finished media path was reported.")
            paths = [inside(line.strip(), directory, directory=False) for line in path_list.read_text("utf-8-sig").splitlines() if line.strip()]
            if len(paths) != 1:
                raise BuddyError("collection_refused", "Expected one finished media file per URL.")
            source = paths[0]
            metadata = probe(engine, job, source)
            output = directory / (source.stem + "_EDIT.mp4")
            if not output.exists():
                partial = directory / ".editor-partial.mp4"
                if partial.exists():
                    inside(partial, job_root, directory=False).unlink()
                args = ["-hide_banner", "-v", "warning", "-xerror", "-nostdin", "-n", "-i", str(source), "-map", "0:v:0", "-map", "0:a?", "-map_metadata", "0", "-c:v"]
                args += ["copy"] if metadata["video_codec"] == "h264" and metadata["pixel_format"] == "yuv420p" else ["libx264", "-preset", "fast", "-crf", "18", "-pix_fmt", "yuv420p"]
                copy_audio = all(a.get("codec_name") == "aac" and a.get("profile") == "LC" for a in metadata["audio_streams"])
                args += ["-c:a", "copy"] if copy_audio else ["-c:a", "aac", "-b:a", "320k"]
                tool(engine, job, "ffmpeg", [*args, "-movflags", "+faststart", str(partial)], timeout=12 * 3600)
                verify(engine, job, partial, metadata["duration_seconds"])
                partial.rename(output)
            verified = verify(engine, job, output, metadata["duration_seconds"])
            final = artifact(engine, job, output, "video", source_url=url, media=verified)
            receipts = [final]
            for extension in (".info.json", ".description", ".url"):
                sidecar = source.with_suffix(extension)
                if sidecar.exists():
                    final_sidecar = output.with_suffix(extension)
                    inside(sidecar, directory, directory=False)
                    inside(final_sidecar, directory, directory=False, must_exist=False)
                    if final_sidecar.exists():
                        if digest(final_sidecar)[0] != digest(sidecar)[0]:
                            raise BuddyError("collision", "Editor sidecar exists with different source metadata.")
                    else:
                        shutil.copyfile(sidecar, final_sidecar)
                    receipts.append(artifact(engine, job, final_sidecar, "source_metadata", source_url=url))
            processing = output.with_suffix(".processing.json")
            atomic_json(processing, {"format": "buddy-processing-v1", "source_url": url, "original_path": str(source), "output_sha256": final["sha256"], "original_preserved": True, "full_decode": "passed", "media": verified})
            receipts.append(artifact(engine, job, processing, "processing_receipt", source_url=url))
            completed[key] = receipts
            atomic_json(checkpoint, completed)
        except (JobPaused, JobCancelled):
            raise
        except Exception as error:
            failures.append({"source_url": url, "code": getattr(error, "code", "download_failed"), "message": str(error)[:1000]})
        engine.progress(job["job_id"], completed_items=len(completed), total_items=len(job["params"]["urls"]), current_url=url)
    return {"assets": [a for rows in completed.values() for a in rows], "failures": failures, "originals_preserved": True}


def clip(engine, job):
    params = job["params"]
    source, alias = engine.asset_path(params["asset_id"])
    # A short preview uses the unchanged indexed file observation; extracting an
    # asset for delivery additionally re-hashes its full original content.
    if job["kind"] == "extract":
        sha, _, _ = digest(source, lambda: engine.check_control(job["job_id"]))
        if "asset-" + sha != params["asset_id"]:
            raise BuddyError("source_changed", "Source content changed since indexing.")
    metadata = probe(engine, job, source)
    start, end = float(params["start_seconds"]), float(params["end_seconds"])
    if end > metadata["duration_seconds"] + .1:
        raise BuddyError("invalid_range", "Clip ends after the source recording.")
    output = engine.job_dir(job["job_id"]) / ("preview.mp4" if job["kind"] == "preview" else "clip.mp4")
    if output.exists():
        inside(output, engine.job_dir(job["job_id"]), directory=False).unlink()
    args = ["-hide_banner", "-v", "warning", "-xerror", "-nostdin", "-n", "-ss", str(start), "-i", str(source), "-t", str(end - start), "-map", "0:v:0", "-map", "0:a?", "-threads", "2", "-c:v", "libx264", "-preset", "fast", "-crf", "23" if job["kind"] == "preview" else "18", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k"]
    if job["kind"] == "preview":
        args += ["-vf", "scale='min(960,iw)':-2"]
    tool(engine, job, "ffmpeg", [*args, "-movflags", "+faststart", str(output)], timeout=max(180, int((end - start) * 10)))
    verified = verify(engine, job, output, end - start)
    after = source.stat()
    if (after.st_size, after.st_mtime_ns) != (alias["bytes"], alias["mtime_ns"]):
        raise BuddyError("source_changed", "Source changed while the selected range was decoded.")
    return {"assets": [artifact(engine, job, output, "preview" if job["kind"] == "preview" else "video", source_asset_id=params["asset_id"], start_seconds=start, end_seconds=end, media=verified)]}


def organize(engine, job):
    root = engine.job_dir(job["job_id"]) / "organized"
    root.mkdir(parents=True, exist_ok=True)
    outputs, mapping, duplicates = [], [], []
    seen = {}
    import datetime
    for asset_id in job["params"]["asset_ids"]:
        engine.check_control(job["job_id"])
        source, alias = engine.asset_path(asset_id)
        sha, size, modified = digest(source, lambda: engine.check_control(job["job_id"]))
        if "asset-" + sha != asset_id:
            raise BuddyError("source_changed", "Selected source changed; re-index before organizing.")
        if sha in seen:
            duplicates.append({"asset_id": asset_id, "same_as": seen[sha]})
            continue
        group_by = job["params"].get("group_by", "source")
        if group_by == "date":
            group = datetime.datetime.fromtimestamp(source.stat().st_mtime).strftime("%Y-%m-%d")
        elif group_by == "tag":
            group = _slug(job["params"]["tag"])
        else:
            group = _slug(alias["label"] if alias.get("generated") else engine.root(alias["root_id"])["label"])
        destination = root / group / (sha[:12] + "_" + _slug(source.stem) + source.suffix.lower())
        destination.parent.mkdir(parents=True, exist_ok=True)
        inside(destination, root, directory=False, must_exist=False)
        if destination.exists():
            if digest(destination)[0] != sha:
                raise BuddyError("collision", "Organized output already exists with different content.")
        else:
            if shutil.disk_usage(root).free < size + 64 * 1024 * 1024:
                raise BuddyError("disk_space", "Not enough free space for the organized copy.")
            temporary = destination.with_suffix(destination.suffix + ".partial")
            if temporary.exists():
                inside(temporary, root, directory=False).unlink()
            with source.open("rb") as incoming, temporary.open("xb") as outgoing:
                while True:
                    engine.check_control(job["job_id"])
                    block = incoming.read(4 * 1024 * 1024)
                    if not block:
                        break
                    outgoing.write(block)
                outgoing.flush()
                os.fsync(outgoing.fileno())
            if digest(temporary)[0] != sha or (source.stat().st_size, source.stat().st_mtime_ns) != (size, modified):
                raise BuddyError("source_changed", "Copy verification failed; original remains intact.")
            temporary.rename(destination)
        final = artifact(engine, job, destination, "video", source_asset_id=asset_id)
        outputs.append(final)
        seen[sha] = final["relative_path"]
        mapping.append({"source_asset_id": asset_id, "original_path": str(source), "new_relative_path": final["relative_path"], "original_preserved": True})
        engine.progress(job["job_id"], completed_items=len(outputs), total_items=len(job["params"]["asset_ids"]))
    report = root / "organization-map.json"
    atomic_json(report, {"mapping": mapping, "duplicates": duplicates, "originals_preserved": True})
    outputs.append(artifact(engine, job, report, "organization_map"))
    return {"assets": outputs, "mapping": mapping, "duplicates": duplicates, "originals_preserved": True}


def execute(engine, job):
    kind = job["kind"]
    if kind == "download":
        return download(engine, job)
    if kind in ("preview", "extract"):
        return clip(engine, job)
    if kind == "organize":
        return organize(engine, job)
    from .vision import index_unit, search
    return index_unit(engine, job) if kind == "index" else search(engine, job)
