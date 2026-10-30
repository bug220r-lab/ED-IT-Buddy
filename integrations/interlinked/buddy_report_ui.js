// BEGIN BUDDY PROJECT REPORT
let buddyReportSequence=0,buddyReportRefreshed=0,buddyReportProject=null,buddyReportSnapshot=null;
function buddyReportModel(value){
 const available=value?.available===true,raw=value?.counts||{};
 const number=key=>Number.isSafeInteger(raw[key])&&raw[key]>=0?raw[key]:0;
 return {available,observed:String(value?.source_observed_at||value?.observed_at||''),counts:{total:number('total'),prepared:number('prepared'),delivered:number('delivered'),attention:number('attention'),running:number('running'),queued:number('queued')},tasks:(Array.isArray(value?.tasks)?value.tasks:[]).slice(0,100),truncated:value?.truncated===true};
}
function buddyRenderReport(value){
 const model=buddyReportModel(value),panel=$('buddy-report-panel');if(!panel)return;
 for(const key of ['prepared','delivered','attention','queued'])$('buddy-report-'+key).textContent=model.available?String(model.counts[key]):'—';
 $('buddy-report-observed').textContent=model.available?'Last Buddy report · '+(model.observed||'observation time not recorded'):'No Buddy report received for this project.';
 $('buddy-report-empty').hidden=model.available&&model.tasks.length>0;
 $('buddy-report-empty').textContent=model.available?'The latest report contains no tasks.':'Run work in ED-IT Buddy and publish its project report to see results here.';
 $('buddy-report-list').replaceChildren();
 for(const task of model.tasks){
  if(!task||typeof task!=='object')continue;
  const item=document.createElement('li');item.className='buddy-report-task'+(task.attention?' attention':'');
  const title=document.createElement('strong');title.textContent=String(task.kind||'Task')+' · '+String(task.status||'unknown');
  const delivery=document.createElement('span');delivery.className='buddy-report-delivery';delivery.textContent='Project delivery: '+String(task.delivery_status||'not_delivered');
  item.append(title,delivery);
  if(task.message){const message=document.createElement('p');message.textContent=String(task.message).slice(0,1000);item.append(message);}
  const outputs=Array.isArray(task.outputs)?task.outputs:[];
  if(outputs.length){
   const references=document.createElement('ul');references.className='buddy-report-outputs';
   for(const output of outputs.slice(0,10)){
    const row=document.createElement('li');row.textContent=String(output?.path||'Recorded output').slice(0,2048)+(typeof output?.sha256==='string'&&/^[a-f0-9]{64}$/.test(output.sha256)?' · SHA-256 '+output.sha256.slice(0,12):'');references.append(row);
   }
   item.append(references);
  }
  if(task.outputs_truncated){const note=document.createElement('small');note.textContent='Additional output references remain in Buddy’s complete report.';item.append(note);}
  if(task.reviewed_by_user_at){const review=document.createElement('small');review.textContent='User marked reviewed: '+String(task.reviewed_by_user_at);item.append(review);}
  $('buddy-report-list').append(item);
 }
 $('buddy-report-limits').textContent=(model.truncated?'Showing up to 100 tasks, with attention items first. ':'')+'Prepared, delivered, and saved to MSI are separate outcomes. Save-back remains explicit.';
}
async function buddyLoadReport(){
 const id=selected?.id,sequence=++buddyReportSequence;if(!$('buddy-report-panel'))return;
 buddyReportRefreshed=Date.now();
 if(buddyReportProject!==id){buddyReportProject=id;buddyReportSnapshot=null;buddyRenderReport(null);}
 $('buddy-report-error').hidden=true;$('buddy-report-refresh').disabled=!id;
 if(!id)return;
 try{
  const value=await api('/api/buddy-report?project='+encodeURIComponent(id));
  if(selected?.id!==id||sequence!==buddyReportSequence)return;
  buddyReportSnapshot=value;buddyRenderReport(value);
 }catch(error){
  if(selected?.id!==id||sequence!==buddyReportSequence)return;
  $('buddy-report-error').textContent='Buddy report unavailable. '+String(error.message||error);$('buddy-report-error').hidden=false;
  if(buddyReportSnapshot)$('buddy-report-observed').textContent='Retained report · local refresh failed. Last source observation: '+String(buddyReportSnapshot.source_observed_at||buddyReportSnapshot.observed_at||'unknown');
 }
}
if($('buddy-report-panel'))$('buddy-report-refresh').addEventListener('click',()=>buddyLoadReport());
// END BUDDY PROJECT REPORT
