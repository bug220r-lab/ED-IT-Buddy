"""Hash-guarded Buddy-only source merge and idle installed-kit publication.

Explicit modes only. No app/process shutdown, project save/release, or workspace
data rewrite. Backups/receipts stay in Buddy's ignored .build directory.
"""
from __future__ import annotations
import argparse
from contextlib import ExitStack,contextmanager
import ctypes
import datetime as dt
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import uuid

ROOT=Path(__file__).resolve().parent
WORKSPACE=ROOT.parents[1]
SOURCE=Path(r'C:\Users\Jude\Documents\Codex\INTERLINKED')
KIT=Path(r'C:\Users\Jude\Documents\MSI-Project-Library')
BASE_SHA='c60fb2559a90a2301aa3d741c1d2e676e5ed90559fb40a63e27ef83d75f697aa'
spec=importlib.util.spec_from_file_location('buddy_publication_guards',ROOT/'interlinked_buddy.py')
guards=importlib.util.module_from_spec(spec);spec.loader.exec_module(guards)
def need(value,message):
    if not value:raise ValueError(message)
def sha(raw):return hashlib.sha256(raw).hexdigest()
def file_sha(path):return sha(guards.leaf(path).read_bytes())
def utc():return dt.datetime.now(dt.timezone.utc).isoformat()
def atomic(path,raw):
    path=guards.ordinary(path,missing=True);path.parent.mkdir(parents=True,exist_ok=True);guards.ordinary(path.parent)
    if path.exists():guards.leaf(path)
    temporary=path.with_name(path.name+'.buddy-'+uuid.uuid4().hex+'.tmp')
    with temporary.open('xb') as out:out.write(raw);out.flush();os.fsync(out.fileno())
    os.replace(temporary,path)
@contextmanager
def native_lock(path):
    guards.leaf(path)
    k=ctypes.WinDLL('kernel32',use_last_error=True);k.CreateFileW.argtypes=[ctypes.c_wchar_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p];k.CreateFileW.restype=ctypes.c_void_p;k.CloseHandle.argtypes=[ctypes.c_void_p]
    handle=k.CreateFileW(str(path),0x80000000,0,None,3,0x00200000,None)
    need(handle!=ctypes.c_void_p(-1).value,'An active GUI/workspace lock prevents publication: '+str(path))
    try:yield
    finally:k.CloseHandle(handle)
def baseline():
    need(os.name=='nt' and os.environ.get('COMPUTERNAME','').casefold()=='this-machine' and os.environ.get('USERNAME','').casefold()=='jude','Publication is pinned to THIS-MACHINE/Jude.')
    need(not ctypes.windll.shell32.IsUserAnAdmin(),'Run as the normal user, without administrator mode.')
    guards.ordinary(SOURCE);guards.ordinary(KIT)
    need(file_sha(KIT/'magix_workspace.py')==BASE_SHA,'Frozen installed base changed.')
    overlay=json.loads((ROOT/'source-overlay.json').read_text('utf-8-sig'))
    need(overlay['format']=='interlinked-buddy-source-overlay-v1' and Path(overlay['source'])==SOURCE,'Unknown source overlay.')
    for row in overlay['files']:
        path=guards.ordinary(ROOT/'candidate'/row['path']);need(file_sha(path)==row['sha256'],'Candidate source differs: '+row['path'])
    return overlay
def create_receipt(mode):
    parent=WORKSPACE/'.build';guards.ordinary(parent);folder=parent/('interlinked-buddy-'+mode+'-'+dt.datetime.now(dt.timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'-'+uuid.uuid4().hex[:8]);folder.mkdir();return folder
def merge_source(overlay,receipt):
    # Read the current manifest and verify all recorded bytes. Never "fix" an
    # unrelated concurrent change by refreshing its hash.
    manifest_path=SOURCE/'repository/DISTRIBUTION-MANIFEST.json';raw=guards.leaf(manifest_path).read_bytes();manifest=json.loads(raw)
    need(manifest['format']=='interlinked-distribution-manifest-v1','Unknown current distribution manifest.')
    for row in manifest['files']:need(file_sha(SOURCE/row['path'])==row['sha256'],'Unreviewed current source change: '+row['path'])
    for row in overlay['files']:
        target=SOURCE/row['path']
        if target.exists() and file_sha(target)==row['sha256']:continue
        if row.get('source_path'):need(file_sha(SOURCE/row['source_path'])==row['source_sha256'],'Source baseline changed: '+row['source_path'])
        if row['before_sha256'] is None:need(not target.exists(),'New source path already exists: '+row['path'])
        else:need(target.exists() and file_sha(target)==row['before_sha256'],'Current source changed: '+row['path'])
    backup=receipt/'source-before';backup.mkdir();records=[]
    for row in overlay['files']:
        target=SOURCE/row['path'];prior=target.read_bytes() if target.exists() else None
        if prior is not None:
            dest=backup/row['path'];dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(prior)
        records.append(dict(path=row['path'],before_sha256=sha(prior) if prior is not None else None,sha256=row['sha256']))
    (backup/'DISTRIBUTION-MANIFEST.json').write_bytes(raw)
    for row in overlay['files']:atomic(SOURCE/row['path'],(ROOT/'candidate'/row['path']).read_bytes())
    entries={row['path']:row for row in manifest['files']}
    for row in overlay['files']:
        target=SOURCE/row['path'];entries[row['path']]=dict(entries.get(row['path'],{}),path=row['path'],bytes=target.stat().st_size,sha256=row['sha256'],origin='Buddy companion integration reviewed source')
    manifest.update(createdUtc=utc(),previousManifestSha256=sha(raw),files=sorted(entries.values(),key=lambda row:row['path']))
    buddy_scope='Additive Buddy companion, lock-owner media ingestion and authenticated project work report.'
    if buddy_scope not in manifest.get('scope',''):manifest['scope']=manifest.get('scope','')+' '+buddy_scope
    manifest['summary']=dict(files=len(entries),bytes=sum(row['bytes'] for row in entries.values()),pythonSourcesParsed=sum(row['path'].endswith('.py') for row in entries.values()))
    manifest['preservedBaseline']='Original SOURCE-MANIFEST, MAGIX-EXTENSION-MANIFEST, frozen workspace base and original launcher source remain exact. Buddy launcher is a separate current source addition.'
    atomic(manifest_path,(json.dumps(manifest,indent=2,ensure_ascii=False)+'\n').encode('utf-8'))
    guards.write(receipt/'receipt.json',dict(format='interlinked-buddy-publication-v1',mode='source',status='MERGED',utc=utc(),files=records,current_distribution_sha256=file_sha(manifest_path),project_files_written=False))
    subprocess.run([sys.executable,'-I','-B',str(SOURCE/'repository/verify_source_snapshot.py')],check=True)
def kit_targets(overlay):
    result=[]
    for row in overlay['files']:
        rel=row['path']
        if rel.startswith('repository/app/'):dest='INTERLINKED-App/'+rel.removeprefix('repository/app/')
        elif rel=='repository/work/magix-buddy/candidate/magix_projects.py':dest='magix_projects.py'
        else:raise ValueError('Unexpected installed target: '+rel)
        result.append((row,dest))
    return result
def publish_kit(overlay,receipt):
    seal_path=KIT/'INTERLINKED-App/GUI-CHECKSUMS.json';old_raw=guards.leaf(seal_path).read_bytes();seal=json.loads(old_raw)
    need(seal.get('format') in ('interlinked-gui-checksums-v4','interlinked-gui-checksums-v5'),'Unknown installed GUI seal.')
    for name,digest in seal['files'].items():need(file_sha(KIT/'INTERLINKED-App'/name)==digest,'Installed GUI differs: '+name)
    targets=kit_targets(overlay);buffers={};baseline_targets={}
    for row,dest in targets:
        # Publish the checked byte snapshot, never a later re-read of mutable
        # source files after waiting for another updater/session to finish.
        buffers[dest]=guards.leaf(SOURCE/row['path']).read_bytes()
        need(sha(buffers[dest])==row['sha256'],'Merge the reviewed current source before publication.')
        target=KIT/dest
        baseline_targets[dest]=guards.leaf(target).read_bytes() if target.exists() else None
        if baseline_targets[dest] is not None and sha(baseline_targets[dest])==row['sha256']:continue
        before=row['before_sha256'] if dest!='magix_projects.py' or row['before_sha256'] is not None else next(r for r in overlay['files'] if r['path'].endswith('magix_projects.py'))['source_sha256']
        if before is None:need(not target.exists(),'Installed new module already exists.')
        else:need(target.exists() and file_sha(target)==before,'Installed baseline changed: '+dest)
    fixed=['INTERLINKED-App/interlinked_master.py','INTERLINKED-App/master_hook.py','INTERLINKED-App/master_bridge.py','INTERLINKED-Desktop/INTERLINKED.Master.Worker.exe','INTERLINKED-GUI-State/master/device.json','INTERLINKED-GUI-State/master/OWNER.json','magix_workspace.py','MAGIX-All-Projects-Index.json']
    preserved={p:file_sha(KIT/p) for p in fixed if (KIT/p).exists()}
    backup=receipt/'kit-before';backup.mkdir();prior={}
    with ExitStack() as stack:
        for p in ('INTERLINKED-GUI-State/.gui-instance.lock','MAGIX-Workspace-State/.workflow.lock'):stack.enter_context(native_lock(KIT/p))
        need(guards.leaf(seal_path).read_bytes()==old_raw,'Installed GUI seal changed while waiting for publication locks.')
        for name,digest in seal['files'].items():need(file_sha(KIT/'INTERLINKED-App'/name)==digest,'Installed GUI changed while waiting for publication locks: '+name)
        for row,dest in targets:
            target=KIT/dest;current=guards.leaf(target).read_bytes() if target.exists() else None
            need(current==baseline_targets[dest],'Installed target changed while waiting for publication locks: '+dest)
        for p,digest in preserved.items():need(file_sha(KIT/p)==digest,'Preserved dependency changed while waiting for publication locks: '+p)
        for name in ('binding.json','multi-binding.json'):need(guards.read(KIT/'MAGIX-Workspace-State'/name).get('status')=='UNBOUND','Finish the managed MAGIX session before publication.')
        running=subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-Command',"if(Get-Process -Name Videodeluxe -ErrorAction SilentlyContinue){exit 4}"],capture_output=True)
        need(running.returncode==0,'Close MAGIX normally before publishing the reviewed launcher.')
        for row,dest in targets:
            prior[dest]=baseline_targets[dest]
            if prior[dest] is not None:
                out=backup/dest;out.parent.mkdir(parents=True,exist_ok=True);out.write_bytes(prior[dest])
        (backup/'GUI-CHECKSUMS.json').write_bytes(old_raw)
        try:
            for row,dest in targets:
                atomic(KIT/dest,buffers[dest])
                need(file_sha(KIT/dest)==row['sha256'],'Published target readback differs: '+dest)
            newseal=dict(seal,format='interlinked-gui-checksums-v5',files=dict(seal['files']))
            for row,dest in targets:
                if dest.startswith('INTERLINKED-App/'):newseal['files'][dest.removeprefix('INTERLINKED-App/')]=row['sha256']
            seal_bytes=(json.dumps(newseal,indent=2)+'\n').encode('utf-8')
            atomic(seal_path,seal_bytes)
            need(guards.leaf(seal_path).read_bytes()==seal_bytes,'Published GUI seal readback differs.')
            for p,digest in preserved.items():need(file_sha(KIT/p)==digest,'Preserved MASTER/base bytes changed: '+p)
        except BaseException as error:
            rollback_errors=[]
            for dest,raw in prior.items():
                try:
                    target=KIT/dest
                    if raw is not None:
                        atomic(target,raw);need(guards.leaf(target).read_bytes()==raw,'Restored bytes differ.')
                    elif target.exists():guards.leaf(target).unlink()
                except BaseException as rollback_error:rollback_errors.append(dict(path=dest,error=str(rollback_error)))
            try:
                atomic(seal_path,old_raw);need(guards.leaf(seal_path).read_bytes()==old_raw,'Restored GUI seal differs.')
            except BaseException as rollback_error:rollback_errors.append(dict(path='INTERLINKED-App/GUI-CHECKSUMS.json',error=str(rollback_error)))
            guards.write(receipt/'receipt.json',dict(format='interlinked-buddy-publication-v1',mode='installed',status='PUBLICATION_UNCERTAIN' if rollback_errors else 'FAILED_ROLLED_BACK',utc=utc(),error=str(error),rollback_errors=rollback_errors,project_files_written=False,workspace_state_written=False))
            if rollback_errors:raise RuntimeError('Publication failed and some app bytes could not be restored; retain backups and reconcile the receipt.') from error
            raise
    guards.write(receipt/'receipt.json',dict(format='interlinked-buddy-publication-v1',mode='installed',status='PUBLISHED',utc=utc(),files=[dict(path=dest,sha256=row['sha256']) for row,dest in targets],seal_sha256=file_sha(seal_path),preserved_files=preserved,project_files_written=False,workspace_state_written=False))
def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('mode',choices=('check','merge-source','publish-kit'));args=parser.parse_args();overlay=baseline()
    if args.mode=='check':print(json.dumps(dict(status='CANDIDATE_VERIFIED',files=len(overlay['files']))));return
    receipt=create_receipt(args.mode)
    if args.mode=='merge-source':merge_source(overlay,receipt)
    else:publish_kit(overlay,receipt)
    print(json.dumps(dict(status='PASS',mode=args.mode,receipt=str(receipt/'receipt.json'))))
if __name__=='__main__':main()
