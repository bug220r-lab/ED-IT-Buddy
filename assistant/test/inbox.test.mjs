import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {Inbox,SourceReader} from '../inbox.mjs';
import {TaskStore} from '../state.mjs';
import {createHash} from 'node:crypto';
import {spawn} from 'node:child_process';
import {fileURLToPath} from 'node:url';

function fixture(){const root=fs.mkdtempSync(path.join(os.tmpdir(),'bi-'));const store=new TaskStore(path.join(root,'state'));let now=Date.now();const app={root:store.root,store,connected:false,knownProject(id){if(id!=='project-1')throw Error('Unknown project');}};const inbox=new Inbox(app,{now:()=>now});return {root,app,store,inbox,advance(n=15000){now+=n;},close(){inbox.close();store.close();fs.rmSync(root,{recursive:true,force:true});}};}
test('a closing broker cannot overwrite a newer ledger owner after an asynchronous transfer returns',()=>{const f=fixture();try{f.inbox.close();f.store.save();f.store.close();const next=new TaskStore(f.store.root);try{next.data.owner_test='new owner';next.save();assert.throws(()=>f.store.save(),/closed/);assert.equal(JSON.parse(fs.readFileSync(next.file)).owner_test,'new owner');}finally{next.close();}}finally{f.close();}});
test('watch baselines skip existing files, reconcile offline additions and changes, and retain removed-watch tasks',()=>{const f=fixture();try{const folder=path.join(f.root,'videos');fs.mkdirSync(folder);const old=path.join(folder,'old.mp4');fs.writeFileSync(old,'old');const w=f.inbox.addWatch(folder);f.inbox.scan();assert.equal(f.store.data.tasks.length,0);fs.writeFileSync(old,'new bytes');fs.writeFileSync(path.join(folder,'new \u2603.mov'),'new');fs.writeFileSync(path.join(folder,'partial.mp4.part'),'partial');f.inbox.scan();f.inbox.scan();assert.equal(f.store.data.tasks.length,2);assert.equal(f.store.data.tasks[0].project_id,null);f.inbox.watchAction(w.id,'remove');assert.equal(f.store.data.tasks.length,2);}finally{f.close();}});
test('watches are nonrecursive by default and pause preserves changes for resume',()=>{const f=fixture();try{const folder=path.join(f.root,'videos');fs.mkdirSync(folder);const w=f.inbox.addWatch(folder);fs.mkdirSync(path.join(folder,'nested'));fs.writeFileSync(path.join(folder,'nested','skip.mp4'),'bytes');f.inbox.scan();assert.equal(f.store.data.tasks.length,0);f.inbox.watchAction(w.id,'pause');fs.writeFileSync(path.join(folder,'later.mp4'),'bytes');f.inbox.scan();assert.equal(f.store.data.tasks.length,0);f.inbox.watchAction(w.id,'resume');f.inbox.scan();assert.equal(f.store.data.tasks.length,1);}finally{f.close();}});
test('startup reconciliation queues videos added while Buddy was closed',()=>{const f=fixture();try{const folder=path.join(f.root,'videos');fs.mkdirSync(folder);f.inbox.addWatch(folder);f.inbox.close();fs.writeFileSync(path.join(folder,'offline.mp4'),'bytes');const next=new Inbox(f.app);try{assert.equal(f.store.data.tasks.length,1);}finally{next.close();}}finally{f.close();}});
test('three observations over 30 seconds are required; changed sources queue new revisions',async()=>{const f=fixture();try{const file=path.join(f.root,'export.mp4');fs.writeFileSync(file,'video');const t=f.inbox.queue(file);assert.equal(f.inbox.queue(file),t);let transfers=0;f.app.connected=true;f.app.capabilities={inbox_protocol:1,archive_configured:true};f.inbox.transfer=async()=>{transfers++;};await f.inbox.tick();f.advance();await f.inbox.tick();assert.equal(transfers,0);f.advance();await f.inbox.tick();assert.equal(transfers,1);fs.writeFileSync(file,'new video version');await f.inbox.tick();assert.equal(t.status,'cancelled');assert.equal(f.store.data.tasks.length,2);}finally{f.close();}});
test('pause/cancel survive transfer errors and do not erase stored state',async()=>{const f=fixture();try{const file=path.join(f.root,'export.mp4');fs.writeFileSync(file,'video');const t=f.inbox.queue(file);f.app.connected=true;f.app.capabilities={inbox_protocol:1,archive_configured:true};t.observations=3;f.advance(31000);f.inbox.transfer=async()=>{t.status='paused';throw Error('checkpoint');};await f.inbox.tick();assert.equal(t.status,'paused');assert.equal(t.storage_status,'waiting');}finally{f.close();}});
test('native source reader denies writers and returns verified Unicode-source chunks', {skip:process.platform!=='win32'},async()=>{const f=fixture();try{const file=path.join(f.root,'show \u2603.mp4'),chunk=path.join(f.root,'chunk.part');const bytes=Buffer.from('native locked source');fs.writeFileSync(file,bytes);const reader=new SourceReader(file,chunk);try{const header=await reader.next();assert.equal(header.sha256,createHash('sha256').update(bytes).digest('hex'));assert.throws(()=>fs.writeFileSync(file,'writer'));const result=await reader.read(0,bytes.length);assert.equal(result.sha256,header.sha256);assert.deepEqual(fs.readFileSync(chunk),bytes);}finally{reader.close();await new Promise(r=>setTimeout(r,100));}}finally{f.close();}});

test('broker resumes a multi-chunk interruption against the real archive engine, then indexes without project delivery', {skip:process.platform!=='win32',timeout:60000},async()=>{
 const f=fixture();let child;
 try{
  const python=process.env.EDIT_BUDDY_TEST_PYTHON||path.join(os.homedir(),'.cache','codex-runtimes','codex-primary-runtime','dependencies','python','python.exe');
  child=spawn(python,['-B',fileURLToPath(new URL('../../tests/archive_rpc_fixture.py',import.meta.url)),path.join(f.root,'w'),path.join(f.root,'archive'),'--fail-after-receipt'],{stdio:['pipe','pipe','pipe']});
  let seq=0,buffer='';const pending=new Map();child.stdout.setEncoding('utf8');child.stdout.on('data',b=>{buffer+=b;let n;while((n=buffer.indexOf('\n'))>=0){const r=JSON.parse(buffer.slice(0,n));buffer=buffer.slice(n+1);const p=pending.get(r.id);pending.delete(r.id);r.error?p.reject(Error(r.error)):p.resolve(r.result);}});
  child.stderr.on('data',()=>{});
  const uploaded=[];let interrupt=true;
  f.app.remote={request(method,params={}){return new Promise((resolve,reject)=>{const id=++seq;pending.set(id,{resolve,reject});child.stdin.write(JSON.stringify({id,method,params})+'\n');});},async uploadChunk(local,directory,index){uploaded.push(index);if(index===1&&interrupt){interrupt=false;throw Error('Simulated disconnected SFTP');}fs.copyFileSync(local,path.join(directory,'chunk-'+String(index).padStart(6,'0')+'.part'));}};
  const file=path.join(f.root,'large local video.mp4');const fd=fs.openSync(file,'w');try{fs.writeSync(fd,Buffer.alloc(64*1024*1024,7));fs.writeSync(fd,Buffer.from('last chunk'));}finally{fs.closeSync(fd);}
  const task=f.inbox.queue(file);
  await assert.rejects(f.inbox.transfer(task),/disconnected/);
  assert.equal(fs.readdirSync(path.join(f.app.root,'upload-chunks')).length,0);
  await new Promise(r=>setTimeout(r,100));
  await assert.rejects(f.inbox.transfer(task),/failure after receipt persistence/);
  assert.deepEqual(uploaded,[0,1,1]);
  await new Promise(r=>setTimeout(r,100));
  await f.inbox.transfer(task); // Resume the same failed finalize from its durable stored receipt.
  assert.deepEqual(uploaded,[0,1,1]);assert.equal(task.storage_status,'stored');assert.equal(task.delivery_status,undefined);
  assert.equal(task.archive_receipt.sha256,createHash('sha256').update(fs.readFileSync(file)).digest('hex'));
  for(let i=0;i<10;i++){const j=await f.app.remote.request('get_job',{job_id:task.index_job_id});if(j.status==='completed')break;if(['failed','needs_review'].includes(j.status))throw Error(JSON.stringify(await f.app.remote.request('get_results',{job_id:j.job_id})));}
  f.app.capabilities={inbox_protocol:1,archive_configured:true};await f.inbox.refresh();assert.equal(task.indexing_status,'searchable');
  await assert.rejects(f.inbox.assign(task.request_id,'invented-project'),/Unknown/);
  await f.inbox.assign(task.request_id,'project-1');assert.deepEqual(task.project_ids,['project-1']);
  const stored=fs.readFileSync(task.archive_receipt.absolute_path);assert.deepEqual(stored,fs.readFileSync(file));
 }finally{if(child){child.stdin.end();child.kill();await new Promise(r=>setTimeout(r,100));}f.close();}
});

test('retry indexing resumes a failed finalize receipt under its existing identity',async()=>{const f=fixture();try{const calls=[];f.inbox.receipt=()=>({request_id:'a'.repeat(32)});f.app.remote={async request(method,p){calls.push({method,p});if(method==='finalize_upload')return {job_id:'existing-finalize',status:'failed'};if(method==='resume_job')return {job_id:p.job_id,status:'queued'};throw Error('Unexpected '+method);}};f.inbox.waitJob=async job=>{assert.equal(job.job_id,'existing-finalize');assert.equal(job.status,'queued');};f.inbox.refresh=async()=>{};await f.inbox.retry('receipt');assert.deepEqual(calls.map(c=>c.method),['finalize_upload','resume_job']);assert.equal(calls[1].p.job_id,'existing-finalize');}finally{f.close();}});
