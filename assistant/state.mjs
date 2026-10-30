import fs from 'node:fs';
import path from 'node:path';
import {randomUUID} from 'node:crypto';

export function ordinary(file,{directory=false,exists=true}={}) {
  let current=path.resolve(file);
  if(exists) {const s=fs.lstatSync(current); if(s.isSymbolicLink() || (directory?!s.isDirectory():!s.isFile()))throw Error('Expected ordinary '+(directory?'directory':'file')+': '+current);}
  while(current) {if(fs.existsSync(current)&&fs.lstatSync(current).isSymbolicLink())throw Error('Linked paths are not allowed: '+current); const parent=path.dirname(current);if(parent===current)break;current=parent;}
  return path.resolve(file);
}
export function contained(root,file) {const r=path.relative(path.resolve(root),path.resolve(file));if(!r||r.startsWith('..')||path.isAbsolute(r))throw Error('Path is outside owned state.');return ordinary(file,{exists:false});}
export function atomicJson(file,value) {
  ordinary(file,{exists:false}); if(fs.existsSync(file)&&fs.lstatSync(file).nlink!==1)throw Error('Hardlinked state cannot be replaced.');
  const tmp=file+'.'+randomUUID()+'.tmp';fs.writeFileSync(tmp,JSON.stringify(value,null,2),{encoding:'utf8',flag:'wx',mode:0o600});
  fs.renameSync(tmp,file);
}
export class TaskStore {
  constructor(root) {
    this.root=ordinary(root,{exists:false});fs.mkdirSync(root,{recursive:true});ordinary(root,{directory:true});
    this.file=path.join(root,'tasks.json');this.lock=path.join(root,'client.lock');
    try{this.fd=fs.openSync(this.lock,'wx',0o600);}catch(e){
      const prior=JSON.parse(fs.readFileSync(this.lock,'utf8'));let live=true;try{process.kill(prior.pid,0);}catch(err){if(err.code==='ESRCH')live=false;}
      if(live)throw Error('Another Buddy assistant owns the local task ledger.');
      ordinary(this.lock);if(fs.lstatSync(this.lock).nlink!==1)throw Error('Invalid ledger lock.');fs.unlinkSync(this.lock);this.fd=fs.openSync(this.lock,'wx',0o600);
    }
    this.nonce=randomUUID();fs.writeFileSync(this.fd,JSON.stringify({pid:process.pid,nonce:this.nonce}));
    try{
      this.data=fs.existsSync(this.file)?JSON.parse(fs.readFileSync(ordinary(this.file),'utf8')):{format:'buddy-tasks-v1',selected_project_id:null,tasks:[],projects:[],sources:[],threads:{}};
      if(this.data.format!=='buddy-tasks-v1'||!Array.isArray(this.data.tasks))throw Error('Unsupported task ledger; existing bytes preserved.');
    }catch(e){this.close();throw e;}
  }
  save(){atomicJson(this.file,this.data);}
  add(kind,params,project){const t={task_id:randomUUID(),request_id:randomUUID().replaceAll('-',''),project_id:project,kind,params,status:'queued',progress:0,message:'Queued for MSI',created_at:new Date().toISOString(),updated_at:new Date().toISOString(),outputs:[]};this.data.tasks.push(t);this.save();return t;}
  update(t,fields){Object.assign(t,fields,{updated_at:new Date().toISOString()});this.save();return t;}
  close(){if(this.fd!==undefined){fs.closeSync(this.fd);this.fd=undefined;try{ordinary(this.lock);const s=fs.lstatSync(this.lock);const v=JSON.parse(fs.readFileSync(this.lock,'utf8'));if(s.nlink===1&&v.nonce===this.nonce&&v.pid===process.pid)fs.unlinkSync(this.lock);}catch{}}}
}
