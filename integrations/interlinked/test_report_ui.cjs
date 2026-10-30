'use strict';
// Synthetic report models/DOM and mocked local GUI API only. No browser or MSI.
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const root=__dirname,source=fs.readFileSync(path.join(root,'buddy_report_ui.js'),'utf8');
const html=fs.readFileSync(path.join(root,'candidate/repository/app/web/index.html'),'utf8');
const generated=fs.readFileSync(path.join(root,'candidate/repository/app/web/app.js'),'utf8');
const elements={},requests=[],tags=[],checks=[];
function element(){return {hidden:false,disabled:false,textContent:'',children:[],listeners:{},className:'',append(...values){this.children.push(...values);},replaceChildren(...values){this.children=values;},addEventListener(name,fn){this.listeners[name]=fn;}};}
let result={available:false},response=null,error=null;
const context={selected:{id:'opaque-ui-1',originalId:'stable-original'},document:{hidden:false,createElement:tag=>{tags.push(tag);return element();}},$:id=>elements[id]??=element(),api:async(route,body)=>{requests.push({route,body});if(error)throw error;return response?await response:result;}};
vm.createContext(context);vm.runInContext(source,context);
function check(condition,label){assert.ok(condition,label);checks.push(label);}
function fixture(){return {available:true,observed_at:'2026-10-30T10:00:00Z',source_observed_at:'2026-10-30T09:59:00Z',counts:{total:3,prepared:2,delivered:1,attention:1,queued:1,running:0},tasks:[{task_id:'download',kind:'download',status:'completed',delivery_status:'COMPLETED',message:'Prepared',outputs:[{path:'media/clip.mp4',sha256:'a'.repeat(64)}]},{task_id:'failed',kind:'index',status:'failed',delivery_status:'not_delivered',message:'Source needs review',attention:true}]};}
(async()=>{
 await context.buddyLoadReport();check(elements['buddy-report-prepared'].textContent==='—'&&!elements['buddy-report-empty'].hidden,'Missing report is unavailable rather than zero completed work');
 check(requests[0].route==='/api/buddy-report?project=opaque-ui-1','GUI sends its selected opaque identity for server-side stable mapping');
 result=fixture();await context.buddyLoadReport();
 check(elements['buddy-report-prepared'].textContent==='2'&&elements['buddy-report-delivered'].textContent==='1','Prepared and delivered outcomes remain distinct');
 check(elements['buddy-report-attention'].textContent==='1'&&elements['buddy-report-queued'].textContent==='1','Attention and queued counts are visible');
 check(elements['buddy-report-observed'].textContent.includes('09:59:00'),'Display retains source observation rather than implying current worker status');
 check(elements['buddy-report-list'].children[1].className.includes('attention'),'Task blockers have an attention treatment');
 check(elements['buddy-report-list'].children[0].children[3].children[0].textContent.includes('media/clip.mp4'),'Output references include project-relative text');
 result={...fixture(),tasks:[{kind:'local_import',status:'completed',storage_status:'stored',indexing_status:'failed',delivery_status:'not_delivered'}]};await context.buddyLoadReport();check(elements['buddy-report-list'].children[0].children[1].textContent.includes('MSI storage: stored')&&elements['buddy-report-list'].children[0].children[1].textContent.includes('Indexing: failed')&&elements['buddy-report-list'].children[0].children[1].textContent.includes('not_delivered'),'Stored inbox media retains separate indexing and delivery observations');
 const literal='<img src=x onerror=alert(1)>';result={...fixture(),tasks:[{kind:literal,status:literal,delivery_status:literal,message:literal,outputs:[{path:literal,sha256:'b'.repeat(64)}]}]};await context.buddyLoadReport();
 const row=elements['buddy-report-list'].children[0];check(row.children[0].textContent.includes(literal)&&row.children[2].textContent===literal&&row.children[3].children[0].textContent.includes(literal),'Untrusted report labels, messages, and paths render literally');
 check(!tags.includes('a')&&!tags.includes('img')&&!source.includes('innerHTML')&&!source.includes('eval('),'Report data cannot create arbitrary links, images, HTML or code');
 result={...fixture(),truncated:true,tasks:Array.from({length:101},()=>({kind:'index',status:'running',message:'x'.repeat(1500),outputs:Array.from({length:11},()=>({path:'x'.repeat(3000)})),outputs_truncated:true}))};await context.buddyLoadReport();
 check(elements['buddy-report-list'].children.length===100&&elements['buddy-report-limits'].textContent.includes('100'),'Task display is bounded with truncation disclosure');
 const bounded=elements['buddy-report-list'].children[0];check(bounded.children[2].textContent.length===1000&&bounded.children[3].children.length===10&&bounded.children[3].children[0].textContent.length===2048,'Messages and output references are bounded');
 result=fixture();await context.buddyLoadReport();error=new Error('Local report read failed');await context.buddyLoadReport();
 check(!elements['buddy-report-error'].hidden&&elements['buddy-report-observed'].textContent.startsWith('Retained report')&&elements['buddy-report-prepared'].textContent==='2','Read failure preserves a visibly historical same-project report');error=null;
 let resolveOld;response=new Promise(resolve=>resolveOld=resolve);const old=context.buddyLoadReport();context.selected={id:'opaque-ui-2'};response=null;result={...fixture(),counts:{prepared:7}};await context.buddyLoadReport();resolveOld({...fixture(),counts:{prepared:99}});await old;
 check(elements['buddy-report-prepared'].textContent==='7','A delayed response cannot overwrite a newly selected project');
 context.selected=null;await context.buddyLoadReport();check(elements['buddy-report-refresh'].disabled&&elements['buddy-report-prepared'].textContent==='—','Clearing the project clears report data');
 check(requests.every(r=>r.body===undefined&&r.route.startsWith('/api/buddy-report?')),'Report refresh performs local GUI GETs only, with no MSI work or companion-token requests');
 check(!source.includes('X-INTERLINKED-Buddy-Token')&&!source.includes('endpoint.json'),'Native companion discovery and capability never enter the browser');
 check(html.includes('id="buddy-report-panel"')&&html.includes('id="buddy-report-attention"')&&html.includes('Save-back remains explicit'),'Candidate markup includes report counts and save-back distinction');
 check(generated.includes("if($('buddy-report-panel'))buddyLoadReport();")&&generated.includes('Date.now()-buddyReportRefreshed>5000'),'Candidate hooks selected-project and bounded periodic refresh');
 console.log(JSON.stringify({status:'PASS',count:checks.length,checks,scope:'Synthetic DOM, mocked local GUI GETs, generated source hooks; no browser, real project or MSI access.'},null,2));
})().catch(error=>{console.error(error);process.exitCode=1;});
