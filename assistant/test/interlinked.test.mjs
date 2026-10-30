import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {Interlinked} from '../interlinked.mjs';
import {Assistant} from '../client.mjs';

function fixture(options={}) {
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'buddy-companion-client-'));
  const directory=path.join(root,'INTERLINKED','buddy-companion');fs.mkdirSync(directory,{recursive:true,mode:0o700});
  const descriptor=path.join(directory,'endpoint.json');
  const value={format:'interlinked-buddy-endpoint-v1',base_url:'http://127.0.0.1:14567',token:'synthetic-capability-token-123456789',pid:process.pid};
  const save=v=>fs.writeFileSync(descriptor,JSON.stringify(v));save(value);
  const calls=[];let ownerChecks=0;
  const client=new Interlinked(root,{verifyOwner:async p=>{ownerChecks++;assert.equal(p,directory);},fetchImpl:async(...args)=>{calls.push(args);return new Response(JSON.stringify({projects:[]}));},...options});
  return {root,directory,descriptor,value,save,client,calls,get ownerChecks(){return ownerChecks;},close(){fs.rmSync(root,{recursive:true,force:true});}};
}

test('companion uses scoped capability, rejects redirects, and supplies a deadline',async()=>{
  const f=fixture();try {
    assert.deepEqual(await f.client.projects(),{projects:[]});assert.equal(f.ownerChecks,1);
    const [url,options]=f.calls[0];assert.equal(url.href,'http://127.0.0.1:14567/api/buddy/projects');
    assert.equal(options.redirect,'error');assert.equal(options.headers['X-INTERLINKED-Buddy-Token'],f.value.token);
    assert.equal(options.headers['X-INTERLINKED-Token'],undefined);assert.equal(options.headers.Origin,undefined);
    assert.ok(options.signal instanceof AbortSignal);assert.equal(options.method,'GET');
  } finally {f.close();}
});

test('untrusted endpoint URL variants never receive a capability',async()=>{
  const f=fixture();try {
    for(const base_url of ['https://127.0.0.1:1234','http://localhost:1234','http://192.168.1.2:1234','http://user:password@127.0.0.1:1234','http://127.0.0.1:1234/?x=1','http://127.0.0.1:1234/#token','http://127.0.0.1:1234/other']) {
      f.save({...f.value,base_url});await assert.rejects(f.client.projects(),/endpoint/);
    }
    assert.equal(f.calls.length,0);
  } finally {f.close();}
});

test('descriptor ownership is checked before network access',async()=>{
  let network=0;const f=fixture({verifyOwner:async()=>{throw Error('Synthetic owner mismatch');},fetchImpl:async()=>{network++;}});
  try {await assert.rejects(f.client.projects(),/owner mismatch/);assert.equal(network,0);}finally{f.close();}
});

test('oversized descriptor is refused before owner or network work',async()=>{
  const f=fixture();try {fs.writeFileSync(f.descriptor,'x'.repeat(16385));await assert.rejects(f.client.projects(),/descriptor/);assert.equal(f.ownerChecks,0);assert.equal(f.calls.length,0);}finally{f.close();}
});

test('streamed response stops at its byte limit and closes the reader',async()=>{
  let consumed=0,closed=false;
  const response={ok:true,body:(async function*(){try{for(let n=0;n<8;n++){consumed++;yield Buffer.alloc(1024*1024);}}finally{closed=true;}})()};
  const f=fixture({fetchImpl:async()=>response});try {await assert.rejects(f.client.projects(),/response exceeded limit/);assert.equal(consumed,3);assert.equal(closed,true);}finally{f.close();}
});

test('receiver errors remain useful without claiming delivery success',async()=>{
  const f=fixture({fetchImpl:async()=>new Response(JSON.stringify({error:'The project is busy'}),{status:409})});
  try {await assert.rejects(f.client.ingest({request_id:'1'.repeat(32),project_id:'band',manifest_path:'synthetic'}),/project is busy/);}finally{f.close();}
});

test('receipt lookup encodes the opaque identity as one query value',async()=>{
  const f=fixture();try {await f.client.operation('id&other=1');const url=f.calls[0][0];assert.equal(url.searchParams.get('request_id'),'id&other=1');assert.equal(url.searchParams.size,1);}finally{f.close();}
});

test('unowned nonempty staging is preserved rather than adopted',async()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'buddy-staging-owner-'));try {
    const staging=path.join(root,'staging');fs.mkdirSync(staging);fs.writeFileSync(path.join(staging,'foreign.txt'),'preserve');
    await assert.rejects(Assistant.prototype.prepareStaging.call({root}),/Unowned staging/);
    assert.equal(fs.readFileSync(path.join(staging,'foreign.txt'),'utf8'),'preserve');assert.equal(fs.existsSync(path.join(staging,'OWNER.json')),false);
  }finally{fs.rmSync(root,{recursive:true,force:true});}
});

test('native Windows staging helper produces protected user and SYSTEM ownership',{skip:process.platform!=='win32'},async()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'buddy-staging-acl-'));try {
    await Assistant.prototype.prepareStaging.call({root});const staging=path.join(root,'staging');
    const owner=JSON.parse(fs.readFileSync(path.join(staging,'OWNER.json'),'utf8').replace(/^\uFEFF/,''));assert.equal(owner.format,'buddy-assistant-staging-v1');assert.match(owner.sid,/^S-1-5-21-/);
    await Assistant.prototype.prepareStaging.call({root});
    const literal=staging.replaceAll("'","''");
    const script=`$p='${literal}'; $a=[IO.Directory]::GetAccessControl($p); if(!$a.AreAccessRulesProtected){throw 'Not protected'}; $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value; foreach($r in $a.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){if($r.IdentityReference.Value -notin @($sid,'S-1-5-18')){throw 'Unexpected principal'}}; 'PRIVATE_STAGING_OK'`;
    const result=await promisify(execFile)('powershell.exe',['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(script,'utf16le').toString('base64')],{windowsHide:true,timeout:10000});assert.match(result.stdout,/PRIVATE_STAGING_OK/);
  }finally{fs.rmSync(root,{recursive:true,force:true});}
});
