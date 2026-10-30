import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import {spawn} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {createHash,randomUUID} from 'node:crypto';
import {ordinary,contained} from './state.mjs';

export const MEDIA=new Set(['.mp4','.mov','.mkv','.avi','.wmv','.m4v','.webm','.mpg','.mpeg','.mts','.m2ts']);
const CHUNK=64*1024*1024;
export function identity(file){ordinary(file);const s=fs.statSync(file);if(!s.size||!MEDIA.has(path.extname(file).toLowerCase()))throw Error('Choose an available video file.');return {path:path.resolve(file),bytes:s.size,mtime_ms:s.mtimeMs,file_id:String(s.dev)+':'+String(s.ino)};}
const fingerprint=s=>JSON.stringify(s);
export function enumerate(folder,recursive=false){ordinary(folder,{directory:true});const rows=[];for(const entry of fs.readdirSync(folder,{withFileTypes:true})){const p=path.join(folder,entry.name);if(entry.isSymbolicLink())continue;if(entry.isDirectory()&&recursive){try{rows.push(...enumerate(p,true));}catch{}}else if(entry.isFile()&&MEDIA.has(path.extname(p).toLowerCase())){try{rows.push(identity(p));}catch{}}}return rows;}

export class SourceReader {
 constructor(file,chunk){
  this.child=spawn('powershell.exe',['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',fileURLToPath(new URL('./source-reader.ps1',import.meta.url)),'-Source',file,'-Chunk',chunk],{windowsHide:true,stdio:['pipe','pipe','pipe']});
  this.waiters=[];this.lines=[];let buffer='';this.child.stdout.setEncoding('utf8');this.child.stdout.on('data',b=>{buffer+=b;let i;while((i=buffer.indexOf('\n'))>=0){const line=buffer.slice(0,i).trim();buffer=buffer.slice(i+1);if(line){try{this.deliver(JSON.parse(line));}catch{this.deliver({error:'Invalid source reader response.'});}}}});
  this.child.stderr.on('data',()=>{});this.child.on('error',e=>this.deliver({error:e.message}));this.child.on('exit',()=>{this.ended=true;this.deliver({error:'Source reader closed; retry after the writer closes.'});});
 }
 deliver(v){const w=this.waiters.shift();if(w)v.error?w.reject(Error(v.error)):w.resolve(v);else this.lines.push(v);}
 next(){if(this.lines.length){const v=this.lines.shift();return v.error?Promise.reject(Error(v.error)):Promise.resolve(v);}if(this.ended)return Promise.reject(Error('Source reader closed.'));return new Promise((resolve,reject)=>this.waiters.push({resolve,reject}));}
 read(offset,length){this.child.stdin.write(JSON.stringify({offset,length})+'\n');return this.next();}
 close(){this.child.stdin.end();if(!this.ended)this.child.kill();}
}

export class Inbox {
 constructor(app,{now=()=>Date.now(),reader=(...a)=>new SourceReader(...a)}={}){this.app=app;this.now=now;this.reader=reader;this.busy=false;this.closed=false;this.watchers=new Map();this.lastScan=0;this.store=app.store;this.store.data.watches??=[];this.store.data.inbox??=[];this.timer=setInterval(()=>this.tick().catch(()=>{}),10000);this.timer.unref();this.restore();}
 restore(){for(const w of this.store.data.watches)this.listen(w);this.scan();}
 listen(w){this.watchers.get(w.id)?.close();this.watchers.delete(w.id);if(w.paused)return;try{const watcher=fs.watch(w.path,{recursive:w.recursive},()=>{this.lastScan=0;});watcher.on('error',()=>{this.lastScan=0;});this.watchers.set(w.id,watcher);}catch(e){w.error=e.message;}}
 addWatch(folder,recursive=false){folder=path.resolve(folder);if(this.store.data.watches.some(w=>w.path.toLowerCase()===folder.toLowerCase()))throw Error('Folder is already watched.');const baseline=Object.fromEntries(enumerate(folder,recursive).map(s=>[s.path,fingerprint(s)]));const w={id:randomUUID(),path:folder,recursive:!!recursive,paused:false,baseline};this.store.data.watches.push(w);this.store.save();this.listen(w);return w;}
 watchAction(id,action){const w=this.store.data.watches.find(w=>w.id===id);if(!w)throw Error('Unknown watch.');if(action==='remove'){this.watchers.get(id)?.close();this.watchers.delete(id);this.store.data.watches=this.store.data.watches.filter(x=>x!==w);}else if(['pause','resume'].includes(action)){w.paused=action==='pause';this.listen(w);}else throw Error('Unknown watch action.');this.store.save();}
 queue(file,project=null){const source=identity(file);const key=fingerprint(source);let t=this.store.data.tasks.find(t=>t.kind==='local_import'&&t.source_key===key);if(t)return t;t=this.store.add('local_import',{},null);this.store.update(t,{scope:'library',source,source_key:key,name:path.basename(file),project_ids:[],pending_project_ids:project?[project]:[],storage_status:'waiting',indexing_status:'not_queued',observations:0,stable_since:this.now(),message:'Waiting for completed video'});return t;}
 scan(){for(const w of this.store.data.watches){if(w.paused)continue;try{for(const s of enumerate(w.path,w.recursive)){const key=fingerprint(s);if(w.baseline[s.path]!==key){this.queue(s.path);w.baseline[s.path]=key;}}w.error=null;}catch(e){w.error=e.message;}}this.store.save();this.lastScan=this.now();}
 async tick(){if(this.closed||this.busy)return;this.busy=true;try{
  if(this.now()-this.lastScan>=60000)this.scan();
  if(!this.app.connected&&this.store.data.inbox_auto_connect&&this.now()>=(this.nextConnect||0)){try{this.app.capabilities=await this.app.remote.request('get_capabilities');this.ready=true;this.app.status.msi='Connected';await this.refresh();}catch(e){this.ready=false;this.app.status.msi=e.message;}this.nextConnect=this.now()+30000;}
  for(const t of this.store.data.tasks.filter(t=>t.kind==='local_import'&&!['cancelled','paused'].includes(t.status)&&t.storage_status!=='stored')){
   if(this.closed)break;if(t.next_attempt>this.now())continue;
   try{const s=identity(t.source.path);if(fingerprint(s)!==t.source_key){this.store.update(t,{status:'cancelled',message:'Source changed; a new revision has been queued.'});this.queue(s.path);continue;}
    t.observations=(t.observations||0)+1;if(t.observations<3||this.now()-t.stable_since<30000){this.store.save();continue;}
    if(!this.app.connected&&!this.ready)continue;if(this.app.capabilities?.inbox_protocol!==1||!this.app.capabilities?.archive_configured){this.store.update(t,{message:'MSI inbox upgrade/configuration required.'});continue;}
    await this.transfer(t);t.attempts=0;t.next_attempt=0;
   }catch(e){if(this.closed)break;this.store.update(t,{status:['paused','cancelled'].includes(t.status)?t.status:'queued',message:e.message,next_attempt:this.now()+Math.min(300000,5000*2**Math.min(6,t.attempts||0)),attempts:(t.attempts||0)+1});}
  }
 }finally{this.busy=false;}}
 async waitJob(job,t){const id=job.job_id;for(;;){if(this.closed||['paused','cancelled'].includes(t.status))throw Error('Transfer checkpointed.');const j=await this.app.remote.request('get_job',{job_id:id});if(j.status==='completed')return this.app.remote.request('get_results',{job_id:id});if(['failed','needs_review','cancelled','paused'].includes(j.status))throw Error(j.error?.message||'MSI verification needs review; resume the transfer to retry.');await new Promise(r=>setTimeout(r,750));}}
 async transfer(t){
  const folder=path.join(this.app.root,'upload-chunks');fs.mkdirSync(folder,{recursive:true});ordinary(folder,{directory:true});const chunk=contained(folder,path.join(folder,t.request_id+'.part'));if(fs.existsSync(chunk)){ordinary(chunk);if(fs.statSync(chunk).nlink!==1)throw Error('Unsafe local chunk.');fs.unlinkSync(chunk);}
  this.store.update(t,{status:'running',storage_status:'hashing',message:'Reading completed video'});const reader=this.reader(t.source.path,chunk);this.activeReader=reader;
  try{const header=await reader.next();if(header.bytes!==t.source.bytes||fingerprint(identity(t.source.path))!==t.source_key)throw Error('Source changed before its read lock was acquired.');
   const manifest={sha256:header.sha256,bytes:header.bytes,name:t.name,source:{machine:os.hostname(),...t.source}};if(t.upload_manifest&&JSON.stringify(t.upload_manifest)!==JSON.stringify(manifest))throw Error('Source content changed; select the new revision.');this.store.update(t,{upload_manifest:manifest,storage_status:'transferring'});
   const remote=this.app.remote;const state=await remote.request('begin_upload',{request_id:t.request_id,manifest});let receipt=state.receipt;
   if(receipt&&!receipt.index_job_id){const pending=await this.finalizeJob(t.request_id);receipt=(await this.waitJob(pending,t)).manifest.archive_receipt;}
   if(!receipt){const verified=new Map((state.verified||[]).map(v=>[v.index,v]));for(let i=0;i<Math.ceil(header.bytes/CHUNK);i++){
     if(this.closed||['paused','cancelled'].includes(t.status))throw Error('Transfer checkpointed.');const length=Math.min(CHUNK,header.bytes-i*CHUNK);
     if(!verified.has(i)){const v=await reader.read(i*CHUNK,length);await remote.uploadChunk(chunk,state.upload_directory,i);let job=await remote.request('confirm_upload_chunk',{request_id:t.request_id,index:i,sha256:v.sha256,bytes:v.bytes});if(['failed','paused','needs_review'].includes(job.status))job=await remote.request('resume_job',{job_id:job.job_id});await this.waitJob(job,t);fs.unlinkSync(chunk);}
     this.store.update(t,{progress:(i+1)/Math.ceil(header.bytes/CHUNK),message:'Transferred '+Math.min(header.bytes,(i+1)*CHUNK)+' / '+header.bytes+' bytes'});
    }
    this.store.update(t,{storage_status:'verifying',message:'Verifying MSI copy'});const job=await this.finalizeJob(t.request_id);this.store.update(t,{commit_job_id:job.job_id});const result=await this.waitJob(job,t);receipt=result.manifest.archive_receipt;
   }
   this.store.update(t,{storage_status:'stored',status:'completed',archive_receipt:receipt,index_job_id:receipt.index_job_id,indexing_status:'queued',progress:1,message:'Stored on MSI; indexing queued'});
   await this.assignPending(t);
  }finally{reader.close();this.activeReader=null;if(fs.existsSync(chunk)){ordinary(chunk);if(fs.statSync(chunk).nlink===1)fs.unlinkSync(chunk);}}
 }
 async assignPending(t){for(const project of [...(t.pending_project_ids||[])]){try{if(this.app.currentProject)await this.app.currentProject(project);else this.app.knownProject(project);await this.app.remote.request('assign_inbox',{asset_id:t.archive_receipt.asset_id,project_id:project});t.pending_project_ids=t.pending_project_ids.filter(p=>p!==project);t.project_ids=[...new Set([...(t.project_ids||[]),project])];t.association_error=null;this.store.save();}catch(e){this.store.update(t,{association_error:'Project association pending: '+e.message});break;}}}
 deliverySummary(id){return this.store.data.tasks.filter(t=>t.kind==='inbox_delivery'&&t.params.inbox_request===id).map(t=>t.project_id+': '+(t.delivery_status||'pending')).join(', ')||'Not delivered';}
 async refresh(){if(this.app.capabilities?.inbox_protocol!==1||!this.app.capabilities?.archive_configured)return;const items=[];let offset=0;do{const r=await this.app.remote.request('list_inbox',{offset});items.push(...r.items);offset=r.next_offset;}while(offset!==null);
  for(const t of this.store.data.tasks.filter(t=>t.kind==='local_import'&&t.storage_status==='stored'&&t.pending_project_ids?.length))await this.assignPending(t);
  this.store.data.inbox=items;for(const t of this.store.data.tasks.filter(t=>t.kind==='local_import')){const r=items.find(r=>r.request_id===t.request_id);if(r)this.store.update(t,{storage_status:'stored',archive_receipt:r,index_job_id:r.index_job_id,project_ids:r.project_ids});}
  for(const r of items){r.delivery_summary=this.deliverySummary(r.request_id);if(r.index_job_id){try{const j=await this.app.remote.request('get_job',{job_id:r.index_job_id});r.indexing_status=j.status==='completed'?'searchable':j.status;r.indexing_error=j.error?.message||null;}catch(e){r.indexing_status='needs_review';r.indexing_error=e.message;}}else r.indexing_status='needs_review';const t=this.store.data.tasks.find(t=>t.request_id===r.request_id&&t.kind==='local_import');if(t)this.store.update(t,{status:'completed',delivery_summary:r.delivery_summary,indexing_status:r.indexing_status,indexing_error:r.indexing_error});}
  this.store.save();
 }
 async assign(request,project){const r=this.receipt(request);if(this.app.currentProject)await this.app.currentProject(project);else this.app.knownProject(project);await this.app.remote.request('assign_inbox',{asset_id:r.asset_id,project_id:project});await this.refresh();}
 receipt(id){const r=this.store.data.inbox.find(r=>r.request_id===id)||this.store.data.tasks.find(t=>t.request_id===id)?.archive_receipt;if(!r)throw Error('Select a stored inbox video.');return r;}
 async finalizeJob(request_id){let job=await this.app.remote.request('finalize_upload',{request_id});if(['failed','paused','needs_review'].includes(job.status))job=await this.app.remote.request('resume_job',{job_id:job.job_id});return job;}
 async retry(id){const r=this.receipt(id);try{if(!r.index_job_id)throw Error('Missing index identity.');await this.app.remote.request('resume_job',{job_id:r.index_job_id});}catch(e){const j=await this.finalizeJob(r.request_id);await this.waitJob(j,{status:'running'});}await this.refresh();}
 async deliver(id,project){if(this.app.currentProject)await this.app.currentProject(project);else this.app.knownProject(project);const r=this.receipt(id);let t=this.store.data.tasks.find(t=>t.kind==='inbox_delivery'&&t.params.inbox_request===id&&t.project_id===project);if(!t){t=this.store.add('inbox_delivery',{inbox_request:id},project);t.status='completed';t.results={manifest:{assets:[{...r,relative_path:r.manifest.name,kind:'archived_video'}]}};this.store.save();}return this.app.ingest(t);}
 close(){this.closed=true;clearInterval(this.timer);for(const w of this.watchers.values())w.close();this.activeReader?.close();}
}
