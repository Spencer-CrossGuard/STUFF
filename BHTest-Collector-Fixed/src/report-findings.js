// Findings and remediation guidance are embedded into the offline report.
// They describe recorded capabilities; no directory changes are performed.
const FIX_ACTIONS = {
  GenericAll:'Replace unnecessary full-control delegation with the specific permissions the role requires.',
  GenericWrite:'Remove unnecessary broad property-write rights and delegate only the required attributes.',
  WriteDacl:'Remove unnecessary ACL-edit rights. If the grant is inherited, review its originating container.',
  WriteOwner:'Restrict who can change the owner and restore the intended ownership through an approved change.',
  Owns:'Confirm the intended owner, review owner-rights entries, and correct unauthorized ownership.',
  AddMember:'Limit group-membership management to approved administrators or a narrowly scoped provisioning role.',
  AddSelf:'Remove unnecessary self-membership rights from this group.',
  ForceChangePassword:'Limit password-reset delegation to the intended support role and account scope.',
  DCSync:'Retain replication permissions only for domain controllers and explicitly approved replication services.',
  AllExtendedRights:'Replace broad extended-right grants with only the approved individual rights.',
  AdminTo:'Remove unnecessary local-administrator membership, including nested assignments through policy.',
  CanRDP:'Remove unnecessary Remote Desktop membership and review logon allow/deny policy.',
  CanPSRemote:'Remove unnecessary remote-management membership and review endpoint authorization.',
  ExecuteDCOM:'Remove unnecessary DCOM group membership and review application-specific launch permissions.',
  HasSession:'Move privileged administration to trusted administration hosts and end unneeded privileged sessions after coordinating with their owner.',
  AllowedToDelegate:'Remove obsolete allowed-service targets and restrict delegation to approved service accounts and services.',
  AllowedToAct:'Remove unauthorized principals from the target’s resource-based delegation descriptor.',
  HasSIDHistory:'Remove obsolete SID history through the approved migration cleanup process after checking dependent access.',
  ReadLAPSPassword:'Restrict local administrator password readers and review the managed account’s scope and rotation.',
  ReadGMSAPassword:'Restrict managed-service password readers to approved service hosts and administrators.',
  DumpSMSAPassword:'Review access to this managed service account and restrict the hosts and principals that can obtain its secret.',
  AddKeyCredentialLink:'Restrict writes to key-credential data and review existing credentials against approved enrollment.',
  AddAllowedToAct:'Restrict writes that can change resource-based delegation on this computer.',
  WriteAccountRestrictions:'Delegate only the account-restriction properties required by the provisioning role.',
  WriteSPN:'Restrict service-name changes to the approved service-account management role.',
  WriteGPLink:'Restrict policy-link editing and review the linked policies and affected scope.',
  SQLAdmin:'Reduce unnecessary SQL administrative privileges and review the database service identity.',
  MemberOf:'If this membership is unnecessary, remove the source from the target group after checking dependent access.'
};
function remediationFor(edge) {
  return {
    action: FIX_ACTIONS[edge.ty] || HELP[edge.ty]?.check || 'Inspect the original relationship and collector documentation before choosing a change.',
    affected: `${edge.sn || edge.s} → ${edge.tn || edge.t} (${edge.ty})`,
    before: 'Confirm the business owner and service dependencies. Record the exact current membership, delegation setting or ACL, including inherited grants.',
    validation: 'Rescan the same scope with comparable permissions and endpoint coverage. Verify the changed relationship and this chain are absent, check alternative paths, and test the legitimate service or support workflow.',
    rollback: 'If required access breaks, restore only the documented original setting through the same approved change process. Reassess a narrower delegation.'
  };
}
let activeFinding = null;
let findingPage = 0;
const FINDING_PAGE_SIZE = 12;
function findingTerms(f) { return [f.Source.n,f.Target.n,f.Priority,f.Observation,...f.Steps.map(s=>s.ty)].join(' ').toLowerCase(); }
function findingButton(label, callback, primary=false) {
  const b=helpText('button',label,primary?'primary':''); b.type='button';b.addEventListener('click',callback);return b;
}
function renderFindingList() {
  const analysis=DATA.Analysis;
  if(!analysis)return;
  const root=document.getElementById('findingList');root.replaceChildren();
  const q=document.getElementById('findingSearch').value.trim().toLowerCase();
  const status=document.getElementById('findingStatus').value;
  const all=[...analysis.Findings,...(status==='No longer observed' ? analysis.NoLongerObserved : [])];
  const rows=all.filter(f=>(status==='all'||f.Observation===status)&&findingTerms(f).includes(q));
  const pages=Math.max(1,Math.ceil(rows.length/FINDING_PAGE_SIZE));findingPage=Math.min(findingPage,pages-1);
  document.getElementById('findingCount').textContent=`${fmt(rows.length)} matching recorded chains · page ${findingPage+1} of ${pages}`;
  document.getElementById('findingPrevious').disabled=findingPage===0;
  document.getElementById('findingNext').disabled=findingPage+1===pages;
  for(const finding of rows.slice(findingPage*FINDING_PAGE_SIZE,(findingPage+1)*FINDING_PAGE_SIZE)) {
    const card=helpText('article','','finding-card');
    const heading=helpText('div','','finding-heading');heading.append(helpText('span',finding.Priority,'pill '+(finding.Score>=65?'danger':'warn')),helpText('span',finding.Observation,'pill'));
    card.append(heading,helpText('h3',`${finding.Source.n} → ${finding.Target.n}`),helpText('p',`${finding.HopCount??finding.Steps.length} recorded hops · ${finding.Steps.map(s=>s.ty).join(' → ')}`,'note'));
    card.append(helpText('p',`${finding.Source.n} has a recorded chain toward ${finding.Target.n} containing a permission or access capability. Effective access needs validation.`));
    const actions=helpText('div','','toolbar');actions.append(findingButton('Explain path & fixes',()=>selectFinding(finding),true),findingButton('Inspect source',()=>explainNode(finding.Source)));card.append(actions);root.append(card);
  }
  if(!rows.length)root.append(helpText('p','No matching chains in this bounded analysis. Review its limits and collection coverage before drawing conclusions.','empty'));
}
function showFixPlan(step, root) {
  const plan=remediationFor(step);
  helpSection(root,'Recommended change to review',plan.action);
  helpSection(root,'Exact relationship',plan.affected);
  helpSection(root,'Before changing it',plan.before);
  helpSection(root,'How to verify the change',plan.validation);
  helpSection(root,'Rollback if needed',plan.rollback);
}
function selectFinding(finding) {
  activeFinding=finding;
  const root=activateHelper('Path explanation & recommended fixes');
  root.append(helpText('p',`${finding.Source.n} → ${finding.Target.n}`,'context'),helpText('span',finding.Priority,'pill warn'));
  helpSection(root,'Why this is flagged','The recorded chain contains a permission or access capability and reaches a privileged group or collected domain. Pure membership chains are not findings. This is a review candidate, not confirmed exploitation.');
  helpSection(root,'Why this priority?',`Triage score ${finding.Score}. This score orders the discovered candidates; it is not a CVSS score or exploitation probability.`);
  const reasons=document.createElement('ul');for(const reason of finding.Reasons)reasons.append(helpText('li',reason));root.append(reasons);
  helpSection(root,'Evidence completeness',finding.Evidence+(finding.MissingObjects?' Some source identities have no collected object record.':''));
  if(DATA.CoverageNotes?.length)root.append(helpText('p',`${DATA.CoverageNotes.length} collection or analysis notes also apply. Review coverage before deciding whether a change is appropriate.`,'note'));
  root.append(findingButton('Export this review plan',()=>exportFindingPlan(finding)));
  const path=document.getElementById('pathSteps');path.replaceChildren();
  document.getElementById('pathTitle').textContent=`${finding.Source.n} → ${finding.Target.n}`;
  document.getElementById('pathState').textContent=finding.Observation==='No longer observed'?'One or more recorded relationships are absent from the current index. This is not proof of remediation.':'Select a step to read its meaning and recommended fix. The graph highlights available steps.';
  if(finding.PathLimited)document.getElementById('pathState').textContent='This chain has '+finding.HopCount+' hops. Only its first 256 steps are embedded; continue from '+finding.NextPathId+' using TraceFinding.';
  finding.Steps.forEach((step,index)=>{
    const item=helpText('article','','path-step');
    item.append(helpText('span',String(index+1),'step-number'),helpText('h3',`${step.sn} → ${step.tn}`),helpText('span',step.ty,'pill'));
    item.append(helpText('p',HELP[step.ty]?.meaning||'Recorded relationship; verify its source documentation.','note'));
    const buttons=helpText('div','','toolbar');buttons.append(findingButton('Explain this step',()=>{
      explainRelationship(step);const content=document.getElementById('helperContent');showFixPlan(step,content);
      window.dispatchEvent(new CustomEvent('bh-highlight-path',{detail:{steps:[step]}}));
    }),findingButton('Inspect target',()=>explainNode({i:step.t,n:step.tn,t:objectType(step.t)})));
    item.append(buttons);path.append(item);
  });
  if(finding.PathLimited)helpSection(root,'Long-path preview limit','This finding has '+finding.HopCount+' recorded hops. Only the first 256 are embedded. Continue from '+finding.NextPathId+' using the TraceFinding command; this HTML does not show the complete chain.');
  const candidates=finding.Steps.filter(s=>s.ty!=='MemberOf');
  helpSection(root,'Where could this chain be broken?',candidates.length?'Review the permission or access steps below before changing ordinary memberships. A change to one step can break this chain; other routes may remain.':'Review whether the recorded membership is required.');
  for(const step of candidates.slice(0,8)) {
    const plan=remediationFor(step);const box=helpText('div','','context');box.append(helpText('p',plan.affected),helpText('p',plan.action));root.append(box);
  }
  helpSection(root,'Change process & validation','Confirm the owner, save the original configuration, change only unnecessary access, rescan the same scope, and check both alternative paths and legitimate functionality. No changes are made by this report.');
  window.dispatchEvent(new CustomEvent('bh-highlight-path',{detail:{steps:finding.Steps}}));
  document.getElementById('pathReview').hidden=false;document.getElementById('pathReview').scrollIntoView({block:'start',behavior:'smooth'});
}
function objectType(id) {
  return DATA.Analysis?.Objects.find(n=>n.i.toLowerCase()===id.toLowerCase())?.t || DATA.Ego.Nodes.find(n=>n.i.toLowerCase()===id.toLowerCase())?.t || '?';
}
function addObjectEvidence(node, root) {
  const analysis=DATA.Analysis;if(!analysis)return;
  const detail=analysis.Objects.find(n=>n.i.toLowerCase()===node.i.toLowerCase());
  helpSection(root,'Collection time',analysis.CollectionWindows.map(w=>(w.Kind?w.Kind+': ':'')+(w.FinishedUtc||w.CreatedUtc||w.StartedUtc||'Time not supplied')).join(' · ')||'Time not supplied');
  root.append(helpText('p','Collection windows describe the supplied dataset, not a per-object observation timestamp. Merge processing times do not establish the original collection time.','note'));
  if(!detail){helpSection(root,'Collected properties','This object’s full record is not embedded in the bounded HTML. Open graph.sqlite in Path Explorer, search for this object and double-click the result to view its retained properties.');return;}
  if(detail.DirectMemberCount!=null)helpSection(root,'Recorded direct members',fmt(detail.DirectMemberCount)+' direct membership relationships in the current full index. This excludes transitive membership and does not establish effective group access.');
  if(detail.Limited)helpSection(root,'Property limit','This record exceeds the embedded property size limit. Its complete record is retained in graph.sqlite.');
  if(!detail.Properties){if(!detail.Limited)helpSection(root,'Property evidence','No retained object record is available in this index.');return;}
  const block=document.createElement('details');block.open=true;block.append(helpText('summary','Collected object properties'));
  const table=document.createElement('table');for(const [key,value] of Object.entries(detail.Properties)) {
    const row=document.createElement('tr');const text=Array.isArray(value)?value.slice(0,30).join(', ')+(value.length>30?' … (first 30 values shown)':''):typeof value==='object'&&value!==null?JSON.stringify(value):String(value??'');
    row.append(helpText('th',key),helpText('td',text.length>2000?text.slice(0,2000)+' …':text));table.append(row);
  }
  block.append(table);root.append(block);
}
function renderComparison() {
  const root=document.getElementById('comparisonContent');root.replaceChildren();
  const comparison=DATA.Analysis?.Comparison;
  if(!comparison){root.append(helpText('p','To compare full snapshots, select “Compare previous index” when generating the report and choose graph.sqlite from the earlier report. Keep reports in separate folders.','note'));return;}
  root.append(helpText('p',comparison.Baseline,'identity'),helpText('p',comparison.Note,'notice'));
  const grid=helpText('div','','grid');for(const [label,value] of [['New objects',comparison.AddedObjects],['Removed objects',comparison.RemovedObjects],['New relationships',comparison.AddedRelationships],['Removed relationships',comparison.RemovedRelationships],['Unchanged relationships',comparison.UnchangedRelationships],['Paths no longer observed',DATA.Analysis.NoLongerObserved.length]]){const card=helpText('div','','card');card.append(helpText('div',fmt(value),'n'),helpText('div',label,'l'));grid.append(card);}root.append(grid);
  if(comparison.PreviousAnalysisLimited)root.append(helpText('p','The earlier path analysis also reached a bound. Its path list is incomplete; the full-index relationship counts above are separate.','note'));
  for(const [title,rows] of [['New relationship sample',comparison.AddedSample],['Removed relationship sample',comparison.RemovedSample]]) {
    const details=document.createElement('details');details.append(helpText('summary',title+' (up to 50)'));
    for(const edge of rows)details.append(helpText('p',`${edge.Source} → ${edge.Target} · ${edge.EdgeType}`,'identity'));root.append(details);
  }
}
function exportFindingPlan(finding) {
  const lines=['BHTest review plan',finding.Source.n+' → '+finding.Target.n,'Status: '+finding.Observation,'Priority: '+finding.Priority,'Triage score: '+finding.Score,'Generated: '+DATA.GeneratedUtc,'Actual hops: '+(finding.HopCount??finding.Steps.length),'Path preview limited: '+Boolean(finding.PathLimited),'Next path ID: '+(finding.NextPathId||'none'),'','Priority reasons:',...finding.Reasons,'','Recorded steps:'];
  finding.Steps.forEach((s,i)=>{const p=remediationFor(s);lines.push(`${i+1}. ${p.affected}`,`Source ID: ${s.s}`,`Target ID: ${s.t}`,`Meaning: ${HELP[s.ty]?.meaning||'Verify this relationship in its source dataset.'}`,`Recommended change: ${p.action}`,`Before: ${p.before}`,`Validate: ${p.validation}`,`Rollback: ${p.rollback}`,'');});
  lines.push('Coverage notes:',...(DATA.CoverageNotes||[]),'',DATA.Analysis.Method,'A missing relationship can reflect scope or collection differences. Validate changes against a comparable collection.');
  const link=document.createElement('a');link.href=URL.createObjectURL(new Blob([lines.join('\n')],{type:'text/plain;charset=utf-8'}));link.download='bhtest-review-'+finding.Id.slice(0,12)+'.txt';link.click();setTimeout(()=>URL.revokeObjectURL(link.href),1000);
}
function initializeFindings() {
  if(!DATA.Analysis)return;
  const a=DATA.Analysis;
  document.getElementById('analysisNote').textContent=`${fmt(a.TotalFindings??a.Findings.length)} discovered candidates · ${a.Findings.length} embedded · ${fmt(a.Visited)} identities visited · depth limit ${a.MaxDepth||"none"}. ${a.Status ? a.Status+": "+a.StopReason+". " : ""}${a.Limited?'Analysis is incomplete; additional candidates may exist.':'Analysis completed within its configured bounds.'} ${a.Method}`;
  if(a.PreviewLimited)document.getElementById('analysisNote').textContent+=' The HTML preview is limited; open graph.sqlite in Path Explorer and choose Findings to page through every discovered candidate.';
  const update=()=>{findingPage=0;renderFindingList();};document.getElementById('findingSearch').addEventListener('input',update);document.getElementById('findingStatus').addEventListener('change',update);
  document.getElementById('findingPrevious').addEventListener('click',()=>{findingPage--;renderFindingList();});document.getElementById('findingNext').addEventListener('click',()=>{findingPage++;renderFindingList();});
  document.getElementById('highlightWholePath').addEventListener('click',()=>{if(activeFinding){window.dispatchEvent(new CustomEvent('bh-highlight-path',{detail:{steps:activeFinding.Steps}}));document.getElementById('graph').scrollIntoView({block:'start',behavior:'smooth'});}});
  renderFindingList();renderComparison();
}
