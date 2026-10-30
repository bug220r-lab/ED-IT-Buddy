"""Deterministic publication races on disposable files; never the installed kit."""
from contextlib import contextmanager
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

PATH=Path(__file__).resolve().parents[1]/'integrations/interlinked/publish_current.py'
spec=importlib.util.spec_from_file_location('buddy_publication_under_test',PATH)
publication=importlib.util.module_from_spec(spec);spec.loader.exec_module(publication)
digest=lambda raw:hashlib.sha256(raw).hexdigest()


class PublicationRaceTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=Path(self.temp.name)
        self.source=self.root/'source';self.kit=self.root/'kit';self.receipt=self.root/'receipt'
        for p in (self.source,self.kit,self.receipt):p.mkdir()
        self.relative='repository/app/interlinked_buddy.py';self.dest='INTERLINKED-App/interlinked_buddy.py'
        for p in (self.source/self.relative,self.kit/self.dest):p.parent.mkdir(parents=True,exist_ok=True)
        self.checked=b'reviewed companion bytes';self.before=b'previous installed companion'
        (self.source/self.relative).write_bytes(self.checked);(self.kit/self.dest).write_bytes(self.before)
        self.seal=self.kit/'INTERLINKED-App/GUI-CHECKSUMS.json'
        self.old_seal=(json.dumps({'format':'interlinked-gui-checksums-v5','files':{'interlinked_buddy.py':digest(self.before)},'auditSha256':None})+'\n').encode()
        self.seal.write_bytes(self.old_seal)
        state=self.kit/'MAGIX-Workspace-State';state.mkdir()
        for name in ('binding.json','multi-binding.json'):(state/name).write_text('{"status":"UNBOUND"}',encoding='utf-8')
        (self.kit/'INTERLINKED-GUI-State').mkdir()
        self.overlay={'files':[{'path':self.relative,'sha256':digest(self.checked),'before_sha256':digest(self.before)}]}

    def tearDown(self):self.temp.cleanup()

    def publish(self,on_lock=None,atomic=None):
        acquired=[0]
        @contextmanager
        def lock(_):
            acquired[0]+=1
            if on_lock and acquired[0]==1:on_lock()
            yield
        with patch.object(publication,'SOURCE',self.source),patch.object(publication,'KIT',self.kit),patch.object(publication,'native_lock',lock),patch.object(publication.subprocess,'run',return_value=subprocess.CompletedProcess([],0)),patch.object(publication,'atomic',atomic or publication.atomic):
            publication.publish_kit(self.overlay,self.receipt)

    def test_checked_source_snapshot_is_used_after_source_changes_while_waiting(self):
        self.publish(on_lock=lambda:(self.source/self.relative).write_bytes(b'concurrent unreviewed source'))
        self.assertEqual(self.checked,(self.kit/self.dest).read_bytes())
        self.assertEqual('PUBLISHED',json.loads((self.receipt/'receipt.json').read_text())['status'])
        seal=json.loads(self.seal.read_text());self.assertEqual(digest(self.checked),seal['files']['interlinked_buddy.py'])

    def test_installed_target_change_while_waiting_is_preserved_and_rejected(self):
        concurrent=b'newer installed version from another publisher'
        with self.assertRaisesRegex(ValueError,'changed while waiting'):
            self.publish(on_lock=lambda:(self.kit/self.dest).write_bytes(concurrent))
        self.assertEqual(concurrent,(self.kit/self.dest).read_bytes())
        self.assertFalse((self.receipt/'receipt.json').exists())

    def test_installed_seal_change_while_waiting_is_rejected_before_any_copy(self):
        changed=b'{"format":"concurrent-new-seal"}'
        with self.assertRaisesRegex(ValueError,'seal changed while waiting'):
            self.publish(on_lock=lambda:self.seal.write_bytes(changed))
        self.assertEqual(changed,self.seal.read_bytes());self.assertEqual(self.before,(self.kit/self.dest).read_bytes())

    def test_target_readback_failure_restores_exact_old_bytes_and_records_failure(self):
        original=publication.atomic;corrupted=[False]
        def atomic(path,raw):
            original(path,raw)
            if Path(path)==self.kit/self.dest and not corrupted[0]:
                corrupted[0]=True;Path(path).write_bytes(b'corrupt published bytes')
        with self.assertRaisesRegex(ValueError,'readback differs'):self.publish(atomic=atomic)
        self.assertEqual(self.before,(self.kit/self.dest).read_bytes());self.assertEqual(self.old_seal,self.seal.read_bytes())
        receipt=json.loads((self.receipt/'receipt.json').read_text());self.assertEqual('FAILED_ROLLED_BACK',receipt['status']);self.assertEqual([],receipt['rollback_errors'])

    def test_failed_restore_reports_uncertainty_instead_of_success(self):
        original=publication.atomic;writes=[0]
        def atomic(path,raw):
            if Path(path)==self.kit/self.dest:
                writes[0]+=1
                if writes[0]>1:raise OSError('Synthetic restoration refusal')
                original(path,b'corrupt published bytes')
            else:original(path,raw)
        with self.assertRaisesRegex(RuntimeError,'could not be restored'):self.publish(atomic=atomic)
        receipt=json.loads((self.receipt/'receipt.json').read_text());self.assertEqual('PUBLICATION_UNCERTAIN',receipt['status']);self.assertEqual(1,len(receipt['rollback_errors']))


if __name__=='__main__':unittest.main()
