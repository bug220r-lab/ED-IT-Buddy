import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {Assistant,validateJob} from '../client.mjs';
import {TaskStore} from '../state.mjs';
import {Remote} from '../remote.mjs';
import {CodexChat,CHAT_CONFIG} from '../codex.mjs';
import {EventEmitter} from 'node:events';
import {PassThrough} from 'node:stream';
import {createHash} from 'node:crypto';

function fixture(){
 const root=fs.mkdtempSync(path.join(os.tmpdir(),'buddy-client-'));const calls=[];let offline=false,dropAck=false;const jobs=new Map();
 const remote={close(){},async request(method,p={}){calls.push({method,p:structuredClone(p)});if(offline)throw Error('MSI offline');
  if(method==='get_capabilities')return {worker_online:true};if(method==='list_source_roots')return {roots:[{root_id:'r1',path:'C:\\Footage'}]};
  if(method==='submit_job'){let j=jobs.get(p.request_id);if(!j){j={job_id:p.request_id,status:'running',progress:{stage:'testing'}};jobs.set(p.request_id,j);}if(dropAck){dropAck=false;throw Error('Acknowledgement lost');}return j;}
  if(method==='get_job')return jobs.get(p.job_id);if(method.endsWith('_job'))return {status:'paused'};throw Error('Unexpected '+method);
 }};
 const interlinked={async projects(){return {projects:[{original_id:'project-1',name:'Band project'}]};},async report(){return {status:'ok'};}};
 const chatFactory=()=>({async start(){return {signed_in:true};},close(){}});
 const app=new Assistant({root,localAppData:root,config:{},remote,interlinked,chatFactory});
 return {app,root,calls,jobs,setOffline(v){offline=v;},dropAck(){dropAck=true;}};
}
test('offline tasks persist without submitting work or changing sources',async()=>{const f=fixture();try{
 await f.app.reconnect();await f.app.handle('select_project',{project_id:'project-1'});f.setOffline(true);
 const t=await f.app.handle('submit',{kind:'download',params:{urls:['https://example.com/video']}});await f.app.sync();
 assert.equal(t.status,'queued');assert.match(t.message,/offline/);const id=t.request_id;assert.match(id,/^[a-f0-9]{32}$/);
 f.app.close();const again=new TaskStore(f.root);assert.equal(again.data.tasks[0].request_id,id);assert.equal(again.data.tasks[0].status,'queued');again.close();
}finally{f.app.close();fs.rmSync(f.root,{recursive:true,force:true});}});
test('lost submit acknowledgement reconciles using the same stable request',async()=>{const f=fixture();try{
 await f.app.reconnect();await f.app.handle('select_project',{project_id:'project-1'});f.dropAck();
 const t=await f.app.queue('download',{urls:['https://example.com/video']});await f.app.sync();await f.app.sync();
 assert.equal(f.jobs.size,1);assert.equal(t.remote_job_id,t.request_id);
 const submits=f.calls.filter(c=>c.method==='submit_job');assert.ok(submits.length>=2);assert.equal(new Set(submits.map(c=>c.p.request_id)).size,1);
}finally{f.app.close();fs.rmSync(f.root,{recursive:true,force:true});}});
test('project identity must come from INTERLINKED, never a display label',async()=>{const f=fixture();try{await f.app.reconnect();await assert.rejects(f.app.handle('select_project',{project_id:'Band project'}),/known/);await f.app.handle('select_project',{project_id:'project-1'});assert.equal(f.app.store.data.selected_project_id,'project-1');}finally{f.app.close();fs.rmSync(f.root,{recursive:true,force:true});}});
test('review attribution never turns an unexecuted task into completion',async()=>{const f=fixture();try{f.app.store.data.selected_project_id='project-1';const t=await f.app.queue('download',{urls:['https://example.com/video']});await f.app.handle('task_action',{task_id:t.task_id,action:'mark_reviewed'});assert.equal(t.status,'queued');assert.ok(t.reviewed_by_user_at);const r=await f.app.report();assert.match(r.content,/queued/);assert.equal(r.summary.tasks[0].delivery_status,'not_delivered');}finally{f.app.close();fs.rmSync(f.root,{recursive:true,force:true});}});
test('unknown parameters, arbitrary commands, credentials and invalid clips are refused',()=>{
 assert.throws(()=>validateJob('shell',{command:'whoami'}));assert.throws(()=>validateJob('download',{urls:['file:///C:/private']}));assert.throws(()=>validateJob('download',{urls:['https://user:secret@example.com/v']}));assert.throws(()=>validateJob('download',{urls:['https://example.com/v'],command:'echo'}));assert.throws(()=>validateJob('extract',{asset_id:'a',start_seconds:4,end_seconds:3}));assert.throws(()=>validateJob('organize',{asset_ids:['a'],group_by:'delete'}));
});
test('RPC transport refuses arbitrary operations before opening SSH',async()=>{let spawns=0;const remote=new Remote({},{spawnProcess:()=>{spawns++;}});await assert.rejects(remote.request('run_shell',{command:'echo'}));assert.equal(spawns,0);});
test('unrecognized chat tools cannot reach the task dispatcher',async()=>{
 let calls=0;const responses=[];const chat=new CodexChat({threads:{p:'thread-1'},tool:async()=>{calls++;}});chat.child={stdin:{write:s=>responses.push(JSON.parse(s))}};
 await chat.message({id:1,method:'item/tool/call',params:{threadId:'thread-1',tool:'shell',arguments:{operation:'tasks',parameters:{}}}});
 await chat.message({id:2,method:'item/tool/call',params:{threadId:'thread-1',tool:'buddy_work',arguments:{operation:'tasks',parameters:{},command:'whoami'}}});
 assert.equal(calls,0);assert.equal(responses[0].result.success,false);assert.equal(responses[1].result.success,false);
 assert.equal(CHAT_CONFIG['features.shell_tool'],false);assert.equal(CHAT_CONFIG['features.apps'],false);
});
test('chat tool dispatch is scoped to its original project conversation',async()=>{let received;const chat=new CodexChat({threads:{'project-1':'thread-1'},tool:async(...a)=>{received=a;return {tasks:[]};}});chat.child={stdin:{write(){}}};await chat.message({id:1,method:'item/tool/call',params:{threadId:'thread-1',tool:'buddy_work',arguments:{operation:'tasks',parameters:{}}}});assert.equal(received[2],'project-1');});
test('a stale SSH child exit cannot kill the replacement connection',async()=>{
 const config=JSON.parse(fs.readFileSync(new URL('../config.example.json',import.meta.url),'utf8'));const children=[];
 const remote=new Remote(config,{spawnProcess:()=>{const c=new EventEmitter();c.stdin=new PassThrough();c.stdout=new PassThrough();c.stderr=new PassThrough();c.kill=()=>{c.killed=true;};children.push(c);return c;}});
 const first=remote.request('get_capabilities');remote.fail(Error('connection lost'),children[0]);await assert.rejects(first,/lost/);
 const second=remote.request('get_capabilities');const sent=JSON.parse(children[1].stdin.read().toString().trim());children[0].emit('exit',1);
 assert.equal(remote.child,children[1]);assert.equal(children[1].killed,undefined);children[1].stdout.write(JSON.stringify({id:sent.id,result:{worker_online:true},error:null})+'\n');assert.equal((await second).worker_online,true);remote.close();
});
test('a stale Codex child cannot invalidate a new conversation transport',()=>{const chat=new CodexChat({threads:{}});const old={kill(){throw Error('must not kill');}};const replacement={kill(){throw Error('must not kill');}};chat.child=replacement;chat.fail(Error('old exit'),old);assert.equal(chat.child,replacement);});
test('a corrupt ledger is preserved and its own startup lock is released',()=>{const root=fs.mkdtempSync(path.join(os.tmpdir(),'buddy-corrupt-'));try{fs.writeFileSync(path.join(root,'tasks.json'),'{corrupt');assert.throws(()=>new TaskStore(root));assert.equal(fs.readFileSync(path.join(root,'tasks.json'),'utf8'),'{corrupt');assert.equal(fs.existsSync(path.join(root,'client.lock')),false);}finally{fs.rmSync(root,{recursive:true,force:true});}});
test('existing library indexing stays visible without publishing it as an unknown project',async()=>{const f=fixture();try{
 const prior=f.app.remote.request;const reports=[];
 const library={job_id:'index-existing',request_id:'library-request',kind:'index',project_id:'unregistered-library-test',params:{root_ids:['r1']},status:'running',created:Date.now()/1000,progress:{indexed_files:1,total_files:5}};
 f.app.remote.request=async(method,p)=>method==='list_jobs'?{jobs:[library]}:method==='get_job'&&p.job_id===library.job_id?library:prior(method,p);
 f.app.interlinked.report=async(project)=>{reports.push(project);if(project!=='project-1')throw Error('Unknown project');};
 await f.app.reconnect();await f.app.handle('select_project',{project_id:'project-1'});f.app.connected=false;
 assert.equal(f.app.tasks()[0].scope,'library');assert.equal(f.app.summary('project-1').tasks.length,0);assert.equal(f.app.summary('project-1').library_work.length,1);
 await f.app.queue('search',{query:'guitars'});f.app.lastReportAttempt=0;f.app.publishReports();await new Promise(r=>setImmediate(r));
 assert.deepEqual(reports,['project-1']);assert.equal(f.app.status.interlinked,'Connected');
 await f.app.discoverLibraryJobs();assert.equal(f.app.store.data.tasks.filter(t=>t.remote_job_id===library.job_id).length,1);
}finally{f.app.close();fs.rmSync(f.root,{recursive:true,force:true});}});

for(const example of [
 {name:'completed index',status:'completed',results:[{},{}],skipped:[{reason:'magix_proxy_original_indexed'}],failures:[],message:'Indexed 2 files; skipped 1 MAGIX proxy; 0 need review.'},
 {name:'index with genuine failures',status:'needs_review',results:Array(109).fill({}),skipped:Array(11).fill({reason:'magix_proxy_original_indexed'}),failures:[{},{}],message:'Indexed 109 files; skipped 11 MAGIX proxies; 2 need review.'},
 {name:'legacy index without skips',status:'completed',results:[{}],failures:[],message:'Indexed 1 file; skipped 0 MAGIX proxies; 0 need review.'}
])test(example.name+' keeps its distinct outcome counts across repeated polls',async()=>{const f=fixture();try{
 f.app.store.data.selected_project_id='project-1';const task=await f.app.queue('index',{root_ids:['r1']});task.remote_job_id=task.request_id;task.status='running';
 const job={job_id:task.request_id,status:example.status,progress:{stage:'last decoded file'}};f.jobs.set(task.request_id,job);
 const original=f.app.remote.request;let fetches=0;
 f.app.remote.request=async(method,p)=>{if(method==='get_results'){fetches++;return {manifest:{kind:'index',results:example.results,...(example.skipped?{skipped:example.skipped}:{}),failures:example.failures}};}return original(method,p);};
 await f.app.sync();assert.equal(task.message,example.message);assert.equal(task.status,example.status);
 job.progress.stage='a later stale worker stage';await f.app.sync();assert.equal(task.message,example.message);assert.equal(fetches,1);
 assert.equal(f.app.summary('project-1').tasks[0].message,example.message);
 assert.equal(f.calls.some(call=>call.method==='submit_job'),false);
}finally{f.app.close();fs.rmSync(f.root,{recursive:true,force:true});}});
test('index outcome summaries do not replace messages for other job kinds',async()=>{const f=fixture();try{
 f.app.store.data.selected_project_id='project-1';const task=await f.app.queue('search',{query:'drummer'});task.remote_job_id=task.request_id;task.status='running';
 f.jobs.set(task.request_id,{job_id:task.request_id,status:'completed',message:'Worker search completed',progress:{}});
 const original=f.app.remote.request;f.app.remote.request=async(method,p)=>method==='get_results'?{manifest:{results:[{}],skipped:[{reason:'magix_proxy_original_indexed'}],failures:[]}}:original(method,p);
 await f.app.sync();await f.app.sync();assert.equal(task.message,'Worker search completed');assert.equal(task.kind,'search');
}finally{f.app.close();fs.rmSync(f.root,{recursive:true,force:true});}});

function deliveryFixture(){
 const f=fixture();const source=path.join(f.root,'verified-source.mp4');const bytes=Buffer.from('verified synthetic delivery');fs.writeFileSync(source,bytes);const sha256=createHash('sha256').update(bytes).digest('hex');
 const asset={asset_id:'asset-'+sha256,absolute_path:source,relative_path:'clips/show.mp4',bytes:bytes.length,sha256,kind:'video'};
 const original=f.app.remote.request;const receipts=new Map();let online=true,loseAck=false,responseStatus='COMPLETED',submissions=0,observations=0;
 f.app.remote.request=async(method,p)=>{
  if(method==='list_jobs')return {jobs:[]};
  if(method==='get_results')return {manifest:{format:'buddy-media-manifest-v1',assets:[asset],results:[]}};
  const result=await original(method,p);if(method==='submit_job')result.status='completed';return result;
 };
 f.app.remote.copyArtifact=async()=>source;f.app.prepareStaging=async()=>{};
 f.app.interlinked.operation=async(id)=>{observations++;if(!online)throw Error('INTERLINKED offline');return receipts.get(id)||{request_id:id,status:'NOT_FOUND'};};
 f.app.interlinked.ingest=async(request)=>{submissions++;if(!online)throw Error('INTERLINKED offline');const receipt={request_id:request.request_id,project_id:request.project_id,status:responseStatus,files:[{path:'BuddyAssets/'+request.request_id+'/clips/show.mp4',sha256}]};receipts.set(request.request_id,receipt);if(loseAck){loseAck=false;throw Error('Ingest acknowledgement lost');}return receipt;};
 return {...f,source,receipts,get submissions(){return submissions;},get observations(){return observations;},setInterlinkedOnline(v){online=v;},loseIngestAck(){loseAck=true;},setReceiptStatus(v){responseStatus=v;},async task(){f.app.store.data.selected_project_id='project-1';return f.app.queue('download',{urls:['https://example.com/video']});}};
}
test('prepared delivery recovers after INTERLINKED reconnect without fetching its manifest again',async()=>{const f=deliveryFixture();try{
 const t=await f.task();f.setInterlinkedOnline(false);await f.app.sync();assert.ok(t.results.manifest);assert.equal(t.delivery_status,'needs_review');assert.equal(f.submissions,0);
 f.setInterlinkedOnline(true);await f.app.reconnect();assert.equal(t.delivery_status,'COMPLETED');assert.equal(f.submissions,1);await f.app.sync();assert.equal(f.submissions,1);
 assert.equal(fs.readFileSync(f.source,'utf8'),'verified synthetic delivery');
}finally{f.app.close();fs.rmSync(f.root,{recursive:true,force:true});}});
test('lost ingest acknowledgement is reconciled from the stable receipt without a second ingestion',async()=>{const f=deliveryFixture();try{
 const t=await f.task();f.loseIngestAck();await f.app.sync();assert.equal(t.delivery_status,'needs_review');assert.equal(f.submissions,1);assert.equal(f.receipts.get(t.request_id).status,'COMPLETED');
 await f.app.reconnect();assert.equal(t.delivery_status,'COMPLETED');assert.equal(f.submissions,1);assert.equal(t.delivery_receipt.request_id,t.request_id);
}finally{f.app.close();fs.rmSync(f.root,{recursive:true,force:true});}});
test('an accepted delivery receipt progresses even after MSI disconnects',async()=>{const f=deliveryFixture();try{
 const t=await f.task();f.setReceiptStatus('QUEUED');await f.app.sync();assert.equal(t.delivery_status,'QUEUED');assert.equal(f.submissions,1);
 f.receipts.set(t.request_id,{request_id:t.request_id,project_id:t.project_id,status:'COMPLETED',files:[]});f.setOffline(true);await f.app.sync();assert.equal(t.delivery_status,'COMPLETED');assert.equal(f.submissions,1);
}finally{f.app.close();fs.rmSync(f.root,{recursive:true,force:true});}});
test('failed uncertain and conflicting delivery receipts remain reviewable and are never resubmitted',async()=>{
 for(const status of ['FAILED','UNCERTAIN','CONFLICT']){const f=deliveryFixture();try{const t=await f.task();f.receipts.set(t.request_id,{request_id:t.request_id,project_id:t.project_id,status,error:'Retained transaction requires review'});await f.app.sync();assert.equal(t.delivery_status,'needs_review');assert.equal(f.submissions,0);f.receipts.delete(t.request_id);await f.app.sync();assert.equal(t.delivery_status,'needs_review');assert.equal(f.submissions,0);}finally{f.app.close();fs.rmSync(f.root,{recursive:true,force:true});}}
});
