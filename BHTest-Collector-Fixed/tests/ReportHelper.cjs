// Execute the shipped helper against a small DOM adapter. No browser packages required.
const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
class Element {
 constructor(tag='div'){this.tagName=tag;this.children=[];this.events={};this.value='';this.className='';this._text='';this.classList={add:()=>{},remove:()=>{}};}
 set textContent(value){this._text=String(value??'');this.children=[];}
 get textContent(){return this._text+this.children.map(c=>c.textContent).join(' ');}
 append(...items){this.children.push(...items);}
 replaceChildren(...items){this._text='';this.children=[...items];}
 addEventListener(name,callback){this.events[name]=callback;}
 querySelectorAll(){return [];}
 focus(){} scrollIntoView(){} click(){this.events.click?.();}
}
function fixture(data){const elements=new Map();const get=id=>{if(!elements.has(id))elements.set(id,new Element());return elements.get(id);};get('reviewFilter').value='all';get('findingStatus').value='all';const document={createElement:tag=>new Element(tag),getElementById:get,querySelector:get};const output={};const context=vm.createContext({document,DATA:data,matchMedia:()=>({matches:false}),window:{dispatchEvent(){},print(){}},CustomEvent:function(){},Blob:function(parts){output.text=parts.join('');},URL:{createObjectURL:()=> 'blob:test',revokeObjectURL(){}},setTimeout:fn=>fn(),fmt:n=>Number(n||0).toLocaleString()});vm.runInContext(fs.readFileSync(require('node:path').join(__dirname,'../src/report-helper.js'),'utf8')+'\n'+fs.readFileSync(require('node:path').join(__dirname,'../src/report-findings.js'),'utf8')+'\ninitializeReport();initializeFindings();',context);return {get,context,output,run:code=>vm.runInContext(code,context)};}
const user='S-1-5-21-1-2-3-1100',group='S-1-5-21-1-2-3-512';
const data={NodeCounts:{users:1,groups:1},EdgeCounts:{acl:1},HighValueNodes:[{Id:group,Name:'Domain Admins',Type:'groups'}],CoverageNotes:['Endpoint phase skipped'],TopAclHolders:[],SearchIndex:[{i:user,n:'<img src=x onerror=alert(1)>',t:'users'}],Ego:{Nodes:[{i:user,n:'Analyst',t:'users'},{i:group,n:'Domain Admins',t:'groups'}],Edges:[{s:user,t:group,ty:'MemberOf'}]},ReviewRelationships:[{s:user,t:group,sn:'<img src=x onerror=alert(1)>',tn:'Domain Admins',ty:'GenericAll'}]};
const app=fixture(data),get=app.get;
assert.match(get('#reviewTable tbody').textContent,/Review first/);
assert.match(get('#reviewTable tbody').textContent,/Context/);
get('#reviewTable tbody').children[0].children[3].children[0].events.click();
assert.match(get('helperContent').textContent,/What should I check/);
assert.match(get('helperContent').textContent,/<img src=x onerror=alert\(1\)>/);
assert.equal(get('helperContent').children.some(c=>c.tagName==='img'),false,'Directory text must remain text.');
get('reviewFilter').value='context';get('reviewFilter').events.change();assert.match(get('#reviewTable tbody').textContent,/MemberOf/);assert.doesNotMatch(get('#reviewTable tbody').textContent,/GenericAll/);
get('reviewSearch').value='does-not-exist';get('reviewSearch').events.input();assert.match(get('#reviewTable tbody').textContent,/No matching/);
get('helpSearch').value='full control';get('helpSearch').events.input();assert.match(get('helpMatches').textContent,/GenericAll/);get('helpMatches').children[0].events.click();assert.equal(get('helperTitle').textContent,'Full control');
get('searchBox').events.input({target:{value:user}});assert.match(get('#searchTable tbody').textContent,/1100/);get('#searchTable tbody').children[0].children[3].children[0].events.click();assert.match(get('helperContent').textContent,/Recorded connections/);
get('explainCoverage').events.click();assert.match(get('helperContent').textContent,/Endpoint phase skipped/);
app.run("explainRelationship({ty:'FutureEdge',s:'a',t:'b'})");assert.match(get('helperContent').textContent,/No risk classification/);
const types=fs.readFileSync(require('node:path').join(__dirname,'../src/BH.Native.cs'),'utf8').split('public static string[] FilterTypes()')[1].split('public static class DirectoryIdentity')[0].match(/"[A-Za-z]+"/g).map(t=>t.slice(1,-1));for(const type of types)assert.equal(app.run(`Boolean(HELP[${JSON.stringify(type)}])`),true,'Missing explanation: '+type);
const empty=fixture({...data,HighValueNodes:[],ReviewRelationships:[],Ego:{Nodes:[],Edges:[]},SearchIndex:[],CoverageNotes:[]});assert.match(empty.get('#reviewTable tbody').textContent,/No matching/);
console.log('Report helper checks passed: evidence, categories, glossary, object IDs, coverage, unknown edges, empty data, safe text and all native path types.');
const finding={Id:'abc123',Source:{i:user,n:'Analyst',t:'users'},Target:{i:group,n:'Domain Admins',t:'groups'},Steps:[{s:user,t:group,sn:'Analyst',tn:'Domain Admins',ty:'WriteDacl'}],Priority:'High review priority',Score:75,Reasons:['Privileged target (+30)','ACL control (+40)','Short chain (+5)'],Observation:'Still observed',Evidence:'Recorded chain; validate access.',MissingObjects:false};
const analysis={Findings:Array.from({length:26},(_,i)=>({...finding,Id:'path'+i})),NoLongerObserved:[],Visited:30,Limited:false,MaxDepth:8,Method:'Bounded search.',Objects:[{i:user,n:'Analyst',t:'users',Properties:{Name:'<script>example</script>',Disabled:false},DirectMemberCount:null}],CollectionWindows:[{FinishedUtc:'2026-11-11T01:00:00Z'}],Comparison:{Baseline:'before.sqlite',AddedObjects:1,RemovedObjects:2,AddedRelationships:3,RemovedRelationships:4,UnchangedRelationships:5,Note:'Scope can differ.',AddedSample:[],RemovedSample:[]}};
const findingsApp=fixture({...data,Analysis:analysis});
assert.equal(findingsApp.get('findingList').children.length,12,'Findings must be paged.');
findingsApp.get('findingNext').events.click();assert.match(findingsApp.get('findingCount').textContent,/page 2 of 3/);
findingsApp.run('selectFinding(DATA.Analysis.Findings[0])');
assert.match(findingsApp.get('helperContent').textContent,/ACL control/);
assert.match(findingsApp.get('helperContent').textContent,/Remove unnecessary ACL-edit rights/);
assert.equal(findingsApp.get('pathSteps').children.length,1);
findingsApp.get('pathSteps').children[0].children.at(-1).children[0].events.click();
assert.match(findingsApp.get('helperContent').textContent,/Rollback if needed/);
assert.match(findingsApp.get('helperContent').textContent,/Rescan the same scope/);
findingsApp.run('explainNode(DATA.Analysis.Findings[0].Source)');
assert.match(findingsApp.get('helperContent').textContent,/<script>example<\/script>/);
assert.match(findingsApp.get('helperContent').textContent,/2026-11-11/);
assert.match(findingsApp.get('comparisonContent').textContent,/Removed relationships/);
const fix=findingsApp.run("remediationFor({ty:'AdminTo',sn:'Support',tn:'Server'})");
assert.match(fix.action,/local-administrator/);assert.match(fix.before,/business owner/);assert.match(fix.validation,/alternative paths/);assert.match(fix.rollback,/restore/i);
findingsApp.get('findingSearch').value='does not exist';findingsApp.get('findingSearch').events.input();assert.match(findingsApp.get('findingList').textContent,/No matching chains/);
console.log('Finding pagination, path inspection, fixes, validation, rollback, priority reasons, properties and comparison UI checks passed.');
findingsApp.run('exportFindingPlan(DATA.Analysis.Findings[0])');
assert.match(findingsApp.output.text,/\nRecommended change: Remove unnecessary ACL-edit rights/);
assert.match(findingsApp.output.text,/Rollback:/);
console.log('Review-plan text export preserves real newlines, recommended changes, validation and rollback.');
const limited=fixture({...data,Analysis:{...analysis,Status:'TimeLimit',StopReason:'Time budget reached; resume is available',TotalFindings:501,PreviewLimited:true,MaxDepth:0,Limited:true}});
assert.match(limited.get('analysisNote').textContent,/501 discovered candidates/);
assert.match(limited.get('analysisNote').textContent,/depth limit none/);
assert.match(limited.get('analysisNote').textContent,/TimeLimit/);
assert.match(limited.get('analysisNote').textContent,/every discovered candidate/);
limited.run("selectFinding({...DATA.Analysis.Findings[0],HopCount:270,PathLimited:true,NextPathId:'continue-here'})");
assert.match(limited.get('pathState').textContent,/270 hops/);
assert.match(limited.get('helperContent').textContent,/continue-here/);
console.log('Deep report status, independent preview limits, unlimited depth labeling and long-path continuation checks passed.');
