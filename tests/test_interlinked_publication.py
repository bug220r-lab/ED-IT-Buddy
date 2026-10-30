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
        with patch.object(publication,'SOURCE',self.source),patch.object(publication,'KIT',self.kit),patch.object(publication,'ROOT',self.root),patch.object(publication,'native_lock',lock),patch.object(publication,'process_names',return_value=[]),patch.object(publication.subprocess,'run',return_value=subprocess.CompletedProcess([],0)),patch.object(publication,'atomic',atomic or publication.atomic):
            publication.publish_kit(self.overlay,self.receipt)

    def test_abandoned_stats_mw5_and_folder_journals_refuse_before_writes(self):
        for rel,fmt in [('stats/session.json','interlinked-stats-session-v1'),('mw5/session.json','interlinked-mw5-session-v1'),('folder-session.json','interlinked-folder-session-v1')]:
            for state in ('RUNNING','RECOVERY_REQUIRED','UNKNOWN'):
                with self.subTest(journal=rel,status=state):
                    self.receipt=Path(tempfile.mkdtemp(prefix='refusal-receipt-',dir=self.root))
                    p=self.kit/'INTERLINKED-GUI-State'/rel;p.parent.mkdir(exist_ok=True);p.write_text(json.dumps(dict(format=fmt,status=state)))
                    with self.assertRaisesRegex(ValueError,'session|MW5'):self.publish()
                    self.assertEqual((self.kit/self.dest).read_bytes(),self.before)
                    p.unlink()
            if rel.startswith('mw5/'):
                self.receipt=Path(tempfile.mkdtemp(prefix='missing-receipt-',dir=self.root))
                with self.assertRaises((OSError,ValueError)):self.publish()
                p.parent.rmdir()

    def test_editor_or_desktop_process_refuses_even_with_idle_journals(self):
        for process in ('Videodeluxe','MAGIXEditor','UE4Editor','UnrealEditor-Win64','MechWarrior5Editor','INTERLINKED','INTERLINKED.Master.Worker'):
            with self.subTest(process=process),patch.object(publication,'KIT',self.kit),patch.object(publication,'process_names',return_value=[process]):
                with self.assertRaises(ValueError):publication.assert_idle()

    def test_missing_process_inspection_is_not_idle(self):
        with patch.object(publication.subprocess,'run',return_value=subprocess.CompletedProcess([],1,stdout='',stderr='denied')):
            with self.assertRaisesRegex(ValueError,'inspection failed'):publication.process_names()
        with patch.object(publication.subprocess,'run',return_value=subprocess.CompletedProcess([],0,stdout='{"format":"buddy-publication-processes-v1","names":[]}')):
            with self.assertRaisesRegex(ValueError,'Incomplete'):publication.process_names()

    def test_process_inspection_timeout_and_malformed_output_refuse(self):
        with patch.object(publication.subprocess,'run',side_effect=subprocess.TimeoutExpired('fixture',10)):
            with self.assertRaisesRegex(ValueError,'unavailable'):publication.process_names()
        for output in ('not json','null','[]','{"format":"buddy-publication-processes-v1","names":[null]}'):
            with self.subTest(output=output),patch.object(publication.subprocess,'run',return_value=subprocess.CompletedProcess([],0,stdout=output)):
                with self.assertRaises(ValueError):publication.process_names()

    def test_recovery_state_appearing_during_publication_rolls_back(self):
        original=publication.atomic;injected=[False]
        def atomic(p,raw):
            original(p,raw)
            if p==self.kit/self.dest and not injected[0]:
                injected[0]=True
                folder=self.kit/'INTERLINKED-GUI-State/stats';folder.mkdir()
                (folder/'session.json').write_text('{"format":"interlinked-stats-session-v1","status":"RECOVERY_REQUIRED"}')
        with self.assertRaisesRegex(ValueError,'session'):self.publish(atomic=atomic)
        self.assertEqual((self.kit/self.dest).read_bytes(),self.before)
        self.assertEqual(self.seal.read_bytes(),self.old_seal)

    def test_v7_preserves_unknown_fields_and_non_target_configuration(self):
        config=self.kit/'INTERLINKED-App/interlinked_configuration.py';config.write_bytes(b'current v2 configuration')
        seal=json.loads(self.old_seal);seal.update(format='interlinked-gui-checksums-v7',startupRecord={'retain':'exactly'})
        seal['files']['interlinked_configuration.py']=digest(config.read_bytes());self.seal.write_text(json.dumps(seal))
        self.publish();after=json.loads(self.seal.read_text())
        self.assertEqual(after['format'],seal['format']);self.assertEqual(after['startupRecord'],seal['startupRecord'])
        self.assertEqual(config.read_bytes(),b'current v2 configuration');self.assertEqual(after['files']['interlinked_configuration.py'],seal['files']['interlinked_configuration.py'])

    def test_source_only_changes_do_not_leak_into_installed_snapshot(self):
        self.checked=b'new source with unrelated newer work and report';(self.source/self.relative).write_bytes(self.checked)
        row=self.overlay['files'][0];row.update(sha256=digest(self.checked),installed_before_sha256=digest(self.before),installed_sha256=digest(b'previous installed companion plus report'),installed_candidate_path='installed/'+self.dest)
        candidate=self.root/'candidate'/row['installed_candidate_path'];candidate.parent.mkdir(parents=True);candidate.write_bytes(b'previous installed companion plus report')
        self.publish()
        self.assertEqual((self.kit/self.dest).read_bytes(),b'previous installed companion plus report')
        self.assertEqual((self.source/self.relative).read_bytes(),self.checked)
        self.assertEqual(json.loads(self.seal.read_text())['files']['interlinked_buddy.py'],row['installed_sha256'])

    def test_v7_non_target_change_while_waiting_refuses_publication(self):
        config=self.kit/'INTERLINKED-App/interlinked_configuration.py';config.write_bytes(b'current v2 configuration')
        seal=json.loads(self.old_seal);seal['format']='interlinked-gui-checksums-v7';seal['files']['interlinked_configuration.py']=digest(config.read_bytes());self.seal.write_text(json.dumps(seal))
        with self.assertRaisesRegex(ValueError,'changed while waiting'):
            self.publish(on_lock=lambda:config.write_bytes(b'newer owner update'))
        self.assertEqual((self.kit/self.dest).read_bytes(),self.before)

    def test_changed_installed_candidate_is_refused(self):
        row=self.overlay['files'][0];row.update(installed_before_sha256=digest(self.before),installed_sha256=digest(b'accepted installed candidate'),installed_candidate_path='installed/'+self.dest)
        candidate=self.root/'candidate'/row['installed_candidate_path'];candidate.parent.mkdir(parents=True);candidate.write_bytes(b'changed unreviewed candidate')
        with self.assertRaisesRegex(ValueError,'candidate changed'):self.publish()
        self.assertEqual((self.kit/self.dest).read_bytes(),self.before)

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
