import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {EventEmitter} from 'node:events';
import {PassThrough} from 'node:stream';
import {CodexChat,CHAT_CONFIG,BUDDY_TOOL} from '../codex.mjs';

function fixture({signedIn=true,inheritedThreadTools=false,transientThreadTools=false,lateOnSecondTurn=false,turnStatus='completed'}={}){
 const root=fs.mkdtempSync(path.join(os.tmpdir(),'buddy-codex-contract-'));const calls=[],spawnArgs=[],tools=[];
 let child,turn=0,accountSignedIn=signedIn;
 const emit=(value,target=child)=>queueMicrotask(()=>target.stdout.write(JSON.stringify(value)+'\n'));
 const spawnProcess=(executable,args)=>{
  spawnArgs.push({executable,args});const generation=spawnArgs.length;child=new EventEmitter();const current=child;child.stdout=new PassThrough();child.stderr=new PassThrough();child.killed=false;child.exitCode=null;
  child.kill=()=>{current.killed=true;current.exitCode=0;queueMicrotask(()=>current.emit('exit',0));};child.stdin={end(){},write(line){const m=JSON.parse(line);calls.push(m);if(!m.id)return;const reply=result=>emit({id:m.id,result},current);
   if(m.method==='initialize')reply({userAgent:'synthetic'});
   else if(m.method==='account/read')reply({account:accountSignedIn?{type:'chatgpt'}:null,requiresOpenaiAuth:true});
   else if(m.method==='mcpServerStatus/list')reply({data:m.params.threadId?(inheritedThreadTools||transientThreadTools&&generation===1||lateOnSecondTurn&&generation===1&&turn>0?[{name:'unexpected',tools:{unsafe:{}}}]:[]):[{name:'node_repl',tools:{js:{}}}],nextCursor:null});
   else if(m.method==='thread/start'||m.method==='thread/resume')reply({thread:{id:'thread-synthetic'}});
   else if(m.method==='turn/start'){const id='turn-'+(++turn);reply({turn:{id,status:'inProgress'}});emit({method:'item/agentMessage/delta',params:{threadId:m.params.threadId,delta:'Synthetic answer'}});emit({method:'turn/completed',params:{threadId:m.params.threadId,turn:{id,status:turnStatus,error:null}}});}
   else if(m.method==='account/login/start')reply({authUrl:'https://auth.openai.com/synthetic',loginId:'fixture'});
   else if(m.method==='turn/interrupt')reply({});
   else emit({id:m.id,error:{message:'Unexpected fixture RPC '+m.method}});
  }};return child;
 };
 const chat=new CodexChat({cwd:path.join(root,'workspace'),threads:{},onThreads(){},onDelta(){},tool:async(...args)=>{tools.push(args);return {tasks:[]};},spawnProcess});
 return {chat,calls,spawnArgs,tools,get child(){return child;},setSignedIn(v){accountSignedIn=v;},async notify(value){await chat.message(value);},close(){chat.close();const relative=path.relative(os.tmpdir(),root);assert.ok(relative&&!relative.startsWith('..')&&!path.isAbsolute(relative)&&path.basename(root).startsWith('buddy-codex-contract-'));fs.rmSync(root,{recursive:true,force:true});}};
}

test('Buddy enables its isolated dynamic-tool runtime while disabling broad capabilities',async()=>{
 const f=fixture();try{
  await f.chat.send('project-1','Show the synthetic task status.');
  assert.equal(CHAT_CONFIG['features.code_mode_host'],true);assert.equal(CHAT_CONFIG['features.shell_tool'],false);assert.equal(CHAT_CONFIG['features.computer_use'],false);assert.equal(CHAT_CONFIG['features.plugins'],false);
  assert.equal(BUDDY_TOOL.deferLoading,false);
  const start=f.calls.find(x=>x.method==='thread/start');assert.equal(start.params.config['mcp_servers.node_repl.enabled'],false);assert.deepEqual(start.params.dynamicTools,[BUDDY_TOOL]);
  assert.equal(f.calls.some(x=>x.method==='mcpServerStatus/list'&&x.params.threadId==='thread-synthetic'),true);
 }finally{f.close();}
});

test('a thread with inherited callable tools is refused before inference',async()=>{
 const f=fixture({inheritedThreadTools:true});try{
  await assert.rejects(f.chat.send('project-1','Do the requested work.'),/inherited connector tools/);
  assert.equal(f.calls.some(x=>x.method==='turn/start'),false);assert.equal(f.tools.length,0);assert.equal(f.spawnArgs.length,2,'re-isolation is bounded to one restart');
 }finally{f.close();}
});

test('a late startup catalog is explicitly disabled and reverified after one restart',async()=>{
 const f=fixture({transientThreadTools:true});try{
  const reply=await f.chat.send('project-1','Read synthetic task status without changing work.');
  assert.equal(reply.status,'completed');assert.equal(f.spawnArgs.length,2);assert.equal(f.calls.filter(x=>x.method==='turn/start').length,1);
  const resumed=f.calls.find(x=>x.method==='thread/resume');assert.equal(resumed.params.config['mcp_servers.unexpected.enabled'],false);
  const diagnostic=JSON.parse(fs.readFileSync(path.join(f.chat.cwd,'tool-isolation-diagnostic.json'),'utf8'));assert.equal(diagnostic.status,'restarted_and_verified_empty');assert.equal(diagnostic.attempt,1);assert.ok(diagnostic.disabled_servers.includes('unexpected'));
 }finally{f.close();}
});

test('a previously verified loaded thread is rechecked before its next inference',async()=>{
 const f=fixture({lateOnSecondTurn:true});try{
  await f.chat.send('project-1','First synthetic turn.');await f.chat.send('project-1','Second synthetic turn.');
  assert.equal(f.spawnArgs.length,2);assert.equal(f.calls.filter(x=>x.method==='turn/start').length,2);assert.equal(f.calls.filter(x=>x.method==='thread/resume').length,1);
 }finally{f.close();}
});

test('a loaded project conversation is reused without ignored resume overrides',async()=>{
 const f=fixture();try{
  await f.chat.send('project-1','First synthetic turn.');await f.chat.send('project-1','Second synthetic turn.');
  assert.equal(f.calls.filter(x=>x.method==='thread/start').length,1);assert.equal(f.calls.filter(x=>x.method==='thread/resume').length,0);assert.equal(f.calls.filter(x=>x.method==='turn/start').length,2);
 }finally{f.close();}
});

test('supported account notifications refresh auth without reading credential storage',async()=>{
 const f=fixture({signedIn:false});try{
  assert.equal((await f.chat.start()).signed_in,false);f.setSignedIn(true);
  await f.notify({method:'account/updated',params:{authMode:'chatgpt'}});assert.equal((await f.chat.start()).signed_in,true);
  assert.equal((await f.chat.login()).signed_in,true);assert.equal(f.calls.some(x=>x.method==='account/login/start'),false);
  f.setSignedIn(false);await f.notify({method:'account/login/completed',params:{success:true}});assert.equal((await f.chat.start()).signed_in,false);
 }finally{f.close();}
});

test('failed turns are actionable errors rather than empty successful replies',async()=>{
 const f=fixture({turnStatus:'failed'});try{
  await assert.rejects(f.chat.send('project-1','Synthetic failure.'),/Codex turn failed/);
 }finally{f.close();}
});

test('named result/control/preview/ingest operations stay scoped to the source project',async()=>{
 const f=fixture();try{
  await f.chat.send('project-1','Set up a synthetic conversation.');
  for(const operation of ['results','control','preview','ingest'])await f.chat.message({id:'tool-'+operation,method:'item/tool/call',params:{threadId:'thread-synthetic',tool:'buddy_work',arguments:{operation,parameters:{task_id:'synthetic'}}}});
  assert.deepEqual(f.tools.map(x=>x[0]),['results','control','preview','ingest']);assert.ok(f.tools.every(x=>x[2]==='project-1'));
  assert.ok(BUDDY_TOOL.inputSchema.properties.operation.enum.includes('results'));
 }finally{f.close();}
});
