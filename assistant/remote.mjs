import fs from 'node:fs';
import path from 'node:path';
import {spawn} from 'node:child_process';
import {randomUUID,createHash} from 'node:crypto';
import {ordinary,contained} from './state.mjs';

const METHODS=new Set(['get_capabilities','register_source_root','list_source_roots','inventory','submit_job','list_jobs','get_job','pause_job','resume_job','cancel_job','get_results','preflight']);
function executable(name){return process.platform==='win32'?path.join(process.env.WINDIR||'C:\\Windows','System32','OpenSSH',name+'.exe'):name;}
export function sshArgs(c){
  for(const k of ['host','identityFile','knownHostsFile','hostKeyAlias'])if(typeof c.ssh[k]!=='string'||/[\r\n\0]/.test(c.ssh[k]))throw Error('Invalid SSH configuration.');
  if(!/^[\w.-]+@[\w.:-]+$/.test(c.ssh.host))throw Error('Invalid MSI host.');
  return ['-i',c.ssh.identityFile,'-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o','UserKnownHostsFile='+c.ssh.knownHostsFile.replaceAll('\\','/'),'-o','HostKeyAlias='+c.ssh.hostKeyAlias,'-o','ForwardAgent=no','-o','ClearAllForwardings=yes','-o','ConnectTimeout=8'];
}
export class Remote {
  constructor(config,{spawnProcess=spawn}={}){this.config=config;this.spawn=spawnProcess;this.pending=new Map();this.child=null;this.buffer='';}
  connect(){
    if(this.child)return;
    const r=this.config.remote;for(const k of ['python','rpc','state','config'])if(typeof r[k]!=='string'||!/^C:\\Users\\jlewi\\AppData\\Local\\ED-IT-Buddy-Media\\[A-Za-z0-9_.\\-]+$/.test(r[k]))throw Error('Remote path is outside the Buddy media installation.');
    const script=`$ProgressPreference='SilentlyContinue'; & '${r.python}' -u '${r.rpc}' --state '${r.state}' --config '${r.config}'; exit $LASTEXITCODE`;
    const args=[...sshArgs(this.config),'-T',this.config.ssh.host,'powershell.exe','-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(script,'utf16le').toString('base64')];
    const child=this.spawn(executable('ssh'),args,{windowsHide:true,shell:false,stdio:['pipe','pipe','pipe']});this.child=child;this.buffer='';
    child.stdout.setEncoding('utf8');child.stdout.on('data',chunk=>{if(this.child!==child)return;this.buffer+=chunk;if(Buffer.byteLength(this.buffer)>8*1024*1024){this.fail(Error('MSI response exceeded limit.'),child);return;}let i;while((i=this.buffer.indexOf('\n'))>=0){const line=this.buffer.slice(0,i);this.buffer=this.buffer.slice(i+1);if(!line.trim())continue;try{const msg=JSON.parse(line);const p=this.pending.get(msg.id);if(!p)continue;clearTimeout(p.timer);this.pending.delete(msg.id);msg.error?p.reject(Error(msg.error.message||JSON.stringify(msg.error))):p.resolve(msg.result);}catch{this.fail(Error('Invalid MSI worker protocol.'),child);}}});
    child.stderr.on('data',()=>{});child.on('error',e=>this.fail(e,child));child.on('exit',code=>this.fail(Error('MSI media connection closed ('+code+').'),child));
  }
  fail(e,source=this.child){if(source!==this.child)return;const child=this.child;this.child=null;for(const p of this.pending.values()){clearTimeout(p.timer);p.reject(e);}this.pending.clear();if(child&&!child.killed)child.kill();}
  request(method,params={}){if(!METHODS.has(method))return Promise.reject(Error('Unsupported media operation.'));this.connect();return new Promise((resolve,reject)=>{const id=randomUUID();const timer=setTimeout(()=>{this.pending.delete(id);reject(Error('MSI response timed out. Reconcile the existing request before retrying.'));},20000);this.pending.set(id,{resolve,reject,timer});this.child.stdin.write(JSON.stringify({id,method,params})+'\n',e=>{if(e)this.fail(e);});});}
  async copyArtifact(asset,localRoot){
    const remote=asset.absolute_path||asset.path;const r=path.win32.relative(this.config.remote.state,remote||'');
    if(!remote||!r||r.startsWith('..')||path.win32.isAbsolute(r)||/["\r\n\0]/.test(remote)||!/^[a-f0-9]{64}$/i.test(asset.sha256||''))throw Error('Unverified remote artifact.');
    fs.mkdirSync(localRoot,{recursive:true});ordinary(localRoot,{directory:true});const ext=path.win32.extname(remote).toLowerCase();if(!/^\.[a-z0-9]{1,12}$/.test(ext))throw Error('Unsupported artifact extension.');
    const dest=contained(localRoot,path.join(localRoot,asset.sha256+ext));
    if(!fs.existsSync(dest)){
      const tmp=dest+'.'+randomUUID()+'.tmp';const src=this.config.ssh.host+':'+remote.replaceAll('\\','/');
      await new Promise((resolve,reject)=>{const c=this.spawn(executable('scp'),[...sshArgs(this.config),src,tmp],{shell:false,windowsHide:true,stdio:['ignore','ignore','pipe']});let err='';c.stderr.on('data',b=>{if(err.length<2000)err+=b;});c.on('error',reject);c.on('exit',code=>code===0?resolve():reject(Error('Artifact transfer failed: '+err)));});
      const hash=await fileHash(tmp);if(hash!==asset.sha256.toLowerCase()||(Number.isFinite(asset.bytes)&&fs.statSync(tmp).size!==asset.bytes)){fs.unlinkSync(tmp);throw Error('Transferred artifact verification failed.');}fs.renameSync(tmp,dest);
    }
    if(await fileHash(dest)!==asset.sha256.toLowerCase())throw Error('Cached artifact changed.');return dest;
  }
  close(){if(this.child)this.child.stdin.end();}
}
export async function fileHash(file){ordinary(file);return await new Promise((resolve,reject)=>{const h=createHash('sha256');const s=fs.createReadStream(file);s.on('data',b=>h.update(b));s.on('end',()=>resolve(h.digest('hex')));s.on('error',reject);});}
