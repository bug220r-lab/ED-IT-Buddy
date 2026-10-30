"""Synthetic companion tests. Never opens MAGIX or a production cache."""
import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import threading
import types
import unittest
import uuid
import ctypes
import http.client
import sys
import subprocess

HERE=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('buddy_under_test',HERE/'interlinked_buddy.py')
b=importlib.util.module_from_spec(spec);spec.loader.exec_module(b)

class IngestTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory(prefix='buddy-ingest-');self.root=Path(self.tmp.name)
        self.staging=self.root/'staging';b.private_directory(self.staging)
        b.write(self.staging/'OWNER.json',dict(format='buddy-assistant-staging-v1',sid=b.sid(),computer=os.environ.get('COMPUTERNAME','')))
        self.project='synthetic-band';self.request=uuid.uuid4().hex
        self.batch=self.staging/self.request;self.batch.mkdir();self.raw=b'generated synthetic video bytes\x00'*100
        (self.batch/'clip.mp4').write_bytes(self.raw)
        self.manifest=dict(format='buddy-asset-manifest-v1',request_id=self.request,project_id=self.project,files=[dict(path='clip.mp4',bytes=len(self.raw),sha256=hashlib.sha256(self.raw).hexdigest())])
        b.write(self.batch/'manifest.json',self.manifest)
        self.state=b.owned_state(self.root/'state',dict(format=b.FORMAT,gui_owner={'fixture':True}))
        self.files=self.root/'Files';self.files.mkdir();self.destination=self.files/'media';self.destination.mkdir()
        self.req=dict(request_id=self.request,project_id=self.project,manifest_path=str(self.batch/'manifest.json'),manifest_sha256=b.digest(self.batch/'manifest.json'),staging_root=str(self.staging),session_id=uuid.uuid4().hex)
    def tearDown(self):self.tmp.cleanup()
    def ingest(self):return b.ingest_files(self.state,self.req,self.destination,self.files,self.root/'alias')
    def test_additive_delivery_and_dedup(self):
        old=self.destination/'existing.mp4';old.write_bytes(b'original')
        result=self.ingest();again=self.ingest()
        self.assertEqual(result['status'],'COMPLETED');self.assertEqual(again['destination'],result['destination'])
        self.assertEqual(old.read_bytes(),b'original');self.assertEqual((self.batch/'clip.mp4').read_bytes(),self.raw)
        self.assertEqual(Path(result['files'][0]['physical_path']).read_bytes(),self.raw)
        self.assertIn('magix_path',result['files'][0]);self.assertFalse(result['automatic_save'])
    def test_queued_receipt_can_start(self):
        b.write(self.state/'operations'/(self.request+'.json'),dict(request_id=self.request,project_id=self.project,manifest_sha256=self.req['manifest_sha256'],status='QUEUED'))
        self.assertEqual(self.ingest()['status'],'COMPLETED')
    def test_source_changed_preserves_originals(self):
        (self.batch/'clip.mp4').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError,'differs'):self.ingest()
        self.assertEqual(list(self.destination.iterdir()),[])
    def test_path_escape_refused(self):
        self.manifest['files'][0]['path']='../outside.mp4';b.write(self.batch/'manifest.json',self.manifest)
        self.req['manifest_sha256']=b.digest(self.batch/'manifest.json')
        with self.assertRaises(ValueError):self.ingest()
    def test_collision_preserved(self):
        target=self.destination/'BuddyAssets'/self.request;target.mkdir(parents=True);(target/'clip.mp4').write_bytes(b'other')
        with self.assertRaisesRegex(ValueError,'ownership intent'):self.ingest()
        self.assertEqual((target/'clip.mp4').read_bytes(),b'other')
    def test_identical_foreign_directory_not_adopted(self):
        target=self.destination/'BuddyAssets'/self.request;target.mkdir(parents=True);(target/'clip.mp4').write_bytes(self.raw)
        with self.assertRaisesRegex(ValueError,'ownership intent'):self.ingest()
    def test_published_without_ack_reconciles(self):
        first=self.ingest();path=self.state/'operations'/(self.request+'.json');record=b.read(path);record['status']='COPYING';record.pop('files');b.write(path,record)
        self.assertEqual(self.ingest()['status'],'COMPLETED');self.assertEqual(len(list((self.destination/'BuddyAssets').iterdir())),1)
    def test_partial_copy_resumes(self):
        target=self.destination/'BuddyAssets'/('.incoming-'+self.request);target.mkdir(parents=True);(target/'clip.mp4.buddy-part').write_bytes(b'partial')
        b.write(self.state/'operations'/(self.request+'.json'),dict(request_id=self.request,project_id=self.project,manifest_sha256=self.req['manifest_sha256'],status='COPYING',destination=str(self.destination/'BuddyAssets'/self.request)))
        self.assertEqual(self.ingest()['status'],'COMPLETED')
    def test_lexical_parent_escape_refused(self):
        outside=self.root/'outside.json';outside.write_text('{}')
        self.req['manifest_path']=str(self.staging/'..'/'outside.json')
        with self.assertRaisesRegex(ValueError,'outside Buddy staging'):self.ingest()
    def test_hardlinked_source_refused(self):
        os.link(self.batch/'clip.mp4',self.batch/'other.mp4')
        with self.assertRaisesRegex(ValueError,'non-hardlinked'):self.ingest()
    def test_wrong_staging_owner_refused(self):
        b.write(self.staging/'OWNER.json',dict(format='other'))
        with self.assertRaisesRegex(ValueError,'ownership'):self.ingest()
    def test_live_prompt_executes_inside_lock_owner(self):
        calls=[];lock=threading.Lock();session=self.req['session_id'];self.req['project_id']=self.project
        b.write(self.state/'requests'/(self.request+'.json'),self.req)
        class Selected:
            def ready(_):self.assertTrue(lock.locked());calls.append('ready')
        manager=types.SimpleNamespace(c=types.SimpleNamespace(cache=self.root,kit=self.root),exact_owner=lambda:calls.append('owner'),read_binding=lambda:dict(status='BOUND',projectId=self.project,aliases=[dict(root='OneDrive/Documents',phase='BOUND')]),check_all_links=lambda record:calls.append('links'),target=lambda project,root:self.destination,link=lambda root:self.root/'alias')
        # The callback's Files derivation is production-fixed; make the fixture
        # physical root match it without changing any real paths.
        physical=self.root/self.project/'Files';physical.mkdir(parents=True);destination=physical/'media';destination.mkdir();manager.target=lambda project,root:destination
        values=iter(['BUDDY_INGEST '+self.request,'DONE']);wrapped=b.magix_prompt(manager,Selected(),self.project,session,self.state,lambda message:next(values))
        with lock,contextlib.redirect_stdout(io.StringIO()) as output:self.assertEqual(wrapped('prompt'),'DONE')
        self.assertIn('BUDDY_RECEIPT',output.getvalue());self.assertEqual(b.read(self.state/'operations'/(self.request+'.json'))['status'],'COMPLETED');self.assertEqual(calls,['owner','ready','links'])
    def test_wrong_session_does_not_copy(self):
        self.req['session_id']='0'*32;b.write(self.state/'requests'/(self.request+'.json'),self.req)
        manager=types.SimpleNamespace(c=types.SimpleNamespace(kit=self.root));values=iter(['BUDDY_INGEST '+self.request,'DONE'])
        wrapped=b.magix_prompt(manager,None,self.project,'1'*32,self.state,lambda message:next(values))
        with contextlib.redirect_stdout(io.StringIO()):self.assertEqual(wrapped('prompt'),'DONE')
        self.assertEqual(b.read(self.state/'operations'/(self.request+'.json'))['status'],'FAILED');self.assertEqual(list(self.destination.iterdir()),[])
    @unittest.skipUnless(os.name=='nt','Windows native share-lock fixture')
    def test_two_native_locks_remain_held_during_ingest(self):
        kernel=ctypes.WinDLL('kernel32',use_last_error=True)
        kernel.CreateFileW.argtypes=[ctypes.c_wchar_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p];kernel.CreateFileW.restype=ctypes.c_void_p;kernel.CloseHandle.argtypes=[ctypes.c_void_p]
        locks=[self.root/'.workflow.lock',self.root/'.client.lock'];handles=[]
        try:
            for path in locks:
                handle=kernel.CreateFileW(str(path),0xc0000000,0,None,4,0x80,None);self.assertNotEqual(handle,ctypes.c_void_p(-1).value);handles.append(handle)
            for path in locks:
                with self.assertRaises(PermissionError):path.open('rb')
            self.assertEqual(self.ingest()['status'],'COMPLETED')
            for path in locks:
                with self.assertRaises(PermissionError):path.open('rb')
        finally:
            for handle in handles:kernel.CloseHandle(handle)
    @unittest.skipUnless(os.name=='nt','Windows inherited private DACL fixture')
    def test_private_staging_allows_same_user_system_inheritance(self):
        target=str(self.staging).replace("'","''")
        script="$ErrorActionPreference='Stop'; $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User; $acl=New-Object Security.AccessControl.DirectorySecurity; $acl.SetAccessRuleProtection($true,$false); foreach($s in @($sid,(New-Object Security.Principal.SecurityIdentifier('S-1-5-18')))){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($s,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}; [IO.Directory]::SetAccessControl('"+target+"',$acl)"
        completed=subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-Command',script],creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0),capture_output=True)
        self.assertEqual(completed.returncode,0,completed.stderr.decode('utf-8','replace'))
        b.private_acl(self.staging);self.assertEqual(self.ingest()['status'],'COMPLETED')

class HttpTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory(prefix='buddy-http-');root=Path(self.tmp.name);self.root=root
        name='buddy_gui_fixture_'+uuid.uuid4().hex
        path=HERE/'candidate/repository/app/interlinked_gui.py'
        spec=importlib.util.spec_from_file_location(name,path);self.g=importlib.util.module_from_spec(spec);sys.modules[name]=self.g;spec.loader.exec_module(self.g)
        original_load=self.g.load_companion
        self.g.load_companion=lambda name:types.SimpleNamespace(safe_service=lambda *args,**kwargs:types.SimpleNamespace(close=lambda:None)) if name in ('interlinked_master','interlinked_jobs') else original_load(name)
        b.install(self.g);self.controller=self.g.Controller(self.g.demo_runtime(root/'state'));self.server=self.g.Server(self.controller)
        self.thread=threading.Thread(target=self.server.serve_forever,daemon=True);self.thread.start()
    def tearDown(self):
        self.server.shutdown();self.server.server_close();self.controller.close();self.thread.join();self.tmp.cleanup()
    def request(self,path,method='GET',body=None,headers=None):
        connection=http.client.HTTPConnection('127.0.0.1',self.server.server_port,timeout=5)
        headers=dict(headers or {});headers.setdefault('X-INTERLINKED-Buddy-Token',self.controller.buddy.token)
        if body is not None:body=json.dumps(body);headers['Content-Type']='application/json'
        connection.request(method,path,body=body,headers=headers);response=connection.getresponse();value=json.loads(response.read());connection.close();return response.status,value
    def test_native_scoped_projects(self):
        status,result=self.request('/api/buddy/projects');self.assertEqual(status,200);self.assertEqual(result['projects'][0]['original_id'],'demo-project-1')
    def test_gui_token_cannot_authorize_companion(self):
        status,_=self.request('/api/buddy/projects',headers={'X-INTERLINKED-Buddy-Token':self.server.token});self.assertEqual(status,403)
    def test_browser_origin_refused(self):
        status,_=self.request('/api/buddy/projects',headers={'Origin':self.server.origin});self.assertEqual(status,403)
    def test_unknown_route_refused(self):
        status,_=self.request('/api/buddy/execute');self.assertEqual(status,403)
    def test_report_separate_from_project_files(self):
        status,result=self.request('/api/buddy/report','POST',dict(project_id='demo-project-1',summary={'completed':2,'failed':1}));self.assertEqual(status,200)
        self.assertEqual(result['summary']['completed'],2);self.assertTrue((self.controller.buddy.state/'reports/demo-project-1.json').exists())
        status,result=self.request('/api/buddy/reports');self.assertEqual(status,200);self.assertEqual(len(result['reports']),1)
    def test_gui_report_requires_existing_gui_capability(self):
        status,_=self.request('/api/buddy-report?project=demo-project-1');self.assertEqual(status,403)
        status,result=self.request('/api/buddy-report?project=demo-project-1',headers={'X-INTERLINKED-Token':self.server.token});self.assertEqual(status,200);self.assertFalse(result['available']);self.assertNotIn('token',result)
    def test_gui_report_has_counts_blockers_and_reduced_output_references(self):
        task=dict(task_id='task-1',kind='download',status='completed',delivery_status='FAILED',message='<script>literal blocker</script>',outputs=[dict(project_relative_path='media/clip.mp4',physical_path='C:/private/clip.mp4',magix_path='C:/alias/clip.mp4',sha256='a'*64)])
        status,_=self.request('/api/buddy/report','POST',dict(project_id='demo-project-1',summary=dict(observed_at='2026-10-30T12:00:00Z',tasks=[task])));self.assertEqual(status,200)
        status,result=self.request('/api/buddy-report?project=demo-project-1',headers={'X-INTERLINKED-Token':self.server.token});self.assertEqual(status,200)
        self.assertEqual(result['counts']['prepared'],1);self.assertEqual(result['counts']['delivered'],0);self.assertEqual(result['counts']['attention'],1)
        self.assertEqual(result['tasks'][0]['message'],task['message']);self.assertEqual(result['tasks'][0]['outputs'],[dict(path='media/clip.mp4',sha256='a'*64)])
        self.assertNotIn('physical_path',json.dumps(result));self.assertNotIn('token',result)
    def test_gui_report_maps_ui_identity_to_stable_original(self):
        self.controller.catalog=lambda:dict(projects=[dict(id='opaque-ui-id',originalId='stable-original',name='Synthetic general project')])
        status,_=self.request('/api/buddy/report','POST',dict(project_id='stable-original',summary=dict(tasks=[])));self.assertEqual(status,200)
        status,result=self.request('/api/buddy-report?project=opaque-ui-id',headers={'X-INTERLINKED-Token':self.server.token});self.assertEqual(status,200);self.assertEqual(result['project_id'],'stable-original');self.assertTrue(result['available'])
        status,_=self.request('/api/buddy-report?project=stable-original',headers={'X-INTERLINKED-Token':self.server.token});self.assertEqual(status,403)
    def test_gui_report_limits_tasks_but_keeps_full_counts(self):
        tasks=[dict(task_id=str(i),kind='index',status='completed',delivery_status='not_delivered') for i in range(101)]+[dict(task_id='failure',kind='download',status='failed',delivery_status='not_delivered',message='Source unavailable')]
        status,_=self.request('/api/buddy/report','POST',dict(project_id='demo-project-1',summary=dict(tasks=tasks)));self.assertEqual(status,200)
        status,result=self.request('/api/buddy-report?project=demo-project-1',headers={'X-INTERLINKED-Token':self.server.token});self.assertEqual(status,200);self.assertEqual(result['counts']['total'],102);self.assertEqual(result['counts']['prepared'],101);self.assertEqual(len(result['tasks']),100);self.assertTrue(result['truncated']);self.assertEqual(result['tasks'][0]['task_id'],'failure')
    def test_staging_escape_refused(self):
        status,result=self.request('/api/buddy/ingest','POST',dict(project_id='demo-project-1',request_id='0'*32,manifest_path=str(self.controller.r.state/'OWNER.json')));self.assertEqual(status,409)
    def test_receipt_not_found_is_observation(self):
        status,result=self.request('/api/buddy/operation?request_id='+'0'*32);self.assertEqual(status,200);self.assertEqual(result['status'],'NOT_FOUND')
    def test_delivery_blocks_done_and_new_project_actions(self):
        self.controller.buddy.pending='1'*32
        try:
            with self.assertRaisesRegex(ValueError,'delivery'):self.controller.session_input('DONE','2'*32,1)
            with self.assertRaisesRegex(ValueError,'delivery'):self.controller.start('open','demo-project-1')
            self.assertTrue(self.controller.busy())
        finally:self.controller.buddy.pending=None
    def test_child_exit_marks_missing_ack_uncertain(self):
        request='3'*32;self.controller.buddy.pending=request
        b.write(self.controller.buddy.state/'operations'/(request+'.json'),dict(request_id=request,status='COPYING'))
        self.controller.job=dict(id='4'*32,action='open',projectId='demo-project-1',status='WAITING_FOR_MAGIX')
        self.controller.finish(2,'BLOCKED')
        result=self.controller.buddy.operation(request);self.assertEqual(result['status'],'UNCERTAIN');self.assertIsNone(self.controller.buddy.pending)
    def test_discovery_is_private_and_removed_on_close(self):
        service=self.controller.buddy;service.endpoint_root=self.root/'endpoint';self.controller.r.demo=False
        try:
            service.publish(self.server.origin);descriptor=b.read(service.endpoint);self.assertEqual(descriptor['token'],service.token)
            b.private_acl(service.endpoint_root);service.close();self.assertFalse(service.endpoint.exists())
        finally:self.controller.r.demo=True

if __name__=='__main__':unittest.main(verbosity=2)
