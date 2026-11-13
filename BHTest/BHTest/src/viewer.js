// The browser retains one result page and one path segment, never the full graph.
const $=id=>document.getElementById(id);
let capability=location.hash.slice(1)||sessionStorage.getItem('bh-viewer-session')||'';
if(capability)sessionStorage.setItem('bh-viewer-session',capability);
history.replaceState(null,'',location.pathname);
let stopped=false,currentObject=null,relationshipCursor={},continuation=null,objectGeneration=0;
async function api(route,args={},method='GET'){
  if(stopped)throw Error('Viewer stopped. Start the PowerShell launcher again.');
  const query=new URLSearchParams(args).toString();
  const response=await fetch('/api/'+route+(query?'?'+query:''),{method,headers:{Authorization:'Bearer '+capability},cache:'no-store',credentials:'omit'});
  const text=await response.text();let result;try{result=JSON.parse(text);}catch{throw Error(text||'Viewer unavailable. Check the launcher.');}
  if(!response.ok)throw Error(result.Error||'Request failed.');return result;
}
function status(id,text,error=false){$(id).textContent=text;$(id).classList.toggle('error',error);}
function action(label,run){return findingButton(label,()=>task('sessionStatus',run));}
async function task(id,run){try{await run();}catch(error){status(id,error.message||String(error),true);}}
function remember(node){const old=DATA.Analysis.Objects.findIndex(n=>n.i.toLowerCase()===node.i.toLowerCase());if(old>=0)DATA.Analysis.Objects.splice(old,1);DATA.Analysis.Objects.push(node);if(DATA.Analysis.Objects.length>600)DATA.Analysis.Objects.shift();}
function showPath(f,kind='saved'){
  DATA.Analysis.Objects=[];for(const e of f.Steps){remember({i:e.s,n:e.sn,t:e.st});remember({i:e.t,n:e.tn,t:e.tt});}
  f.Id=f.Source.i;f.Reasons||=[];f.Evidence||='Recorded relationships in the selected index; effective access requires validation.';f.Observation||='On-demand recorded path';f.Priority||='Path to review';f.Score??=0;
  reviewEdges=f.Steps;selectFinding(f);
  if(!f.PathLimited)$('pathState').textContent='Complete recorded path. Select a step for its meaning, evidence and recommended fix.';
  if(kind==='query'){
    const root=$('helperContent');root.replaceChildren();helpSection(root,'Recorded path',f.Source.n+' → '+f.Target.n);helpSection(root,'What this establishes','The index contains this chain of recorded relationships. A custom target is not necessarily privileged, and a chain alone does not establish effective access. Inspect each step and its recommended checks.');
  }
  continuation=f.PathLimited?{kind,start:f.NextPathId,target:f.Target.i}:null;
  $('pathContinue').hidden=!continuation;
  if(continuation){$('pathState').textContent=`${fmt(f.HopCount)} recorded hops in this remaining chain. Showing ${f.Steps.length} steps. Use Continue this path for the next segment.`;for(const h of $('helperContent').querySelectorAll('h3'))if(h.textContent==='Long-path preview limit')h.nextElementSibling.textContent='This view displays one segment. Use Continue this path to inspect subsequent steps.';}
  if(kind==='query'&&!f.PathLimited)$('pathState').textContent='Complete recorded path. Inspect each relationship to understand its meaning and recommended checks.';
  $('graph').scrollIntoView({block:'start',behavior:'smooth'});
}
async function savedPath(id){const f=await api('recorded',{id});showPath(f);}
function pager(kind){
  const findings=kind==='findings';return {kind,findings,cursor:findings?{score:2147483647,sequence:0}:{after:''},history:[],next:null,q:'',busy:false,page:1};
}
const findingPager=pager('findings'),objectPager=pager('search');
async function loadPage(p){
  if(p.busy)return;p.busy=true;const id=p.findings?'findingCount':'searchNote';
  const previous=$(p.findings?'findingPrevious':'objectPrevious'),next=$(p.findings?'findingNext':'objectNext');previous.disabled=true;next.disabled=true;
  try{
    status(id,'Searching index…');let data,scanned=0,cursor={...p.cursor};
    // Empty scan windows advance automatically, with a clear continuation after one million rows.
    for(let window=0;window<20;window++){
      data=await api(p.kind,{...cursor,q:p.q});scanned+=data.Scanned;
      cursor=p.findings?{score:data.AfterScore,sequence:data.AfterSequence}:{after:data.After};
      status(id,`${fmt(scanned)} rows inspected…`);
      if(data.Rows.length||!data.More)break;
    }
    p.next=data.More?cursor:null;
    const root=p.findings?$('findingList'):$('searchTable').querySelector('tbody');root.replaceChildren();
    for(const row of data.Rows){
      if(p.findings){
        const card=helpText('article','','finding-card');card.append(helpText('span',row.Score>=65?'High review priority':'Medium review priority','pill warn'),helpText('h3',row.Source.n+' → '+row.Target.n),helpText('p',`${fmt(row.HopCount)} recorded hops · score ${row.Score}`,'note'),action('Explain path & fixes',()=>savedPath(row.Source.i)),action('Inspect source',()=>inspect(row.Source.i)));root.append(card);
      }else{
        const tr=document.createElement('tr'),cell=document.createElement('td');cell.append(action('Inspect',()=>inspect(row.i)),action('Source',()=>useNode(row,'source')),action('Target',()=>useNode(row,'target')));tr.append(helpText('td',row.n),helpText('td',row.t),cell);root.append(tr);
      }
    }
    status(id,`${fmt(data.Rows.length)} matches · page ${p.page} · ${fmt(scanned)} rows inspected this page. ${data.More?'More index data available. Continue with Next page.':p.q?'Search complete for the remaining index.':'End of index.'}`);
    if(!data.Rows.length)root.append(helpText(p.findings?'p':'caption',data.More?'No matches in this window. Continue to search the remaining index.':'No matches in the remaining index.','empty'));
  }finally{p.busy=false;previous.disabled=p.history.length===0;next.disabled=!p.next;}
}
function wirePager(p,form,input,previous,next){
  $(form).addEventListener('submit',e=>{e.preventDefault();if(p.busy)return;p.q=$(input).value.trim();p.cursor=p.findings?{score:2147483647,sequence:0}:{after:''};p.history=[];p.next=null;p.page=1;task(p.findings?'findingCount':'searchNote',()=>loadPage(p));});
  $(next).addEventListener('click',()=>{if(p.busy||!p.next)return;p.history.push(p.cursor);p.cursor=p.next;p.page++;task(p.findings?'findingCount':'searchNote',()=>loadPage(p));});
  $(previous).addEventListener('click',()=>{if(p.busy||!p.history.length)return;p.cursor=p.history.pop();p.page--;task(p.findings?'findingCount':'searchNote',()=>loadPage(p));});
}
function useNode(node,which){$(which==='source'?'sourceId':'targetId').value=node.i;status('pathResult',`${which==='source'?'Source':'Target'} selected: ${node.n}`);}
async function inspect(id){
  const generation=++objectGeneration;const node=await api('object',{id});if(generation!==objectGeneration)return;currentObject=node;remember(node);
  $('objectDetail').hidden=false;$('objectName').textContent=node.n;$('objectId').textContent=node.i+' · '+node.t;
  let record;try{record=node.Record?JSON.stringify(JSON.parse(node.Record),null,2):null;}catch{record='The retained record is not valid JSON. Review the source dataset.';}
  $('objectProperties').textContent=node.Limited?'This record exceeds the viewer’s 1 MiB decompressed / 2 MiB stored property limit. The full record remains in the index.':record||'No collected object properties are available for this identity.';
  const root=activateHelper(node.n);root.append(helpText('p',node.i,'identity'));helpSection(root,'What is this?',OBJECT_HELP[node.t]||'An identity referenced by the indexed directory data.');helpSection(root,'Why is it shown?','It was selected from the full index or a recorded path. Its presence alone does not flag it as unsafe.');helpSection(root,'Evidence','Read its collected properties and browse incoming or outgoing relationships below. Relationship explanations describe possible capabilities, checks and recommended fixes.');
  window.dispatchEvent(new CustomEvent('bh-select-node',{detail:node.i}));relationshipCursor={};$('direction').value='in';await loadRelationships(generation);$('objectDetail').scrollIntoView({block:'start',behavior:'smooth'});
}
explainNode=node=>{task('sessionStatus',()=>inspect(node.i));};
async function loadRelationships(generation=objectGeneration){
  if(!currentObject)return;const id=currentObject.i,direction=$('direction').value;status('relationshipNote','Loading relationships…');$('relationshipNext').disabled=true;
  let data,scanned=0;
  for(let window=0;window<20;window++){
    data=await api('relationships',{id,direction,...relationshipCursor});if(generation!==objectGeneration)return;
    scanned+=data.Scanned;relationshipCursor={target:data.Target,bucket:data.Bucket,source:data.Source,type:data.Type};
    status('relationshipNote',`${fmt(scanned)} rows inspected…`);if(data.Rows.length||!data.More)break;
  }
  reviewEdges=data.Rows;const root=$('connections');root.replaceChildren();
  for(const edge of data.Rows){const row=helpText('div','','connection');row.append(helpText('p',edge.sn+' → '+edge.tn+' · '+edge.ty),action('Explain & recommended fix',()=>{explainRelationship(edge);showFixPlan(edge,$('helperContent'));}),action('Show relationship',()=>{window.dispatchEvent(new CustomEvent('bh-highlight-path',{detail:{steps:[edge]}}));$('graph').scrollIntoView({block:'start',behavior:'smooth'});}),action('Inspect other object',()=>inspect(direction==='in'?edge.s:edge.t)));root.append(row);}
  status('relationshipNote',`${data.Rows.length} ${direction==='in'?'incoming':'outgoing'} relationships · ${fmt(scanned)} rows inspected. ${data.More?'More data remains.':'End of relationships.'}${direction==='out'?' Outgoing searches scan disk because this index has no source-side index.':''}`);$('relationshipNext').disabled=!data.More;
}
async function queryPath(source,target){
  const p=await api('path',{source,target,limit:$('visitLimit').value});
  if(p.Status==='Found'){status('pathResult',`${fmt(p.HopCount)} recorded hops · ${fmt(p.Visited)} identities visited.`);showPath(p,'query');}
  else status('pathResult',p.Status==='VisitLimit'?`Visit budget reached (${fmt(p.Visited)} identities). The result is incomplete. Increase the budget or choose a closer source/target.`:'No path found through the supported resolved relationships in this index. Collection gaps and other relationship types may hide routes.');
}
wirePager(findingPager,'findingForm','findingSearch','findingPrevious','findingNext');wirePager(objectPager,'objectForm','searchBox','objectPrevious','objectNext');
$('useSource').addEventListener('click',()=>useNode(currentObject,'source'));$('useTarget').addEventListener('click',()=>useNode(currentObject,'target'));
$('relationshipForm').addEventListener('submit',e=>{e.preventDefault();objectGeneration++;relationshipCursor={};task('relationshipNote',()=>loadRelationships());});
$('direction').addEventListener('change',()=>{objectGeneration++;relationshipCursor={};task('relationshipNote',()=>loadRelationships());});
  $('relationshipNext').addEventListener('click',()=>task('relationshipNote',()=>loadRelationships()));
$('pathForm').addEventListener('submit',e=>{e.preventDefault();const button=e.submitter;button.disabled=true;status('pathResult','Searching recorded relationships…');task('pathResult',()=>queryPath($('sourceId').value.trim(),$('targetId').value.trim())).finally(()=>button.disabled=false);});
$('pathContinue').addEventListener('click',()=>{const button=$('pathContinue');button.disabled=true;task('pathResult',async()=>{if(!continuation)return;const c=continuation;if(c.kind==='saved')await savedPath(c.start);else await queryPath(c.start,c.target);}).finally(()=>button.disabled=false);});
$('highlightWholePath').addEventListener('click',()=>{if(activeFinding){window.dispatchEvent(new CustomEvent('bh-highlight-path',{detail:{steps:activeFinding.Steps}}));$('graph').scrollIntoView({block:'start',behavior:'smooth'});}});
$('helpSearch').addEventListener('input',()=>{const q=$('helpSearch').value.trim().toLowerCase(),root=$('helpMatches');root.replaceChildren();if(!q)return;for(const [ty,h] of Object.entries(HELP).filter(([ty,h])=>(ty+' '+h.title+' '+h.meaning).toLowerCase().includes(q)).slice(0,12))root.append(action(ty+' · '+h.title,()=>{explainRelationship({ty,sn:'Source principal',tn:'Target object'});showFixPlan({ty},$('helperContent'));}));});
$('openGuide').addEventListener('click',()=>{$('helper').classList.add('active');$('helpSearch').focus();});$('toggleHelper').addEventListener('click',()=>{$('helper').classList.toggle('active');$('toggleHelper').textContent=$('helper').classList.contains('active')?'Hide details':'Show details';});
$('stopViewer').addEventListener('click',()=>task('sessionStatus',async()=>{await api('stop',{},'POST');stopped=true;capability='';sessionStorage.removeItem('bh-viewer-session');document.body.querySelectorAll('button,input,select').forEach(e=>e.disabled=true);status('sessionStatus','Viewer stopped. Close this tab. Restart the PowerShell launcher to reopen the index.');}));
task('sessionStatus',async()=>{
  const summary=await api('summary');let nodes=0,edges=0;for(const row of summary.Nodes)nodes+=Number(row[1]);for(const row of summary.Edges)edges+=Number(row[1]);
  for(const [label,value] of [['Directory objects',nodes],['Relationships',edges],['Saved candidates',summary.Analysis.Findings],['Visited in analysis',summary.Analysis.Visited]]){const card=helpText('div','','card');card.append(helpText('div',fmt(value),'n'),helpText('div',label,'l'));$('summaryCards').append(card);}
  $('analysisNote').textContent=summary.Analysis.Status==='NotStarted'?'No saved deep analysis in this index. Generate a Deep report to populate saved findings. Object search and on-demand paths work now.':`${fmt(summary.Analysis.Findings)} saved candidates · ${summary.Analysis.Status}: ${summary.Analysis.StopReason}. Paging shows all saved candidates; an incomplete analysis may have undiscovered candidates.`;
  status('sessionStatus','Connected to the selected read-only index. Only this computer can access the viewer.');
});
task('findingCount',()=>loadPage(findingPager));task('searchNote',()=>loadPage(objectPager));
