import fs from 'node:fs';
import path from 'node:path';
import {ordinary} from './state.mjs';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';

export class Interlinked {
  constructor(localAppData,{fetchImpl=fetch,verifyOwner}={}){this.descriptor=path.join(localAppData,'INTERLINKED','buddy-companion','endpoint.json');this.fetch=fetchImpl;this.verifyOwner=verifyOwner||verifyEndpointOwner;}
  async call(route,body){
    const s=fs.statSync(ordinary(this.descriptor));if(s.size>16384||s.nlink!==1)throw Error('Invalid INTERLINKED companion descriptor.');
    await this.verifyOwner(path.dirname(this.descriptor));
    const d=JSON.parse(fs.readFileSync(this.descriptor,'utf8'));const u=new URL(d.base_url);
    if(d.format!=='interlinked-buddy-endpoint-v1'||u.protocol!=='http:'||u.hostname!=='127.0.0.1'||u.pathname!=='/'||!u.port||u.username||u.password||u.search||u.hash||!/^\S{24,256}$/.test(d.token||''))throw Error('Invalid INTERLINKED companion endpoint.');
    const response=await this.fetch(new URL(route,d.base_url),{redirect:'error',method:body===undefined?'GET':'POST',headers:{'X-INTERLINKED-Buddy-Token':d.token,...(body===undefined?{}:{'Content-Type':'application/json'})},body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(30000)});
    let total=0;const chunks=[];for await(const chunk of response.body){total+=chunk.length;if(total>2*1024*1024)throw Error('INTERLINKED response exceeded limit.');chunks.push(chunk);}const result=JSON.parse(Buffer.concat(chunks).toString('utf8'));if(!response.ok||result.error)throw Error(typeof result.error==='string'?result.error:result.error?.message||result.message||'INTERLINKED operation failed.');return result;
  }
  projects(){return this.call('/api/buddy/projects');}
  ingest(request){return this.call('/api/buddy/ingest',request);}
  operation(id){return this.call('/api/buddy/operation?request_id='+encodeURIComponent(id));}
  report(project_id,summary){return this.call('/api/buddy/report',{project_id,summary});}
}

async function verifyEndpointOwner(root){
  ordinary(path.join(root,'OWNER.json'));
  if(process.platform!=='win32'){if(fs.statSync(root).mode&0o077)throw Error('Companion directory is not private.');return;}
  const p=root.replaceAll("'","''");
  const script=`$ErrorActionPreference='Stop';$p='${p}';$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;$owner=Get-Content -LiteralPath (Join-Path $p 'OWNER.json') -Raw|ConvertFrom-Json;if($owner.format -ne 'interlinked-buddy-endpoint-owner-v1' -or $owner.sid -ne $sid -or $owner.computer -ne $env:COMPUTERNAME){throw 'Companion owner mismatch.'};$acl=[IO.Directory]::GetAccessControl($p);if(!$acl.AreAccessRulesProtected){throw 'Companion ACL is inherited.'};foreach($r in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){if($r.IdentityReference.Value -notin @($sid,'S-1-5-18') -or $r.AccessControlType -ne 'Allow'){throw 'Companion ACL is not private.'}}`;
  await promisify(execFile)('powershell.exe',['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(script,'utf16le').toString('base64')],{windowsHide:true,timeout:10000,maxBuffer:65536});
}
