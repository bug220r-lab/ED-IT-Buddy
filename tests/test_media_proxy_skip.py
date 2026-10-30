"""Synthetic durable-index regression coverage for safe MAGIX proxy skips."""
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from media_worker.core import BuddyError, Engine, atomic_json
from media_worker.vision import _profile


class Runner:
    def __init__(self):
        self.probes = []

    def __call__(self, engine, job, name, args, stdout_path=None, timeout=None):
        if name == "ffprobe":
            source = Path(args[-1])
            self.probes.append(str(source))
            if "_proxy_" in source.name.lower() or source.suffix.lower() == ".mpg":
                raise BuddyError("unsupported_media", "Synthetic proxy has no finite duration")
            data = {"format": {"duration": "125"}, "streams": [{"codec_type": "video", "codec_name": "h264", "pix_fmt": "yuv420p", "width": 1280, "height": 720, "avg_frame_rate": "30/1"}]}
            Path(stdout_path).write_text(json.dumps(data), encoding="utf-8")
        elif name == "ffmpeg":
            output = args[-1]
            for i in (1, 2):
                Path(output.replace("%06d", f"{i:06d}")).write_bytes(b"synthetic thumbnail" + bytes([i]))
            with (engine.job_dir(job["job_id"]) / "tools.log").open("a", encoding="utf-8") as stream:
                stream.write("[showinfo] n:0 pts_time:0.0333667\n[showinfo] n:1 pts_time:2.1354667\n")
        return 0


class Encoder:
    def images(self, paths):
        return [[1.0, 0.0] for _ in paths]


class MediaProxySkipTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.base = Path(self.tmp.name)
        self.footage = self.base / "footage"
        self.footage.mkdir()
        self.runner = Runner()
        self.config = {"expected_host": "", "source_allowlist": [str(self.footage)], "model_revision": "synthetic-model-current"}
        self.engine = Engine(self.base / "state", self.config, runner=self.runner, encoder=Encoder())
        self.root_id = self.engine.register_root({"path": str(self.footage)})["root_id"]
        self.counter = 0

    def tearDown(self):
        self.tmp.cleanup()

    def source(self, name="recording.mp4", content=b"original recording"):
        source = self.footage / name
        source.parent.mkdir(parents=True, exist_ok=True)
        source.write_bytes(content)
        return source

    def proxy(self, stem="recording", extension="mp4", parent=None):
        parent = self.footage if parent is None else parent
        parent.mkdir(parents=True, exist_ok=True)
        source = parent / (stem + "_" + extension + "_proxy_" + "a" * 32 + "_" + "b" * 32 + ".mpg")
        source.write_bytes(b"readable but durationless proxy")
        return source

    def seed_index(self, source, *, profile=None, complete=True):
        asset = self.engine.register_asset(source, self.root_id, lambda: None, {"duration_seconds": 125})
        with self.engine.connect() as connection:
            connection.execute("INSERT OR REPLACE INTO indexes VALUES(?,?,?,?)", (asset, profile or _profile(self.engine, 2), 125 if complete else 60, int(complete)))
        return asset

    def job(self, paths=None, *, root_id=None, params=None):
        self.counter += 1
        root_id = root_id or self.root_id
        args = {"root_ids": [root_id], "sample_seconds": 2}
        args.update(params or {})
        job = self.engine.submit({"request_id": "proxy-check-" + str(self.counter), "kind": "index", "project_id": "band-library", "params": args})
        if paths is not None:
            # A legitimate pre-upgrade checkpoint containing only the pending
            # assets. This also prevents a negative-case original from being
            # freshly indexed and becoming eligible during the test itself.
            atomic_json(self.engine.job_dir(job["job_id"]) / "index-plan.json", {"items": [{"root_id": root_id, "path": str(p)} for p in paths], "cursor": 0, "results": [], "failures": [], "selected": args.get("asset_ids", [])})
        return job

    def finish(self, job):
        for _ in range(100):
            current = self.engine.get_job({"job_id": job["job_id"]})
            if current["status"] in ("completed", "needs_review", "failed", "cancelled", "paused"):
                return current, self.engine.get_results({"job_id": job["job_id"]})["manifest"]
            self.assertTrue(self.engine.run_next())
        self.fail("Index did not finish")

    def assert_not_skipped(self, proxy):
        current, manifest = self.finish(self.job([proxy]))
        self.assertEqual("needs_review", current["status"])
        self.assertFalse(manifest.get("skipped", []))
        self.assertEqual(1, len(manifest["failures"]))
        self.assertIn(str(proxy), self.runner.probes)
        self.assertTrue(proxy.exists())

    def test_complete_fresh_original_skips_without_probing_or_modifying_proxy(self):
        original = self.source()
        asset = self.seed_index(original)
        proxy = self.proxy()
        before = {p: (p.read_bytes(), p.stat().st_mtime_ns) for p in (original, proxy)}
        current, manifest = self.finish(self.job([proxy]))
        self.assertEqual("completed", current["status"])
        self.assertFalse(manifest["failures"])
        self.assertEqual(1, len(manifest["skipped"]))
        receipt = manifest["skipped"][0]
        self.assertEqual(str(original), receipt["original_path"])
        self.assertEqual(str(proxy), receipt["source_path"])
        self.assertEqual(asset, receipt["original_asset_id"])
        self.assertEqual(self.root_id, receipt["root_id"])
        self.assertEqual(_profile(self.engine, 2), receipt["profile"])
        self.assertEqual("magix_proxy_original_indexed", receipt["reason"])
        self.assertNotIn(str(proxy), self.runner.probes)
        for path, state in before.items():
            self.assertEqual(state, (path.read_bytes(), path.stat().st_mtime_ns))

    def test_pattern_and_original_matching_are_case_insensitive(self):
        original = self.source("Drummer.MP4")
        self.seed_index(original)
        proxy = self.proxy("DRUMMER", "MP4")
        current, manifest = self.finish(self.job([proxy]))
        self.assertEqual("completed", current["status"])
        self.assertEqual(1, len(manifest["skipped"]))

    def test_unique_magix_truncated_prefix_can_skip(self):
        original = self.source("abcdefghijklmnop.mp4")
        self.seed_index(original)
        proxy = self.proxy("abcdefghij")
        current, manifest = self.finish(self.job([proxy]))
        self.assertEqual("completed", current["status"])
        self.assertEqual(1, len(manifest["skipped"]))

    def test_ambiguous_original_prefix_still_needs_review(self):
        for name in ("abcdefghij-first.mp4", "abcdefghij-second.mp4"):
            self.seed_index(self.source(name, name.encode()))
        self.assert_not_skipped(self.proxy("abcdefghij"))

    def test_missing_or_unindexed_original_is_not_skipped(self):
        proxy = self.proxy()
        self.assert_not_skipped(proxy)
        self.source()
        self.assert_not_skipped(proxy)

    def test_changed_original_metadata_prevents_skip(self):
        original = self.source()
        self.seed_index(original)
        original.write_bytes(b"replacement recording with different bytes")
        self.assert_not_skipped(self.proxy())

    def test_content_change_with_restored_size_and_mtime_prevents_skip(self):
        original = self.source(content=b"original")
        self.seed_index(original)
        before = original.stat()
        original.write_bytes(b"replaced")
        os.utime(original, ns=(before.st_atime_ns, before.st_mtime_ns))
        self.assert_not_skipped(self.proxy())

    def test_partial_finer_or_other_model_indexes_do_not_hide_proxy(self):
        original = self.source()
        asset = self.seed_index(original, complete=False)
        proxy = self.proxy()
        self.assert_not_skipped(proxy)
        profiles = [_profile(self.engine, 2, 20, 40), _profile(self.engine, 1)]
        old = self.engine.config["model_revision"]
        self.engine.config["model_revision"] = "synthetic-model-old"
        profiles.append(_profile(self.engine, 2))
        self.engine.config["model_revision"] = old
        for profile in profiles:
            with self.subTest(profile=profile):
                with self.engine.connect() as connection:
                    connection.execute("DELETE FROM indexes WHERE asset_id=?", (asset,))
                self.seed_index(original, profile=profile)
                self.assert_not_skipped(proxy)

    def test_non_magix_mpg_and_malformed_hash_names_are_not_hidden(self):
        self.seed_index(self.source())
        for name in ("recording.mpg", "recording_proxy.mpg", "recording_mp4_proxy_short_short.mpg", "recording_mp4_proxy_" + "z" * 32 + "_" + "b" * 32 + ".mpg"):
            with self.subTest(name=name):
                self.assert_not_skipped(self.source(name, b"ordinary video"))

    def test_matching_name_in_other_folder_does_not_authorize_skip(self):
        original = self.source("elsewhere/recording.mp4")
        self.seed_index(original)
        self.assert_not_skipped(self.proxy())

    def test_pending_old_checkpoint_uses_new_skip_rule_without_new_job(self):
        self.seed_index(self.source())
        proxy = self.proxy()
        job = self.job([proxy])
        reopened = Engine(self.base / "state", self.config, runner=self.runner, encoder=Encoder())
        self.engine = reopened
        current, manifest = self.finish(job)
        self.assertEqual(job["job_id"], current["job_id"])
        self.assertEqual("completed", current["status"])
        self.assertEqual(1, len(manifest["skipped"]))

    def test_original_indexed_through_ancestor_root_can_cover_child_scope(self):
        original = self.source("show/recording.mp4")
        asset = self.seed_index(original)
        proxy = self.proxy(parent=original.parent)
        child = self.engine.register_root({"path": str(original.parent)})["root_id"]
        current, manifest = self.finish(self.job([proxy], root_id=child))
        self.assertEqual("completed", current["status"])
        self.assertEqual(1, len(manifest["skipped"]))
        self.assertEqual(asset, manifest["skipped"][0]["original_asset_id"])
        self.assertEqual(str(original), manifest["skipped"][0]["original_path"])
        self.assertEqual(child, manifest["skipped"][0]["root_id"])

    def test_missing_original_bytes_do_not_trust_prior_completed_receipt(self):
        original = self.source()
        self.seed_index(original)
        original.unlink()
        self.assert_not_skipped(self.proxy())

    def test_explicit_asset_selection_does_not_silently_skip_proxy(self):
        import hashlib
        self.seed_index(self.source())
        proxy = self.proxy()
        selected = "asset-" + hashlib.sha256(proxy.read_bytes()).hexdigest()
        current, manifest = self.finish(self.job([proxy], params={"asset_ids": [selected]}))
        self.assertEqual("needs_review", current["status"])
        self.assertFalse(manifest.get("skipped", []))
        self.assertIn(str(proxy), self.runner.probes)

    def test_live_old_failure_reconciles_only_after_original_finishes(self):
        original = self.source()
        proxy = self.proxy()
        job = self.job([proxy, original])
        prior = {"source_name": proxy.name, "code": "unsupported_media", "message": "No finite duration"}
        plan = self.engine.job_dir(job["job_id"]) / "index-plan.json"
        value = json.loads(plan.read_text("utf-8"))
        value.update(cursor=1, failures=[prior])
        atomic_json(plan, value)
        current, manifest = self.finish(job)
        self.assertEqual("completed", current["status"])
        self.assertFalse(manifest["failures"])
        self.assertEqual(1, len(manifest["skipped"]))
        self.assertEqual(prior, manifest["skipped"][0]["prior_failure"])
        self.assertNotIn(str(proxy), self.runner.probes)

    def test_terminal_old_review_manifest_is_preserved_by_new_followup_job(self):
        original = self.source()
        proxy = self.proxy()
        historical = self.job([proxy])
        current, old_manifest = self.finish(historical)
        self.assertEqual("needs_review", current["status"])
        old_result = self.engine.get_results({"job_id": historical["job_id"]})
        manifest_path = Path(old_result["manifest_path"])
        previous_bytes = manifest_path.read_bytes()
        self.seed_index(original)
        followup = self.job([proxy])
        current, manifest = self.finish(followup)
        self.assertEqual("completed", current["status"])
        self.assertEqual(1, len(manifest["skipped"]))
        self.assertNotEqual(historical["job_id"], followup["job_id"])
        self.assertEqual("needs_review", self.engine.get_job({"job_id": historical["job_id"]})["status"])
        self.assertEqual(previous_bytes, manifest_path.read_bytes())
        self.assertEqual(old_manifest, self.engine.get_results({"job_id": historical["job_id"]})["manifest"])

    def test_original_is_indexed_before_truncated_proxy_is_rejected(self):
        original = self.source("abcdefghijklmnop.mp4")
        proxy = self.proxy("abcdefghij")
        current, manifest = self.finish(self.job())
        self.assertEqual("completed", current["status"])
        self.assertEqual(1, len(manifest["results"]))
        self.assertEqual(1, len(manifest["skipped"]))
        self.assertNotIn(str(proxy), self.runner.probes)
        self.assertIn(str(original), self.runner.probes)

    def test_real_failures_remain_review_items_alongside_proxy_skips(self):
        self.seed_index(self.source())
        proxy = self.proxy()
        unrelated = self.source("unrelated.mpg", b"unreadable original")
        current, manifest = self.finish(self.job([proxy, unrelated]))
        self.assertEqual("needs_review", current["status"])
        self.assertEqual(1, len(manifest["skipped"]))
        self.assertEqual([unrelated.name], [f["source_name"] for f in manifest["failures"]])


if __name__ == "__main__":
    unittest.main()
