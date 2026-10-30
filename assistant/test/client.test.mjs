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
