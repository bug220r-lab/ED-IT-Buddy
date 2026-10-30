"""Isolated worker integration tests. No MSI, network, model, or production footage."""
import hashlib
import json
import os
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from media_worker.core import BuddyError, Engine, JobCancelled, atomic_json, digest, worker_lock


class Encoder:
    def __init__(self):
        self.calls = 0

    def images(self, paths):
        self.calls += 1
        return [[1.0, 0.0] if "000001" in str(p) else [0.0, 1.0] for p in paths]

    def text(self, query):
        return [1.0, 0.0] if "crowd" in query else [0.0, 1.0]


class Tools:
    def __init__(self):
        self.calls = []
        self.durations = {}
        self.fail_names = set()

    def __call__(self, engine, job, name, args, stdout_path=None, timeout=None):
        self.calls.append((name, args))
        if name == "ffprobe":
            source = Path(args[-1])
            if source.name in self.fail_names:
                raise BuddyError("probe_failed", "Synthetic corrupt recording")
            duration = self.durations.get(str(source), 125)
            value = {"format": {"duration": str(duration)}, "streams": [{"codec_type": "video", "codec_name": "h264", "pix_fmt": "yuv420p", "width": 1280, "height": 720, "avg_frame_rate": "30000/1001"}, {"codec_type": "audio", "codec_name": "aac", "profile": "LC"}]}
            Path(stdout_path).write_text(json.dumps(value), encoding="utf-8")
        elif name == "ffmpeg":
            output = args[-1]
            if "frame-%06d.jpg" in output:
                for i in (1, 2):
                    Path(output.replace("%06d", f"{i:06d}")).write_bytes(b"synthetic thumbnail" + bytes([i]))
                with (engine.job_dir(job["job_id"]) / "tools.log").open("a", encoding="utf-8") as log:
                    # Actual variable-frame-rate PTS; neither sample is an exact frame grid.
                    log.write("[showinfo] n:0 pts_time:0.0333667\n[showinfo] n:1 pts_time:2.1354667\n")
            elif output != "-":
                duration = float(args[args.index("-t") + 1]) if "-t" in args else 125
                # Identical synthetic bytes must imply identical media metadata,
                # just as real content-addressed video assets do.
                Path(output).write_bytes(b"synthetic valid output|" + str(duration).encode())
                self.durations[output] = duration
        elif name == "yt-dlp":
            listing = Path(args[args.index("--print-to-file") + 2])
            source = listing.parent / "downloaded.mp4"
            source.write_bytes(b"synthetic download")
            source.with_suffix(".info.json").write_text('{"webpage_url":"https://example.com/video"}', encoding="utf-8")
            listing.write_text(str(source) + "\n", encoding="utf-8")
        return 0


class MediaWorkerTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.footage = self.root / "footage"
        self.footage.mkdir()
        self.tools = Tools()
        self.encoder = Encoder()
        self.config = {"expected_host": "", "source_allowlist": [str(self.footage)], "model_revision": "synthetic-model-1"}
        self.engine = Engine(self.root / "state", self.config, runner=self.tools, encoder=self.encoder)
        self.source_root = self.engine.handle("register_source_root", {"path": str(self.footage), "label": "Band show"})["root_id"]
        self.next_id = 0

    def tearDown(self):
        self.tmp.cleanup()

    def source(self, name="show.mp4", content=b"recording"):
        path = self.footage / name
        path.write_bytes(content)
        return path

    def submit(self, kind, params, request_id=None):
        self.next_id += 1
        return self.engine.submit({"request_id": request_id or f"request-{self.next_id}", "kind": kind, "project_id": "band-project-original-id", "params": params})

    def finish(self, job, limit=500):
        for _ in range(limit):
            current = self.engine.get_job({"job_id": job["job_id"]})
            if current["status"] in ("completed", "needs_review", "failed", "cancelled", "paused"):
                return current
            self.assertTrue(self.engine.run_next())
        self.fail("Job did not reach a bounded outcome")

    def indexed(self):
        self.source()
        job = self.submit("index", {"root_ids": [self.source_root]})
        self.assertEqual("completed", self.finish(job)["status"])
        result = self.engine.get_results({"job_id": job["job_id"]})
        return result["manifest"]["results"][0]["asset_id"]

    def test_durable_request_identity_and_changed_request_conflict(self):
        request = {"request_id": "stable", "kind": "index", "project_id": "p", "params": {"root_ids": [self.source_root]}}
        first = self.engine.submit(request)
        reopened = Engine(self.root / "state", self.config, runner=self.tools, encoder=self.encoder)
        self.assertEqual(first["job_id"], reopened.submit(request)["job_id"])
        request["params"]["sample_seconds"] = 1
        with self.assertRaisesRegex(BuddyError, "different work"):
            reopened.submit(request)
        self.assertEqual(1, len(reopened.list_jobs({})["jobs"]))

    def test_scope_and_unowned_state_are_refused(self):
        outside = self.root / "other"
        outside.mkdir()
        with self.assertRaises(BuddyError):
            self.engine.register_root({"path": str(outside)})
        (outside / "personal.txt").write_text("keep")
        with self.assertRaisesRegex(BuddyError, "unowned"):
            Engine(outside, self.config)
        self.assertEqual("keep", (outside / "personal.txt").read_text())

    def test_worker_singleton_and_queued_control(self):
        with worker_lock(self.engine.state):
            with self.assertRaises(BuddyError):
                with worker_lock(self.engine.state):
                    pass
        job = self.submit("index", {"root_ids": [self.source_root]})
        self.assertEqual("paused", self.engine.control({"job_id": job["job_id"]}, "pause")["status"])
        self.assertFalse(self.engine.run_next())
        self.engine.control({"job_id": job["job_id"]}, "resume")
        self.assertEqual("cancelled", self.engine.control({"job_id": job["job_id"]}, "cancel")["status"])

    def test_running_recovery_preserves_pause_and_never_duplicates_job(self):
        job = self.submit("index", {"root_ids": [self.source_root]})
        with self.engine.connect() as c:
            c.execute("UPDATE jobs SET status='running',control='pause' WHERE job_id=?", (job["job_id"],))
        self.engine.recover()
        self.assertEqual("paused", self.engine.get_job({"job_id": job["job_id"]})["status"])
        self.assertEqual(1, len(self.engine.list_jobs({})["jobs"]))

    def test_variable_source_timestamps_and_interactive_priority(self):
        self.source()
        indexing = self.submit("index", {"root_ids": [self.source_root]})
        self.engine.run_next()
        self.assertEqual("queued", self.engine.get_job({"job_id": indexing["job_id"]})["status"])
        search = self.submit("search", {"query": "crowd", "root_ids": [self.source_root]})
        self.engine.run_next()
        self.assertEqual("completed", self.engine.get_job({"job_id": search["job_id"]})["status"])
        match = self.engine.get_results({"job_id": search["job_id"]})["manifest"]["results"][0]
        self.assertAlmostEqual(.033367, match["timestamp_seconds"], places=6)
        self.assertEqual(hashlib.sha256(Path(match["thumbnail_path"]).read_bytes()).hexdigest(), match["thumbnail_sha256"])
        self.assertEqual("suggested", match["review_state"])
        self.assertEqual("completed", self.finish(indexing)["status"])

    def test_more_than_five_hours_checkpoints_and_resumes(self):
        source = self.source()
        self.tools.durations[str(source)] = 21601
        job = self.submit("index", {"root_ids": [self.source_root]})
        self.engine.run_next()
        self.assertEqual(60, self.engine.get_job({"job_id": job["job_id"]})["progress"]["indexed_seconds"])
        self.engine.control({"job_id": job["job_id"]}, "pause")
        self.engine = Engine(self.root / "state", self.config, runner=self.tools, encoder=self.encoder)
        self.engine.control({"job_id": job["job_id"]}, "resume")
        self.assertEqual("completed", self.finish(job)["status"])
        with self.engine.connect() as c:
            last = c.execute("SELECT MAX(pts) FROM samples").fetchone()[0]
        self.assertGreater(last, 21600)
        self.assertEqual(b"recording", source.read_bytes())

    def test_duplicate_content_reuses_index_but_retains_aliases(self):
        self.source("a.mp4")
        self.source("b.mp4")
        job = self.submit("index", {"root_ids": [self.source_root]})
        self.assertEqual("completed", self.finish(job)["status"])
        with self.engine.connect() as c:
            self.assertEqual(1, c.execute("SELECT COUNT(*) FROM assets").fetchone()[0])
            self.assertEqual(2, c.execute("SELECT COUNT(*) FROM aliases").fetchone()[0])
        results = self.engine.get_results({"job_id": job["job_id"]})["manifest"]["results"]
        self.assertTrue(any(x["reused"] for x in results))

    def test_corrupt_file_does_not_block_other_recordings(self):
        self.source("bad.mp4", b"bad")
        self.source("good.mp4", b"good")
        self.tools.fail_names.add("bad.mp4")
        job = self.submit("index", {"root_ids": [self.source_root]})
        self.assertEqual("needs_review", self.finish(job)["status"])
        manifest = self.engine.get_results({"job_id": job["job_id"]})["manifest"]
        self.assertEqual(1, len(manifest["results"]))
        self.assertEqual(1, len(manifest["failures"]))
        self.tools.fail_names.clear()
        self.engine.control({"job_id": job["job_id"]}, "resume")
        self.assertIsNone(self.engine.get_results({"job_id": job["job_id"]})["manifest"])
        self.assertEqual("completed", self.finish(job)["status"])
        retried = self.engine.get_results({"job_id": job["job_id"]})["manifest"]
        self.assertEqual(2, len(retried["results"]))
        self.assertEqual([], retried["failures"])

    def test_preflight_receipt_is_bound_to_model_revision(self):
        self.assertFalse(self.engine.get_preflight()["passed"])
        atomic_json(self.engine.state / "gpu-preflight.json", {"passed": True, "model_revision": "other"})
        self.assertFalse(self.engine.get_preflight()["passed"])
        atomic_json(self.engine.state / "gpu-preflight.json", {"passed": True, "model_revision": self.config["model_revision"]})
        self.assertTrue(self.engine.get_preflight()["passed"])

    def test_copy_first_and_clip_keep_original_unchanged(self):
        asset = self.indexed()
        before = digest(self.footage / "show.mp4")
        job = self.submit("organize", {"asset_ids": [asset], "group_by": "tag", "tag": "October gig"})
        self.assertEqual("completed", self.finish(job)["status"])
        manifest = self.engine.get_results({"job_id": job["job_id"]})["manifest"]
        self.assertTrue(manifest["originals_preserved"])
        output = Path(manifest["assets"][0]["absolute_path"])
        self.assertEqual(b"recording", output.read_bytes())
        self.assertEqual(before, digest(self.footage / "show.mp4"))
        clip = self.submit("extract", {"asset_id": asset, "start_seconds": 3, "end_seconds": 13})
        self.assertEqual("completed", self.finish(clip)["status"])
        self.assertEqual(before, digest(self.footage / "show.mp4"))
        preview = self.submit("preview", {"asset_id": asset, "start_seconds": 3, "end_seconds": 13})
        self.assertEqual("completed", self.finish(preview)["status"])
        self.assertEqual(before, digest(self.footage / "show.mp4"))

    def test_changed_source_is_not_returned_or_extracted(self):
        asset = self.indexed()
        self.source(content=b"changed recording")
        job = self.submit("search", {"query": "crowd"})
        self.assertEqual("completed", self.finish(job)["status"])
        self.assertEqual([], self.engine.get_results({"job_id": job["job_id"]})["manifest"]["results"])
        job = self.submit("extract", {"asset_id": asset, "start_seconds": 0, "end_seconds": 10})
        self.assertEqual("failed", self.finish(job)["status"])

    def test_model_change_excludes_old_embeddings(self):
        self.indexed()
        self.engine.config["model_revision"] = "different-model"
        job = self.submit("search", {"query": "crowd"})
        self.assertEqual("completed", self.finish(job)["status"])
        self.assertEqual([], self.engine.get_results({"job_id": job["job_id"]})["manifest"]["results"])

    def test_isolated_download_preserves_unrelated_footage(self):
        unrelated = self.source(content=b"existing personal recording")
        # YouTube is a supported individual source, avoiding an unknown-site probe fixture.
        job = self.submit("download", {"urls": ["https://www.youtube.com/watch?v=abc123"]})
        self.assertEqual("completed", self.finish(job)["status"])
        result = self.engine.get_results({"job_id": job["job_id"]})["manifest"]
        self.assertGreaterEqual(len(result["assets"]), 3)
        self.assertEqual(b"existing personal recording", unrelated.read_bytes())
        download_args = next(args for name, args in self.tools.calls if name == "yt-dlp")
        self.assertIn(str(self.engine.job_dir(job["job_id"])), download_args[download_args.index("--output") + 1])
        self.assertNotIn("--exec", download_args)
        self.assertTrue(list(self.engine.job_dir(job["job_id"]).rglob("downloaded.mp4")))

    def test_download_retry_uses_completed_source_after_conversion_failure(self):
        delegate = self.tools
        failed_once = [False]
        def runner(engine, job, name, args, **kwargs):
            if name == "ffmpeg" and args[-1].endswith(".editor-partial.mp4") and not failed_once[0]:
                failed_once[0] = True
                raise BuddyError("synthetic_interrupt", "Conversion interrupted after download completed")
            return delegate(engine, job, name, args, **kwargs)
        self.engine.runner = runner
        job = self.submit("download", {"urls": ["https://www.youtube.com/watch?v=retry123"]})
        self.assertEqual("needs_review", self.finish(job)["status"])
        self.engine.control({"job_id": job["job_id"]}, "resume")
        self.assertEqual("completed", self.finish(job)["status"])
        self.assertEqual(1, sum(name == "yt-dlp" for name, _ in self.tools.calls))

    def test_completed_download_can_be_previewed_extracted_and_organized(self):
        downloaded = self.submit("download", {"urls": ["https://www.youtube.com/watch?v=reuse123"]})
        self.assertEqual("completed", self.finish(downloaded)["status"])
        manifest = self.engine.get_results({"job_id": downloaded["job_id"]})["manifest"]
        video = next(a for a in manifest["assets"] if a["kind"] == "video")
        original = Path(video["absolute_path"]).read_bytes()
        for kind in ("preview", "extract"):
            operation = self.submit(kind, {"asset_id": video["asset_id"], "start_seconds": 1, "end_seconds": 6})
            self.assertEqual("completed", self.finish(operation)["status"])
            generated = self.engine.get_results({"job_id": operation["job_id"]})["manifest"]["assets"][0]
            self.assertEqual(video["asset_id"], generated["source_asset_id"])
        organized = self.submit("organize", {"asset_ids": [video["asset_id"]], "group_by": "source"})
        self.assertEqual("completed", self.finish(organized)["status"])
        self.assertEqual(original, Path(video["absolute_path"]).read_bytes())

    def test_generated_asset_requires_unchanged_manifest_bytes_and_owned_path(self):
        downloaded = self.submit("download", {"urls": ["https://www.youtube.com/watch?v=verify123"]})
        self.assertEqual("completed", self.finish(downloaded)["status"])
        manifest = self.engine.get_results({"job_id": downloaded["job_id"]})["manifest"]
        video = next(a for a in manifest["assets"] if a["kind"] == "video")
        path = Path(video["absolute_path"])
        original = path.read_bytes()
        info = path.stat()
        path.write_bytes(bytes([original[0] ^ 1]) + original[1:])
        os.utime(path, ns=(info.st_atime_ns, info.st_mtime_ns))
        with self.assertRaises(BuddyError):
            self.engine.asset_path(video["asset_id"])
        path.write_bytes(original)
        manifest_path = self.engine.job_dir(downloaded["job_id"]) / "manifest.json"
        manifest["assets"][0]["sha256"] = "0" * 64
        atomic_json(manifest_path, manifest)
        with self.assertRaises(BuddyError):
            self.engine.asset_path(video["asset_id"])
        metadata = next(a for a in manifest["assets"] if a["kind"] == "source_metadata")
        with self.assertRaises(BuddyError):
            self.engine.asset_path(metadata["asset_id"])
        unregistered = self.engine.state / "unregistered.mp4"
        unregistered.write_bytes(b"not a completed job asset")
        with self.assertRaises(BuddyError):
            self.engine.asset_path("asset-" + hashlib.sha256(unregistered.read_bytes()).hexdigest())
        with self.assertRaises(BuddyError):
            self.engine.register_root({"path": str(self.engine.state)})

    def test_completed_output_can_be_reused_after_worker_restarts(self):
        asset = self.indexed()
        first = self.submit("extract", {"asset_id": asset, "start_seconds": 0, "end_seconds": 10})
        self.assertEqual("completed", self.finish(first)["status"])
        output = self.engine.get_results({"job_id": first["job_id"]})["manifest"]["assets"][0]
        self.engine = Engine(self.root / "state", self.config, runner=self.tools, encoder=self.encoder)
        second = self.submit("preview", {"asset_id": output["asset_id"], "start_seconds": 0, "end_seconds": 5})
        self.assertEqual("completed", self.finish(second)["status"])

    def test_registered_duplicate_roots_use_selected_source_scope(self):
        first = self.source()
        other = self.footage / "second"
        other.mkdir()
        (other / "same.mp4").write_bytes(first.read_bytes())
        second_root = self.engine.register_root({"path": str(other), "label": "Second"})["root_id"]
        index = self.submit("index", {"root_ids": [self.source_root, second_root]})
        self.assertEqual("completed", self.finish(index)["status"])
        query = self.submit("search", {"query": "crowd", "root_ids": [second_root]})
        self.assertEqual("completed", self.finish(query)["status"])
        matches = self.engine.get_results({"job_id": query["job_id"]})["manifest"]["results"]
        self.assertTrue(matches)
        self.assertTrue(all(x["root_id"] == second_root for x in matches))

    def test_finer_index_range_does_not_mark_whole_file_complete(self):
        self.source()
        index = self.submit("index", {"root_ids": [self.source_root], "sample_seconds": 1, "start_seconds": 60, "end_seconds": 65})
        self.assertEqual("completed", self.finish(index)["status"])
        whole = self.submit("index", {"root_ids": [self.source_root], "sample_seconds": 1})
        before = self.encoder.calls
        self.engine.run_next()
        self.assertGreater(self.encoder.calls, before)
        self.assertEqual("queued", self.engine.get_job({"job_id": whole["job_id"]})["status"])

    def test_owned_tool_cancellation_is_bounded(self):
        from media_worker.jobs import tool
        self.engine.runner = None
        self.engine.config["ffmpeg_path"] = sys.executable
        job = self.submit("index", {"root_ids": [self.source_root]})
        started = self.engine.job_dir(job["job_id"]) / "tool-started.txt"
        outcome = []
        program = "import pathlib,time; pathlib.Path(" + repr(str(started)) + ").write_text('ready'); time.sleep(60)"
        def run():
            try:
                tool(self.engine, job, "ffmpeg", ["-c", program], timeout=10)
            except Exception as error:
                outcome.append(error)
        worker = threading.Thread(target=run)
        worker.start()
        deadline = time.monotonic() + 5
        while not started.exists() and not outcome and time.monotonic() < deadline:
            time.sleep(.05)
        self.engine.control({"job_id": job["job_id"]}, "cancel")
        worker.join(timeout=5)
        self.assertFalse(worker.is_alive())
        self.assertTrue(outcome)
        self.assertIsInstance(outcome[0], JobCancelled)

    def test_unknown_parameters_and_unsafe_urls_are_rejected(self):
        for url in ("file:///C:/secret.mp4", "http://127.0.0.1/video", "https://youtube.com/playlist?list=x"):
            with self.assertRaises(BuddyError):
                self.submit("download", {"urls": [url]})
        with self.assertRaises(BuddyError):
            self.submit("download", {"urls": ["https://example.com/video"], "command": "anything"})

    def test_rpc_protocol_keeps_errors_correlated_without_starting_work(self):
        config_path = self.root / "config.json"
        config_path.write_text(json.dumps(self.config), encoding="utf-8")
        rpc = Path(__file__).resolve().parents[1] / "media_worker" / "rpc.py"
        request = json.dumps({"id": "r1", "method": "get_capabilities", "params": {}}) + "\n" + json.dumps({"id": "r2", "method": "unknown", "params": {}}) + "\n"
        result = subprocess.run([sys.executable, "-B", str(rpc), "--state", str(self.engine.state), "--config", str(config_path)], input=request, text=True, encoding="utf-8", capture_output=True, timeout=20)
        self.assertEqual(0, result.returncode, result.stderr)
        responses = [json.loads(x) for x in result.stdout.splitlines()]
        self.assertFalse(responses[0]["result"]["worker_online"])
        self.assertEqual("r2", responses[1]["id"])
        self.assertEqual("unsupported", responses[1]["error"]["code"])


if __name__ == "__main__":
    unittest.main()
