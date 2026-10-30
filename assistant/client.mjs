import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import {fileURLToPath} from 'node:url';
import {createInterface} from 'node:readline';
import {randomUUID,createHash} from 'node:crypto';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {TaskStore,atomicJson,ordinary,contained} from './state.mjs';
import {Remote,fileHash} from './remote.mjs';
import {Interlinked} from './interlinked.mjs';
import {CodexChat} from './codex.mjs';

const KINDS=new Set(['download','index','search','preview','extract','organize']);
const TERMINAL=new Set(['completed','failed','cancelled','needs_review','uncertain']);
function requireText(v,label,max=500){if(typeof v!=='string'||!v.trim()||v.length>max)throw Error('Invalid '+label);return v.trim();}
export function validateJob(kind,p){
  if(!KINDS.has(kind)||!p||typeof p!=='object'||Array.isArray(p))throw Error('Unsupported task.');
  const allowed={download:['urls'],index:['root_ids','source_id','sample_seconds','start_seconds','end_seconds'],search:['query','root_ids','source_ids','limit'],preview:['asset_id','start_seconds','end_seconds'],extract:['asset_id','start_seconds','end_seconds'],organize:['asset_ids','group_by','tag']}[kind];
  if(Object.keys(p).some(k=>!allowed.includes(k)))throw Error('Unexpected task parameter.');
  if(kind==='download'){if(!Array.isArray(p.urls)||p.urls.length<1||p.urls.length>50)throw Error('Supply 1–50 video links.');p.urls=p.urls.map(s=>{const u=new URL(requireText(s,'URL',4096));if(!['http:','https:'].includes(u.protocol)||u.username||u.password)throw Error('Use ordinary HTTP video links.');return u.href;});}
  if(kind==='index'){if(p.source_id)p.root_ids=[p.source_id];delete p.source_id;if(!Array.isArray(p.root_ids)||!p.root_ids.length)throw Error('Choose at least one MSI footage source.');}
  if(kind==='search'){p.query=requireText(p.query,'search query',1000);if(p.source_ids?.length)p.root_ids=p.source_ids;delete p.source_ids;if(Array.isArray(p.root_ids)&&!p.root_ids.length)delete p.root_ids;}
  if(kind==='preview'||kind==='extract'){p.asset_id=requireText(p.asset_id,'asset ID',100);for(const k of ['start_seconds','end_seconds'])if(typeof p[k]!=='number'||!Number.isFinite(p[k]))throw Error('Choose valid clip times.');if(p.start_seconds<0||p.end_seconds<=p.start_seconds||p.end_seconds-p.start_seconds>(kind==='preview'?60:3600))throw Error('Previews are at most 60 seconds; extracted clips are at most one hour.');}
  if(kind==='organize'){if(!Array.isArray(p.asset_ids)||!p.asset_ids.length)throw Error('Choose footage to organize.');if(!['date','source','tag'].includes(p.group_by))throw Error('Choose date, source or tag grouping.');}
  return p;
}
export class Assistant {
  constructor({root,localAppData,config,remote,interlinked,chatFactory,event=()=>{}}){
    this.store=new TaskStore(root);this.root=root;this.event=event;this.remote=remote||new Remote(config);this.interlinked=interlinked||new Interlinked(localAppData);this.connected=false;this.syncing=false;this.status={msi:'Disconnected',interlinked:'Disconnected',chat:'Not connected'};
    this.chat=(chatFactory||((o)=>new CodexChat(o)))({executable:config.codexExecutable||'codex.exe',cwd:path.join(root,'chat-workspace'),threads:this.store.data.threads,onThreads:()=>this.store.save(),onDelta:delta=>event({event:'chat_delta',delta}),tool:(op,p,project)=>this.chatTool(op,p,project)});
    this.timer=setInterval(()=>{if(this.connected)this.sync().catch(()=>{});},3000);this.timer.unref();
    this.published=new Map();this.reporting=false;this.lastReportAttempt=0;
  }
  project(p=this.store.data.selected_project_id){return requireText(p,'selected INTERLINKED project',200);}
  tasks(){return this.store.data.tasks.filter(t=>!this.store.data.selected_project_id||t.project_id===this.store.data.selected_project_id||t.scope==='library');}
  snapshot(){return {projects:this.store.data.projects,sources:this.store.data.sources,tasks:this.tasks(),selected_project_id:this.store.data.selected_project_id,status:this.status};}
  async reconnect(){
    this.connected=true;await Promise.allSettled([
      (async()=>{try{const r=await this.interlinked.projects();this.store.data.projects=(r.projects||[]).map(p=>({...p,project_id:p.original_id||p.project_id||p.id,name:p.name||p.title||p.original_id||p.id}));this.status.interlinked='Connected';}catch(e){this.status.interlinked=e.message;}})(),
      (async()=>{try{const c=await this.remote.request('get_capabilities');this.capabilities=c;this.status.msi=c.worker_online?'Connected':'MSI reachable; media worker is not running';const r=await this.remote.request('list_source_roots');this.store.data.sources=(Array.isArray(r)?r:r.roots||r.sources||[]).map(s=>({...s,source_id:s.root_id||s.source_id}));await this.discoverLibraryJobs();}catch(e){this.status.msi=e.message;}})(),
      (async()=>{try{const a=await this.chat.start();this.status.chat=a.signed_in?'Codex signed in':'Codex sign-in needed';}catch(e){this.status.chat=e.message;}})()
    ]);this.store.save();await this.sync();return this.snapshot();
  }
  async queue(kind,p,project){const task=this.store.add(kind,validateJob(kind,structuredClone(p)),this.project(project));if(this.connected)this.sync().catch(()=>{});this.event({event:'state_changed'});return task;}
  async discoverLibraryJobs(){
    const result=await this.remote.request('list_jobs',{limit:100});const sources=new Set(this.store.data.sources.map(s=>s.source_id));
    for(const job of result.jobs||[]){
      if(job.kind!=='index'||!job.params?.root_ids?.some(id=>sources.has(id))||this.store.data.tasks.some(t=>t.remote_job_id===job.job_id))continue;
      this.store.data.tasks.push({task_id:'library-'+job.job_id,request_id:job.request_id,remote_job_id:job.job_id,project_id:job.project_id,scope:'library',kind:'index',params:job.params,status:job.status,progress:0,message:'Library indexing on MSI',created_at:new Date(job.created*1000).toISOString(),updated_at:new Date().toISOString(),outputs:[]});
    }
    this.store.save();
  }
  async sync(){
    if(this.syncing)return;this.syncing=true;
    try{
      for(const t of this.store.data.tasks){
        if(t.status==='queued'&&!t.remote_job_id){
          try{
            const job=await this.remote.request('submit_job',{request_id:t.request_id,kind:t.kind,project_id:t.project_id,params:t.params});
            const identity=job.job_id||job.job?.job_id;if(!identity)throw Error('MSI omitted job identity.');
            this.store.update(t,{remote_job_id:identity,status:job.status||job.job?.status||'queued',message:'Accepted by MSI'});
          }catch(e){this.store.update(t,{message:e.message});this.status.msi=e.message;break;}
        }
        if(t.remote_job_id&&!['delivered','cancelled','failed'].includes(t.status)){
          try{const value=await this.remote.request('get_job',{job_id:t.remote_job_id});const j=value.job||value;const p=j.progress||{};
            let progress=typeof p==='number'?p:0;if(p.total_items)progress=p.completed_items/p.total_items;if(p.total_files)progress=((p.indexed_files||0)+(p.source_duration_seconds?Math.min(1,(p.indexed_seconds||0)/p.source_duration_seconds):0))/p.total_files;
            const stage=typeof p==='object'?(p.stage||p.current_file||''):'';
            this.store.update(t,{status:j.status||t.status,progress:j.status==='completed'?1:Math.max(0,Math.min(1,progress)),message:j.message||j.error?.message||j.error||stage,remote:j});
            if(['completed','needs_review'].includes(j.status)&&!t.results?.manifest){const result=await this.remote.request('get_results',{job_id:t.remote_job_id});if(result.manifest)this.store.update(t,{results:result,outputs:result.manifest.assets||[]});}
          }catch(e){this.status.msi=e.message;break;}
        }
      }
      // File delivery has its own durable lifecycle. In particular, fetching a
      // manifest once must not prevent recovery after an offline/lost-ACK ingest.
      // Reconcile accepted deliveries even when MSI has subsequently disconnected.
      for(const t of this.store.data.tasks)if(t.status==='completed'&&t.results?.manifest&&['download','extract','organize'].includes(t.kind))await this.reconcileDelivery(t);
      this.event({event:'state_changed'});
      this.publishReports();
    }finally{this.syncing=false;}
  }
  find(id){const t=this.store.data.tasks.find(t=>t.task_id===id);if(!t)throw Error('Unknown task.');return t;}
  async results(t){
    if(!t.results?.manifest&&t.remote_job_id){const value=await this.remote.request('get_results',{job_id:t.remote_job_id});if(value.manifest){t.results=value;this.store.save();}}
    const r=t.results?.manifest||t.results||{};const items=r.results?.length?r.results:(r.assets||[]).filter(a=>['video','preview'].includes(a.kind));
    const list=[];for(const item of items.slice(0,100)){const out={...item,source_path:item.source_path||item.absolute_path};
      if(item.source_asset_id)out.asset_id=item.source_asset_id;
      if(item.thumbnail_path&&item.thumbnail_sha256){try{out.thumbnail_path=await this.remote.copyArtifact({absolute_path:item.thumbnail_path,sha256:item.thumbnail_sha256,bytes:item.thumbnail_bytes},path.join(this.root,'previews'));}catch{delete out.thumbnail_path;}}
      if(t.kind==='preview'&&item.sha256)out.preview_path=await this.remote.copyArtifact(item,path.join(this.root,'previews'));
      list.push(out);
    }return {results:list,manifest:r};
  }
  async ingest(t){
    if(!t.results)throw Error('No verified task output is available.');const manifest=t.results.manifest;const assets=manifest?.assets;if(!assets?.length)throw Error('This task has no deliverable files.');
    await this.prepareStaging();
    const dir=contained(this.root,path.join(this.root,'staging',t.request_id));fs.mkdirSync(dir,{recursive:true});ordinary(dir,{directory:true});const files=[];
    const names=new Map();
    for(const a of assets){
      const cached=await this.remote.copyArtifact(a,path.join(this.root,'artifact-cache'));
      const raw=a.relative_path||path.win32.basename(a.absolute_path||cached);
      if(path.win32.isAbsolute(raw)||raw.split(/[\\/]/).some(x=>x==='..'))throw Error('Invalid artifact relative path.');
      let name=raw.split(/[\\/]/).filter(Boolean).map(x=>x.replace(/[<>:"/\\|?*\x00-\x1f]/g,'_').replace(/[. ]+$/,'')).join('/');
      if(!name||name.split('/').some(x=>!x||/^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(x)))name=a.sha256+path.extname(cached);
      if(names.get(name.toLowerCase())===a.sha256)continue;
      if(names.has(name.toLowerCase()))name=path.posix.join(path.posix.dirname(name),a.sha256+'_'+path.posix.basename(name));names.set(name.toLowerCase(),a.sha256);
      const destination=contained(dir,path.join(dir,...name.split('/')));fs.mkdirSync(path.dirname(destination),{recursive:true});ordinary(path.dirname(destination),{directory:true});
      if(!fs.existsSync(destination))fs.copyFileSync(cached,destination,fs.constants.COPYFILE_EXCL);
      if(await fileHash(destination)!==a.sha256)throw Error('Staged delivery file changed.');
      files.push({path:name,bytes:fs.statSync(destination).size,sha256:a.sha256});
    }
    const mpath=path.join(dir,'manifest.json');atomicJson(mpath,{format:'buddy-asset-manifest-v1',request_id:t.request_id,project_id:t.project_id,files});
    this.store.update(t,{delivery_status:'submitting',local_manifest:mpath});
    const receipt=await this.interlinked.ingest({request_id:t.request_id,project_id:t.project_id,manifest_path:mpath});
    this.store.update(t,{delivery_status:receipt.status||'submitted',delivery_receipt:receipt});return receipt;
  }
  async reconcileDelivery(t){
    if(t.delivery_status==='COMPLETED')return;
    const prior=t.delivery_receipt;
    try{
      const receipt=await this.interlinked.operation(t.request_id);
      if(receipt.project_id&&receipt.project_id!==t.project_id)throw Error('Delivery receipt belongs to another project.');
      this.status.interlinked='Connected';
      if(receipt.status==='NOT_FOUND'){
        // A known failed/uncertain/accepted transaction cannot silently become a
        // fresh one when its receipt disappears. Preserve it for reconciliation.
        if(prior&&prior.status!=='NOT_FOUND'||['FAILED','UNCERTAIN','CONFLICT','COPYING','QUEUED'].includes(t.delivery_status)){
          this.store.update(t,{delivery_status:'needs_review',message:'Prepared on MSI; the previously observed delivery receipt is unavailable. Review it before retrying.'});return;
        }
        try{await this.ingest(t);}catch(e){this.store.update(t,{delivery_status:'needs_review',message:'Prepared on MSI; delivery needs attention: '+e.message});}
        return;
      }
      if(!['QUEUED','COPYING','COMPLETED','FAILED','UNCERTAIN'].includes(receipt.status)){
        this.store.update(t,{delivery_status:'needs_review',delivery_receipt:receipt,message:'Prepared on MSI; delivery requires review: '+(receipt.error||receipt.status||'unknown receipt')});return;
      }
      this.store.update(t,{delivery_status:['FAILED','UNCERTAIN'].includes(receipt.status)?'needs_review':receipt.status,delivery_receipt:receipt,...(['FAILED','UNCERTAIN'].includes(receipt.status)?{message:'Prepared on MSI; delivery requires review: '+(receipt.error||receipt.status)}:{})});
    }catch(e){this.status.interlinked=e.message;this.store.update(t,{delivery_status:'needs_review',message:'Prepared on MSI; waiting to reconcile delivery: '+e.message});}
  }
  async prepareStaging(){
    const staging=contained(this.root,path.join(this.root,'staging'));fs.mkdirSync(staging,{recursive:true});ordinary(staging,{directory:true});
    if(!fs.existsSync(path.join(staging,'OWNER.json'))&&fs.readdirSync(staging).length)throw Error('Unowned staging directory contains existing files.');
    if(process.platform==='win32'){
      const literal=staging.replaceAll("'","''");
      const script=`$ErrorActionPreference='Stop'; $p='${literal}'; $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User; $owner=Join-Path $p 'OWNER.json'; if(Test-Path -LiteralPath $owner){$o=Get-Content -LiteralPath $owner -Raw|ConvertFrom-Json; if($o.format -ne 'buddy-assistant-staging-v1' -or $o.sid -ne $sid.Value -or $o.computer -ne $env:COMPUTERNAME){throw 'Staging owner mismatch.'}}; $acl=New-Object Security.AccessControl.DirectorySecurity; $acl.SetAccessRuleProtection($true,$false); foreach($s in @($sid,(New-Object Security.Principal.SecurityIdentifier('S-1-5-18')))){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($s,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}; [IO.Directory]::SetAccessControl($p,$acl); if(!(Test-Path -LiteralPath $owner)){@{format='buddy-assistant-staging-v1';sid=$sid.Value;computer=$env:COMPUTERNAME}|ConvertTo-Json|Set-Content -LiteralPath $owner -Encoding UTF8}`;
      await promisify(execFile)('powershell.exe',['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(script,'utf16le').toString('base64')],{windowsHide:true});
    }
  }
  async preview(p){
    const t=await this.queue('preview',p);if(!this.connected)return t;
    const deadline=Date.now()+120000;
    while(Date.now()<deadline){await this.sync();if(t.status==='failed'||t.status==='cancelled')throw Error(t.message||'Preview failed.');if(t.results){const a=t.results.manifest?.assets?.[0];if(!a)throw Error('Preview has no verified video output.');const preview_path=await this.remote.copyArtifact(a,path.join(this.root,'previews'));return {task_id:t.task_id,preview_path};}await new Promise(r=>setTimeout(r,750));}
    return {task_id:t.task_id,status:t.status,message:'Preview remains queued. Open task results when it completes.'};
  }
  summary(project){const tasks=this.store.data.tasks.filter(t=>t.project_id===project);return {format:'buddy-project-report-v1',project_id:project,observed_at:new Date().toISOString(),counts:tasks.reduce((a,t)=>(a[t.status]=(a[t.status]||0)+1,a),{}),library_work:this.store.data.tasks.filter(t=>t.scope==='library').map(t=>({task_id:t.task_id,kind:t.kind,status:t.status,progress:t.progress,message:t.message})),tasks:tasks.map(t=>({task_id:t.task_id,kind:t.kind,status:t.status,progress:t.progress,message:t.message,delivery_status:t.delivery_status||'not_delivered',outputs:t.delivery_receipt?.files||[],reviewed_by_user_at:t.reviewed_by_user_at||null}))};}
  publishReports(){
    if(this.reporting||Date.now()-this.lastReportAttempt<15000)return;
    this.reporting=true;this.lastReportAttempt=Date.now();
    (async()=>{for(const project of new Set(this.store.data.tasks.filter(t=>t.scope!=='library').map(t=>t.project_id))){const summary=this.summary(project);const stable={...summary};delete stable.observed_at;const fingerprint=createHash('sha256').update(JSON.stringify(stable)).digest('hex');if(this.published.get(project)===fingerprint)continue;try{await this.interlinked.report(project,summary);this.status.interlinked='Connected';this.published.set(project,fingerprint);}catch(e){this.status.interlinked=e.message;break;}}})().finally(()=>{this.reporting=false;});
  }
  async report(format='markdown',project=this.project()){const summary=this.summary(project);const markdown=['# ED-IT Buddy project report','',`Project: ${project}`,`Observed: ${summary.observed_at}`,'',...summary.tasks.map(t=>`- ${t.kind}: ${t.status}; delivery: ${t.delivery_status}${t.message?' — '+t.message:''}`)].join('\n');let publication;try{await this.interlinked.report(project,summary);this.status.interlinked='Connected';publication={status:'published'};}catch(e){publication={status:'failed',message:e.message};}return {summary,content:format==='json'?JSON.stringify(summary,null,2):markdown,markdown,publication};}
  async chatTool(op,p,project){
    if(op==='tasks')return this.summary(project);
    if(op==='sources')return this.store.data.sources;
    if(op==='report')return this.report('markdown',project);
    if(['results','control','ingest'].includes(op)){
      const task=this.find(p.task_id);if(task.project_id!==project&&task.scope!=='library')throw Error('That task belongs to another project.');
      if(op==='ingest'&&task.scope==='library')throw Error('Select or extract a clip into the current project before delivery.');
      if(op==='results')return this.results(task);
      if(op==='ingest')return this.ingest(task);
      return this.handle('task_action',{task_id:task.task_id,action:p.action});
    }
    if(op==='preview'){const task=await this.queue('preview',p,project);return {task_id:task.task_id,status:task.status,message:'Preview is queued; use task results when complete.'};}
    if(['download','index','search','extract','organize'].includes(op))return this.queue(op,p,project);
    throw Error('Unsupported Buddy tool.');
  }
  async handle(method,p={}){
    if(!p||typeof p!=='object'||Array.isArray(p))throw Error('Expected object parameters.');
    switch(method){
      case 'bootstrap':return this.snapshot();
      case 'reconnect':return this.reconnect();
      case 'projects':return {projects:this.store.data.projects};
      case 'select_project':{const id=this.project(p.project_id);if(!this.store.data.projects.some(x=>x.project_id===id))throw Error('Select a known INTERLINKED project.');this.store.data.selected_project_id=id;this.store.save();return this.snapshot();}
      case 'sources':return {sources:this.store.data.sources};
      case 'register_source':{if(!this.connected)throw Error('Connect to MSI to register footage.');const r=await this.remote.request('register_source_root',{path:requireText(p.path,'MSI source path',240)});await this.reconnect();return r;}
      case 'submit':return this.queue(p.kind,p.params);
      case 'search':return this.queue('search',{query:p.query,...(p.source_ids?.length?{root_ids:p.source_ids}:{})});
      case 'tasks':return {...this.snapshot()};
      case 'task_action':{const t=this.find(p.task_id);if(p.action==='mark_reviewed')return this.store.update(t,{reviewed_by_user_at:new Date().toISOString()});if(!['pause','resume','cancel'].includes(p.action))throw Error('Unsupported task action.');if(!t.remote_job_id){if(p.action==='cancel')return this.store.update(t,{status:'cancelled'});throw Error('Task has not reached MSI.');}const r=await this.remote.request(p.action+'_job',{job_id:t.remote_job_id});this.store.update(t,{status:r.status||r.job?.status||t.status});await this.sync();return r;}
      case 'results':return this.results(this.find(p.task_id));
      case 'preview':return this.preview(p);
      case 'extract':return this.queue('extract',p);
      case 'organize':return this.queue('organize',p);
      case 'ingest':return this.ingest(this.find(p.task_id));
      case 'send_chat':return this.chat.send(this.project(),p.text,p.attachment_path);
      case 'login_chat':return this.chat.login();
      case 'report':return this.report(p.format||'markdown');
      default:throw Error('Unknown assistant operation: '+method);
    }
  }
  close(){clearInterval(this.timer);this.remote.close();this.chat.close();this.store.close();}
}

if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)){
  const local=process.env.LOCALAPPDATA||path.join(os.homedir(),'.local','share');const root=process.env.EDIT_BUDDY_ASSISTANT_STATE||path.join(local,'ED-IT Buddy','assistant');
  const configFile=process.env.EDIT_BUDDY_ASSISTANT_CONFIG||fileURLToPath(new URL('./config.example.json',import.meta.url));const config=JSON.parse(fs.readFileSync(configFile,'utf8').replace(/^\uFEFF/,''));
  const output=v=>process.stdout.write(JSON.stringify(v)+'\n');const app=new Assistant({root,localAppData:local,config,event:output});let closing=false;
  const input=createInterface({input:process.stdin,crlfDelay:Infinity});input.on('line',line=>{if(Buffer.byteLength(line)>1024*1024){output({id:null,error:{message:'Request exceeded limit.'}});return;}let m;try{m=JSON.parse(line);}catch{output({id:null,error:{message:'Invalid JSON.'}});return;}app.handle(m.method,m.params||{}).then(result=>output({id:m.id,result,error:null}),error=>output({id:m.id,result:null,error:{message:error.message}}));});
  const close=()=>{if(!closing){closing=true;app.close();}};input.on('close',close);process.on('SIGTERM',()=>{close();process.exit(0);});process.on('SIGINT',()=>{close();process.exit(0);});
}
