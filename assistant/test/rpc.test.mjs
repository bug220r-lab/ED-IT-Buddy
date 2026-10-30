import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import {spawn,execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {fileURLToPath} from 'node:url';
import {createHash} from 'node:crypto';
import {Assistant,validateJob} from '../client.mjs';
import {Remote} from '../remote.mjs';

// These tests run the real Python JSONL bridge against disposable SQLite state.
// The injected spawn function never launches SSH, SCP, Codex, FFmpeg, or a model.
const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const python=process.env.EDIT_BUDDY_TEST_PYTHON||path.join(os.homedir(),'.cache','codex-runtimes','codex-primary-runtime','dependencies','python','python.exe');
const available=fs.existsSync(python);
const run=promisify(execFile);
const remoteBase='C:\\Users\\jlewi\\AppData\\Local\\ED-IT-Buddy-Media';
const config={ssh:{host:'jlewi@192.168.247.1',identityFile:'fixture-key',knownHostsFile:'fixture-hosts',hostKeyAlias:'msi'},remote:{python:remoteBase+'\\runtime\\python.exe',rpc:remoteBase+'\\v0.3.0\\media_worker\\rpc.py',state:remoteBase+'\\state',config:remoteBase+'\\config.json'}};
const mutateScript=String.raw`
import json,sys
from pathlib import Path
sys.path.insert(0,sys.argv[1])
from media_worker.core import Engine,canonical
e=Engine(sys.argv[2],json.loads(Path(sys.argv[3]).read_text('utf-8')))
op=sys.argv[4]; p=json.loads(sys.argv[5])
if op=='heartbeat':
 e.heartbeat()
elif op=='progress':
 e.progress(p['job_id'],**p['progress'])
 with e.connect() as c: c.execute("UPDATE jobs SET status='running' WHERE job_id=?",(p['job_id'],))
elif op=='fail':
 with e.connect() as c: c.execute("UPDATE jobs SET status='failed',error=? WHERE job_id=?",(canonical({'code':'fixture','message':'Synthetic worker failure'}),p['job_id']))
elif op=='complete':
 job=e.get_job({'job_id':p['job_id']})
 result=p.get('result',{})
 if p.get('artifact'):
  from media_worker.jobs import artifact
  output=e.job_dir(job['job_id'])/'preview.mp4'
  output.write_bytes(b'Explicitly synthetic protocol fixture; not a decoded video.')
  result={'assets':[artifact(e,job,output,p.get('kind','preview'),source_asset_id=p.get('source_asset_id','asset-source'),start_seconds=0,end_seconds=5)],'results':[]}
 e.complete(job,result)
else: raise RuntimeError('Unknown test-only mutation')
`;

async function fixture(){
 const fixtureParent=path.join(os.tmpdir(),'buddy-rpc-fixtures');fs.mkdirSync(fixtureParent,{recursive:true});
 const root=fs.mkdtempSync(path.join(fixtureParent,'rpc-'));
 const source=path.join(root,'Footage café');fs.mkdirSync(source);fs.writeFileSync(path.join(source,'wide-stage.mp4'),'Synthetic source bytes remain untouched.');
 const state=path.join(root,'remote-state'),configFile=path.join(root,'worker.json');
 fs.writeFileSync(configFile,JSON.stringify({expected_host:'',source_allowlist:[source],model_revision:'synthetic-contract-only',downloader_root:path.join(root,'not-installed')}));
 const children=[],spawns=[];
 const remote=new Remote(config,{spawnProcess(executable,args,options){
  assert.match(path.basename(executable),/^ssh(?:\.exe)?$/i,'Only the fixed SSH spawn is replaced');
  spawns.push({executable,args});
  const child=spawn(python,['-B','-u',path.join(repo,'media_worker','rpc.py'),'--state',state,'--config',configFile],{...options,env:{...process.env,PYTHONDONTWRITEBYTECODE:'1'}});children.push(child);return child;
 }});
 // Artifact transport is replaced with a hash-verified local copy of fixture data.
 remote.copyArtifact=async(asset,destination)=>{
  const sourcePath=path.resolve(asset.absolute_path||asset.path);const relative=path.relative(state,sourcePath);
  assert.ok(relative&&!relative.startsWith('..')&&!path.isAbsolute(relative),'artifact belongs to disposable worker state');
  const bytes=fs.readFileSync(sourcePath);assert.equal(createHash('sha256').update(bytes).digest('hex'),asset.sha256);
  if(asset.bytes!==undefined)assert.equal(bytes.length,asset.bytes);
  fs.mkdirSync(destination,{recursive:true});const target=path.join(destination,asset.sha256+path.extname(sourcePath));if(!fs.existsSync(target))fs.writeFileSync(target,bytes,{flag:'wx'});return target;
 };
 const published=[],deliveries=new Map();let publisherOffline=false;
 const interlinked={
  async projects(){return {projects:[{original_id:'band-project',name:'Band project'}]};},
  async report(project_id,summary){if(publisherOffline)throw Error('Synthetic INTERLINKED outage');published.push({project_id,summary});return {status:'published'};},
  async ingest(request){const receipt={status:'COMPLETED',request_id:request.request_id,project_id:request.project_id,files:[{project_relative_path:'BuddyAssets/fixture/preview.mp4'}]};deliveries.set(request.request_id,receipt);return receipt;},
  async operation(request_id){return deliveries.get(request_id)||{status:'NOT_FOUND',request_id};}
 };
 const chatFactory=()=>({async start(){return {signed_in:true};},async login(){return {authUrl:'https://auth.openai.com/fixture-only'};},close(){}});
 const app=new Assistant({root:path.join(root,'local-state'),localAppData:root,config,remote,interlinked,chatFactory});
 app.prepareStaging=async()=>{fs.mkdirSync(path.join(app.root,'staging'),{recursive:true});};
 const mutate=async(op,p={})=>{await run(python,['-B','-c',mutateScript,repo,state,configFile,op,JSON.stringify(p)],{env:{...process.env,PYTHONDONTWRITEBYTECODE:'1'},windowsHide:true});};
 await remote.request('get_capabilities');await mutate('heartbeat');
 const registered=await remote.request('register_source_root',{path:source,label:'Stage 🎸'});
 await app.reconnect();clearInterval(app.timer);app.connected=false;await app.handle('select_project',{project_id:'band-project'});
 async function close(){
  app.close();
  await Promise.all(children.map(child=>new Promise(resolve=>{if(child.exitCode!==null)return resolve();const timer=setTimeout(()=>{child.kill();resolve();},1500);child.once('exit',()=>{clearTimeout(timer);resolve();});})));
  const relative=path.relative(fixtureParent,root);assert.ok(relative&&!relative.startsWith('..')&&!path.isAbsolute(relative));fs.rmSync(root,{recursive:true,force:true});
 }
 return {app,remote,root,source,registered,spawns,mutate,close,published,setPublisherOffline(v){publisherOffline=v;}};
}

test('real local RPC preserves Unicode roots, stable identity and scoped inventory',{skip:!available},async()=>{
 const f=await fixture();try{
  const snapshot=await f.app.handle('bootstrap');assert.equal(snapshot.sources[0].source_id,f.registered.root_id);assert.equal(snapshot.sources[0].path,f.source);assert.equal(snapshot.projects[0].project_id,'band-project');
  const inventory=await f.remote.request('inventory',{root_id:f.registered.root_id});assert.equal(inventory.files[0].relative_path,'wide-stage.mp4');
  await assert.rejects(f.remote.request('register_source_root',{path:repo}),/source folder|configured|scope|linked|offline|unhydrated/i);
  assert.equal(f.spawns.length,1);assert.ok(f.spawns[0].args.includes('StrictHostKeyChecking=yes'));
 }finally{await f.close();}
});

test('all-sources visual search reaches the real worker without an empty root filter',{skip:!available},async()=>{
 const f=await fixture();try{
  const t=await f.app.handle('search',{query:'crowd shot',source_ids:[]});await f.app.sync();
  assert.ok(t.remote_job_id,t.message);const job=await f.remote.request('get_job',{job_id:t.remote_job_id});assert.equal(job.params.query,'crowd shot');assert.ok(!Object.hasOwn(job.params,'root_ids'));
 }finally{await f.close();}
});

test('worker progress objects become usable UI progress observations',{skip:!available},async()=>{
 const f=await fixture();try{
  const t=await f.app.handle('submit',{kind:'index',params:{source_id:f.registered.root_id}});await f.app.sync();
  await f.mutate('progress',{job_id:t.remote_job_id,progress:{completed_items:1,total_items:4,stage:'sampling'}});await f.app.sync();
  assert.equal(typeof t.progress,'number');assert.equal(t.progress,.25);
 }finally{await f.close();}
});

test('observing a queued task does not hide its later completed output assets',{skip:!available},async()=>{
 const f=await fixture();try{
  const t=await f.app.handle('submit',{kind:'download',params:{urls:['https://example.com/individual-video']}});await f.app.sync();
  const waiting=await f.app.handle('results',{task_id:t.task_id});assert.deepEqual(waiting.results,[]);
  await f.mutate('complete',{job_id:t.remote_job_id,artifact:true,kind:'video'});await f.app.sync();
  const done=await f.app.handle('results',{task_id:t.task_id});assert.equal(t.status,'completed');assert.equal(done.results.length,1);assert.ok(done.results[0].asset_id);assert.equal(t.delivery_status,'COMPLETED');
  assert.equal(fs.readFileSync(path.join(f.source,'wide-stage.mp4'),'utf8'),'Synthetic source bytes remain untouched.');
 }finally{await f.close();}
});

test('resuming a failed worker job updates the existing local task rather than duplicating it',{skip:!available},async()=>{
 const f=await fixture();try{
  const t=await f.app.handle('search',{query:'wide stage view'});await f.app.sync();const id=t.remote_job_id;
  await f.mutate('fail',{job_id:id});await f.app.sync();assert.equal(t.status,'failed');
  await f.app.handle('task_action',{task_id:t.task_id,action:'resume'});assert.equal(t.remote_job_id,id);assert.equal(t.status,'queued');
  const jobs=await f.remote.request('list_jobs',{});assert.equal(jobs.jobs.length,1);
 }finally{await f.close();}
});

test('an offline queued preview becomes locally playable after its completion receipt',{skip:!available},async()=>{
 const f=await fixture();try{
  const t=await f.app.handle('preview',{asset_id:'asset-source',start_seconds:0,end_seconds:5});assert.equal(t.status,'queued');await f.app.sync();
  await f.mutate('complete',{job_id:t.remote_job_id,artifact:true,kind:'preview',source_asset_id:'asset-source'});await f.app.sync();
  const output=await f.app.handle('results',{task_id:t.task_id});assert.equal(output.results.length,1);assert.ok(fs.existsSync(output.results[0].preview_path),'UI receives a verified local preview, not an unregistered derived source ID');
 }finally{await f.close();}
});

test('report and review keep job completion distinct from project delivery and sign-in returns a URL',{skip:!available},async()=>{
 const f=await fixture();try{
  const t=await f.app.handle('search',{query:'drummer close-up'});await f.app.handle('task_action',{task_id:t.task_id,action:'mark_reviewed'});
  const report=await f.app.handle('report',{format:'json'});const summary=JSON.parse(report.content);assert.equal(summary.tasks[0].status,'queued');assert.equal(summary.tasks[0].delivery_status,'not_delivered');assert.ok(summary.tasks[0].reviewed_by_user_at);assert.equal(f.published[0].project_id,'band-project');
  assert.equal(report.publication.status,'published');
  f.setPublisherOffline(true);const localReport=await f.app.handle('report',{format:'markdown'});assert.equal(localReport.publication.status,'failed');assert.match(localReport.publication.message,/INTERLINKED outage/);assert.match(localReport.content,/queued/);
  const login=await f.app.handle('login_chat');assert.equal(login.authUrl,'https://auth.openai.com/fixture-only');
 }finally{await f.close();}
});

test('preview duration validation matches the Python worker bound',()=>{
 assert.throws(()=>validateJob('preview',{asset_id:'asset-source',start_seconds:0,end_seconds:61}),/preview|60|range|minute/i);
 assert.doesNotThrow(()=>validateJob('extract',{asset_id:'asset-source',start_seconds:0,end_seconds:120}));
});
