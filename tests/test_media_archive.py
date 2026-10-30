"""Independent archive contract, restart, indexing, and corruption tests."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from media_worker.core import Engine, BuddyError
from test_media_worker import Tools, Encoder


class ArchiveTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.config = {"expected_host": "", "archive_root": str(self.root / "archive"), "model_revision": "synthetic-model"}
        self.tools = Tools()
        self.engine = Engine(self.root / "state", self.config, runner=self.tools, encoder=Encoder())
        self.rid = "a" * 32
        self.content = b"video bytes for independent storage"
        self.manifest = {"sha256": hashlib.sha256(self.content).hexdigest(), "bytes": len(self.content), "name": "show \u2603.mp4", "source": {"machine": "THIS-MACHINE", "path": "C:/Downloads/show.mp4"}}

    def tearDown(self):
        self.tmp.cleanup()

    def begin(self, rid=None):
        return self.engine.handle("begin_upload", {"request_id": rid or self.rid, "manifest": self.manifest})

    def finish_job(self, job):
        for _ in range(100):
            j = self.engine.get_job({"job_id": job["job_id"]})
            if j["status"] in ("completed", "failed", "needs_review"):
                return self.engine.get_results({"job_id": j["job_id"]})
            self.engine.run_next()
        self.fail("job did not finish")

    def upload(self, rid=None):
        rid = rid or self.rid
        r = self.begin(rid)
        (Path(r["upload_directory"]) / "chunk-000000.part").write_bytes(self.content)
        j = self.engine.handle("confirm_upload_chunk", {"request_id": rid, "index": 0, "bytes": len(self.content), "sha256": self.manifest["sha256"]})
        self.assertEqual(self.finish_job(j)["job"]["status"], "completed")
        return Path(r["upload_directory"])

    def commit(self, rid=None):
        j = self.engine.handle("finalize_upload", {"request_id": rid or self.rid})
        result = self.finish_job(j)
        self.assertEqual(result["job"]["status"], "completed", result["job"]["error"])
        return result["manifest"]["archive_receipt"]

    def test_commit_is_independent_idempotent_and_searchable(self):
        folder = self.upload()
        receipt = self.commit()
        p = Path(receipt["absolute_path"])
        self.assertEqual(p.read_bytes(), self.content)
        self.assertEqual(p.stat().st_nlink, 1)
        self.assertFalse(p.is_relative_to(self.engine.state))
        self.assertEqual(self.begin()["receipt"]["sha256"], receipt["sha256"])
        self.assertEqual(self.commit()["asset_id"], receipt["asset_id"])
        index = self.finish_job({"job_id": receipt["index_job_id"]})
        self.assertEqual(index["job"]["status"], "completed")
        search = self.engine.submit({"request_id": "search", "scope": "library", "project_id": None, "kind": "search", "params": {"query": "crowd"}})
        self.assertTrue(self.finish_job(search)["manifest"]["results"])

    def test_reopen_resumes_verified_chunks_and_duplicate_content(self):
        self.upload()
        self.engine = Engine(self.root / "state", self.config, runner=self.tools, encoder=Encoder())
        self.assertEqual(len(self.begin()["verified"]), 1)
        first = self.commit()
        self.upload("b" * 32)
        second = self.commit("b" * 32)
        self.assertEqual(first["absolute_path"], second["absolute_path"])
        self.assertEqual(len(list((self.root / "archive/Media").iterdir())), 1)

    def test_index_failure_keeps_stored_bytes_and_can_retry(self):
        self.upload()
        receipt = self.commit()
        self.tools.fail_names.add(Path(receipt["absolute_path"]).name)
        outcome = self.finish_job({"job_id": receipt["index_job_id"]})
        self.assertEqual(outcome["job"]["status"], "needs_review")
        self.assertEqual(Path(receipt["absolute_path"]).read_bytes(), self.content)
        self.tools.fail_names.clear()
        self.engine.control({"job_id": receipt["index_job_id"]}, "resume")
        self.assertEqual(self.finish_job({"job_id": receipt["index_job_id"]})["job"]["status"], "completed")

    def test_corrupt_chunk_is_not_reused_and_source_conflicts_fail(self):
        folder = self.upload()
        (folder / "chunk-000000.part").write_bytes(b"x" * len(self.content))
        self.assertEqual(self.begin()["verified"], [])
        j = self.engine.handle("finalize_upload", {"request_id": self.rid})
        self.assertEqual(self.finish_job(j)["job"]["status"], "failed")
        self.manifest["name"] = "different.mp4"
        with self.assertRaises(BuddyError):
            self.begin()
        self.assertFalse(list((self.root / "archive/Media").iterdir()))

    def test_assignment_is_metadata_and_receipts_survive_database_loss(self):
        self.upload(); r = self.commit()
        self.engine.handle("assign_inbox", {"asset_id": r["asset_id"], "project_id": "real-original-id"})
        self.engine.db_path.unlink()
        self.engine = Engine(self.root / "state", self.config, runner=self.tools, encoder=Encoder())
        listing = self.engine.handle("list_inbox", {})
        self.assertEqual(listing["items"][0]["project_ids"], ["real-original-id"])
        self.assertEqual(Path(listing["items"][0]["absolute_path"]).read_bytes(), self.content)
        with self.engine.connect() as c:
            self.assertEqual(c.execute("SELECT count(*) FROM jobs").fetchone()[0], 0)

    def test_disk_space_ownership_scope_and_bounds(self):
        with patch("media_worker.archive.shutil.disk_usage", return_value=type("Usage", (), {"free": 1})()):
            with self.assertRaisesRegex(BuddyError, "space"):
                self.begin()
        with self.assertRaises(BuddyError):
            self.engine.handle("begin_upload", {"request_id": "../escape", "manifest": self.manifest})
        self.begin()
        with self.assertRaises(BuddyError):
            self.engine.handle("confirm_upload_chunk", {"request_id": self.rid, "index": -1, "sha256": self.manifest["sha256"], "bytes": 1})
        marker = self.root / "archive/OWNER.json"
        marker.write_text("{}")
        with self.assertRaisesRegex(BuddyError, "owner"):
            self.engine.handle("list_inbox", {})

    def test_saved_session_identity_traversal_and_mismatch_fail_before_commit(self):
        r = self.begin(); folder = Path(r["upload_directory"])
        original = json.loads((folder / 'session.json').read_text())
        for bad in ['../../escaped-proof', 'b' * 32, '', None]:
            with self.subTest(saved_request_id=bad):
                value = dict(original, request_id=bad)
                (folder / 'session.json').write_text(json.dumps(value))
                with self.assertRaises(BuddyError):
                    self.engine.handle('finalize_upload', {'request_id': self.rid})
                with self.assertRaises(BuddyError):
                    from media_worker.archive import execute
                    execute(self.engine, {'kind': 'archive_commit', 'params': {'request_id': self.rid}, 'job_id': 'not-started'})
                self.assertFalse(list((self.root / 'archive/Media').iterdir()))
                self.assertFalse(list((self.root / 'archive/Receipts').iterdir()))
                self.assertFalse((self.root / 'escaped-proof.json').exists())

    def test_saved_manifest_shape_hash_and_length_are_validated(self):
        r = self.begin(); folder = Path(r['upload_directory'])
        original = json.loads((folder / 'session.json').read_text())
        for bad in [None, [], dict(self.manifest, sha256='../../escape'), dict(self.manifest, bytes=True), dict(self.manifest, bytes=-1), dict(self.manifest, extra='unknown'), dict(self.manifest, name='../video.mp4')]:
            with self.subTest(manifest=bad):
                (folder / 'session.json').write_text(json.dumps(dict(original, manifest=bad)))
                with self.assertRaises(BuddyError):
                    self.engine.handle('finalize_upload', {'request_id': self.rid})
                self.assertFalse(list((self.root / 'archive/Media').iterdir()))
                self.assertFalse(list((self.root / 'archive/Receipts').iterdir()))

    def test_another_corrupt_session_cannot_undermine_space_reservations(self):
        first=self.begin();p=Path(first['upload_directory'])/'session.json';value=json.loads(p.read_text());value['manifest']['bytes']=-1000000000;p.write_text(json.dumps(value))
        with self.assertRaises(BuddyError):self.begin('b'*32)
        self.assertFalse((self.root/'archive/Incoming'/('b'*32)).exists())

    def test_corrupt_chunk_retransmit_reconfirms_without_rewriting_completed_job(self):
        folder = self.upload()
        parameters = {'request_id': self.rid, 'index': 0, 'bytes': len(self.content), 'sha256': self.manifest['sha256']}
        original = self.engine.handle('confirm_upload_chunk', parameters)
        (folder / 'chunk-000000.part').write_bytes(b'x' * len(self.content))
        finalize = self.engine.handle('finalize_upload', {'request_id': self.rid})
        self.assertEqual(self.finish_job(finalize)['job']['status'], 'failed')
        self.assertFalse((folder / 'chunk-000000.json').exists())
        (folder / 'chunk-000000.part').write_bytes(self.content)
        repair = self.engine.handle('confirm_upload_chunk', parameters)
        self.assertNotEqual(repair['job_id'], original['job_id'])
        self.assertEqual(self.engine.handle('confirm_upload_chunk', parameters)['job_id'], repair['job_id'])
        self.assertEqual(self.finish_job(repair)['job']['status'], 'completed')
        self.assertEqual(self.engine.get_job({'job_id': original['job_id']})['status'], 'completed')
        self.engine.control({'job_id': finalize['job_id']}, 'resume')
        result = self.finish_job(finalize)
        self.assertEqual(result['job']['status'], 'completed', result['job']['error'])
        self.assertEqual(Path(result['manifest']['archive_receipt']['absolute_path']).read_bytes(), self.content)

    def test_ancestor_inventory_and_explicit_assets_cannot_expose_incoming_assembly(self):
        parent_folder = self.root / 'footage'
        parent_folder.mkdir()
        self.config['archive_root'] = str(parent_folder / 'archive')
        self.engine.config['archive_root'] = self.config['archive_root']
        self.begin()
        base = Path(self.config['archive_root'])
        assembly = base / 'Incoming' / self.rid / 'assembled'
        assembly.mkdir(); payload = assembly / 'video.mp4'; payload.write_bytes(self.content)
        committed = base / 'Media' / 'committed'; committed.mkdir()
        visible = committed / 'video.mp4'; visible.write_bytes(self.content)
        self.engine.config['source_allowlist'] = [str(parent_folder)]
        parent = self.engine.register_root({'path': str(parent_folder)})['root_id']
        self.assertEqual(list(self.engine.media_files(parent)), [visible])
        for folder in (base, base / 'Incoming', assembly):
            with self.assertRaises(BuddyError):
                self.engine.register_root({'path': str(folder)})
        with self.assertRaises(BuddyError):
            self.engine.register_asset(payload, parent)
        # Previously recorded aliases cannot make an incoming file searchable.
        stat = payload.stat(); asset = 'asset-' + self.manifest['sha256']
        with self.engine.connect() as connection:
            connection.execute('INSERT INTO aliases VALUES(?,?,?,?,?)', (parent, str(payload), asset, stat.st_size, stat.st_mtime_ns))
        with self.assertRaises(BuddyError):
            self.engine.asset_path(asset, [parent])

    def test_persisted_receipt_before_index_failure_resumes_existing_finalize(self):
        self.upload(); original = self.engine.register_root
        def fail(_):
            raise BuddyError('injected_failure', 'Failure after receipt persistence')
        with patch.object(self.engine, 'register_root', fail):
            finalize = self.engine.handle('finalize_upload', {'request_id': self.rid})
            self.assertEqual(self.finish_job(finalize)['job']['status'], 'failed')
        receipt = self.begin()['receipt']
        self.assertNotIn('index_job_id', receipt)
        self.assertEqual(Path(receipt['absolute_path']).read_bytes(), self.content)
        retry = self.engine.handle('finalize_upload', {'request_id': self.rid})
        self.assertEqual(retry['job_id'], finalize['job_id'])
        self.engine.control({'job_id': retry['job_id']}, 'resume')
        complete = self.finish_job(retry)
        self.assertEqual(complete['job']['status'], 'completed')
        self.assertIn('index_job_id', complete['manifest']['archive_receipt'])


if __name__ == "__main__":
    unittest.main()
