// Portable canvas graph: pointer gestures use CSS pixels; drawing is capped by the report data.
(function setupGraph() {
  const byId = id => document.getElementById(id);
  const canvas = byId('egoCanvas');
  const wrap = byId('egowrap');
  const ctx = canvas.getContext('2d');
  const note = byId('egoNote');
  const hint = byId('graphHint');
  const info = byId('egoInfo');
  const selection = byId('graphSelection');
  const nodePicker = byId('graphNodeSelect');
  const explain = byId('graphExplain');
  const colors = {users:'#79b3ff',groups:'#f6c777',computers:'#77d9bb',ous:'#a4b4ca',gpos:'#c792ea',domains:'#edf3fc',special:'#e0b44c','?':'#94a3b8'};
  const targets = new Set([...(DATA.HighValueNodes || []).map(n => n.Id), ...(DATA.Analysis?.Findings || []).map(f => f.Target.i)]);
  const defaults = {nodes:DATA.Ego.Nodes, edges:DATA.Ego.Edges};
  let nodes = defaults.nodes, edges = defaults.edges;
  let pathMode = false, vertical = false, compact = nodes.length <= 12;
  let positions = new Map(), labelHits = [], selectedId = null, selectedEdge = null;
  let view = {x:0,y:0,scale:1}, pixelRatio = 1;
  let frame = null, ready = false, moved = false, tapOrigin = null;
  const pointers = new Map();
  const edgeKey = edge => JSON.stringify([edge.s,edge.t,edge.ty]);
  const narrow = () => canvas.clientWidth < 620;
  const mobile = () => typeof matchMedia === 'function' && matchMedia('(max-width:850px)').matches;
  const screen = point => ({x:canvas.clientWidth/2+view.x+point.x*view.scale,y:canvas.clientHeight/2+view.y+point.y*view.scale});
  const clampScale = value => Math.max(.005,Math.min(6,value));
  const overlap = (a,b) => a.l<b.r && a.r>b.l && a.t<b.b && a.b>b.t;
  const outside = (a,b,w,h) => Math.max(a.x,b.x)<-30 || Math.min(a.x,b.x)>w+30 || Math.max(a.y,b.y)<-30 || Math.min(a.y,b.y)>h+30;

  function scheduleDraw() {
    if(frame!==null)return;
    frame=requestAnimationFrame(() => {frame=null;draw();});
  }
  function layout() {
    positions.clear();vertical=pathMode && narrow();compact=pathMode || nodes.length<=12;
    if(pathMode) {
      nodes.forEach((node,index) => positions.set(node.i,{node,index,x:vertical?0:index*210,y:vertical?index*130:0}));
      return;
    }
    const incoming=new Map(), depth=new Map(), queue=[];
    for(const edge of edges){if(!incoming.has(edge.t))incoming.set(edge.t,[]);incoming.get(edge.t).push(edge.s);}
    for(const node of nodes)if(targets.has(node.i)){depth.set(node.i,0);queue.push(node.i);}
    for(let i=0;i<queue.length;i++)for(const source of incoming.get(queue[i])||[])if(!depth.has(source)){depth.set(source,depth.get(queue[i])+1);queue.push(source);}
    const maxDepth=Math.max(1,...depth.values()),rings=new Map();
    for(const node of nodes){const d=depth.get(node.i)??maxDepth+1;if(!rings.has(d))rings.set(d,[]);rings.get(d).push(node);}
    const roots=(rings.get(0)||[]).length,rootRadius=roots>1?Math.min(150,Math.max(45,roots*18)):0;
    for(const [d,ring] of rings)ring.forEach((node,index)=>{
      const radius=rootRadius+d*(compact?140:80),angle=index/ring.length*Math.PI*2;
      positions.set(node.i,{node,index:d,x:radius*Math.cos(angle),y:radius*Math.sin(angle)});
    });
  }
  function renderLegend() {
    const legend=byId('egoLegend');legend.replaceChildren();
    const types=new Set(nodes.map(n=>n.t));
    for(const [type,color] of Object.entries(colors))if(types.has(type)){
      const item=document.createElement('span'),dot=document.createElement('span');dot.className='dot';dot.style.background=color;
      item.append(dot,document.createTextNode(type));legend.append(item);
    }
    if(nodes.some(n=>targets.has(n.i))){const item=document.createElement('span'),dot=document.createElement('span');dot.className='dot';dot.style.background='#ff8796';item.append(dot,document.createTextNode('privileged target'));legend.append(item);}
  }
  function renderPicker() {
    nodePicker.replaceChildren();
    const empty=document.createElement('option');empty.value='';empty.textContent='Choose a node…';nodePicker.append(empty);
    for(const node of nodes){const option=document.createElement('option');option.value=node.i;option.textContent=node.n||node.i;nodePicker.append(option);}
    nodePicker.value=selectedId||'';
    byId('graphPrevious').hidden=!pathMode;byId('graphNext').hidden=!pathMode;byId('graphRead').disabled=!pathMode;
    renderLegend();
  }
  function fitAll() {
    if(!positions.size)return;
    const all=[...positions.values()],xs=all.map(p=>p.x),ys=all.map(p=>p.y);
    const minX=Math.min(...xs),maxX=Math.max(...xs),minY=Math.min(...ys),maxY=Math.max(...ys);
    const scale=clampScale(Math.min(1.5,(canvas.clientWidth-30)/(maxX-minX+170),(canvas.clientHeight-80)/(maxY-minY+120)));
    view={x:-(minX+maxX)*scale/2,y:-(minY+maxY)*scale/2,scale};
    hint.textContent=pathMode?'Whole-path overview. Choose Read path or use Previous/Next to inspect each step.':'Drag to pan; pinch or use +/− to zoom. Tap a node or a line to select it.';
    scheduleDraw();
  }
  function focusNode(id) {
    const point=positions.get(id);if(!point)return;
    view.scale=1;
    view.x=(vertical?46:Math.min(110,canvas.clientWidth/3))-canvas.clientWidth/2-point.x;
    view.y=(vertical?92:canvas.clientHeight/2)-canvas.clientHeight/2-point.y;
    hint.textContent='Read path: drag with one finger, pinch to zoom, or use Previous/Next. Scroll the page outside the canvas.';
    scheduleDraw();
  }
  function updateSelection() {
    selection.replaceChildren();explain.disabled=!selectedId && !selectedEdge;
    nodePicker.value=selectedId||'';
    if(selectedEdge) {
      const from=positions.get(selectedEdge.s)?.node,to=positions.get(selectedEdge.t)?.node;
      selection.textContent=(from?.n||selectedEdge.s)+' → '+(to?.n||selectedEdge.t)+' · '+selectedEdge.ty;
      info.textContent=selectedEdge.ty+' relationship selected';explain.textContent='Explain relationship';
    } else if(selectedId) {
      const point=positions.get(selectedId),node=point.node;
      const title=document.createElement('strong');title.textContent=node.n||node.i;
      const detail=document.createElement('div');detail.className='note';detail.textContent=node.t+' · '+node.i;
      selection.append(title,detail);
      info.textContent=pathMode?`Node ${point.index+1} of ${nodes.length} · ${node.n||node.i}`:node.n||node.i;
      explain.textContent='Explain selected node';
    } else {
      selection.textContent='Tap a node, its name, or a relationship line to select it.';info.textContent='Interactive graph';explain.textContent='Explain selection';
    }
    const index=nodes.findIndex(n=>n.i===selectedId);
    byId('graphPrevious').disabled=index<=0;byId('graphNext').disabled=index<0||index>=nodes.length-1;
  }
  function explainSelection() {
    if(selectedEdge)explainRelationship({...selectedEdge,sn:positions.get(selectedEdge.s)?.node.n||selectedEdge.s,tn:positions.get(selectedEdge.t)?.node.n||selectedEdge.t});
    else if(selectedId)explainNode(positions.get(selectedId).node);
  }
  function selectNode(id,focus=false,openInspector=false) {
    if(!positions.has(id))return;
    selectedId=id;selectedEdge=null;updateSelection();if(focus)focusNode(id);else scheduleDraw();
    if(openInspector && !mobile())explainSelection();
  }
  function zoomBetween(before,after,factor) {
    const cx=canvas.clientWidth/2,cy=canvas.clientHeight/2;
    const worldX=(before.x-cx-view.x)/view.scale,worldY=(before.y-cy-view.y)/view.scale;
    view.scale=clampScale(view.scale*factor);
    view.x=after.x-cx-worldX*view.scale;view.y=after.y-cy-worldY*view.scale;scheduleDraw();
  }
  function zoom(factor) {const center={x:canvas.clientWidth/2,y:canvas.clientHeight/2};zoomBetween(center,center,factor);}
  function resize() {
    pixelRatio=Math.max(1,Math.min(3,window.devicePixelRatio||1));
    canvas.width=Math.round(canvas.clientWidth*pixelRatio);canvas.height=Math.round(canvas.clientHeight*pixelRatio);
    const changed=pathMode && vertical!==narrow();
    if(changed){pointers.clear();layout();focusNode(selectedId||nodes[0]?.i);}
    scheduleDraw();
  }

  function drawNodeLabels(w,h) {
    labelHits=[];const boxes=[],seen=new Set();
    ctx.font='600 13px Segoe UI,Arial,sans-serif';ctx.textBaseline='middle';
    const ordered=[...(selectedId?[positions.get(selectedId)]:[]),...positions.values()];
    for(const point of ordered) {
      if(!point || seen.has(point.node.i) || boxes.length>=80)continue;seen.add(point.node.i);
      const p=screen(point),active=point.node.i===selectedId;
      if(p.x<0||p.x>w||p.y<45||p.y>h-12)continue;
      if(!active&&!compact&&!targets.has(point.node.i)&&view.scale<1.2)continue;
      const full=point.node.n||point.node.i,label=full.length>30?full.slice(0,29)+'…':full;
      const width=Math.min(w-16,ctx.measureText(label).width+12);
      let x=pathMode&&vertical?p.x+21+width/2:p.x,y=pathMode&&vertical?p.y:p.y+22;
      x=Math.max(width/2+5,Math.min(w-width/2-5,x));
      if(y>h-12)y=p.y-22;
      const box={l:x-width/2,r:x+width/2,t:y-11,b:y+11};
      if(!active&&boxes.some(b=>overlap(box,b)))continue;
      boxes.push(box);labelHits.push({...box,id:point.node.i});
      ctx.fillStyle=active?'#203c62':'#101b2e';ctx.fillRect(box.l,box.t,width,22);
      ctx.fillStyle='#edf3fc';ctx.textAlign='center';ctx.fillText(label,x,y,width-10);
    }
    return boxes;
  }
  function draw() {
    const w=canvas.clientWidth,h=canvas.clientHeight;if(w<1||h<1)return;
    ctx.setTransform(pixelRatio,0,0,pixelRatio,0,0);ctx.clearRect(0,0,w,h);
    const labels=[];
    for(const edge of edges) {
      const from=positions.get(edge.s),to=positions.get(edge.t);if(!from||!to)continue;
      const a=screen(from),b=screen(to);if(outside(a,b,w,h))continue;
      const chosen=selectedEdge && edgeKey(edge)===edgeKey(selectedEdge);
      const active=chosen||pathMode||selectedId&&(edge.s===selectedId||edge.t===selectedId);
      ctx.strokeStyle=chosen?'#f6c777':active?'#91bbff':'#465d7c';ctx.lineWidth=chosen?3:active?2:1;
      ctx.beginPath();ctx.moveTo(a.x,a.y);ctx.lineTo(b.x,b.y);ctx.stroke();
      const length=Math.hypot(b.x-a.x,b.y-a.y);
      if(length>24){const angle=Math.atan2(b.y-a.y,b.x-a.x),x=b.x-Math.cos(angle)*11,y=b.y-Math.sin(angle)*11;ctx.beginPath();ctx.moveTo(x,y);ctx.lineTo(x-Math.cos(angle-.45)*8,y-Math.sin(angle-.45)*8);ctx.lineTo(x-Math.cos(angle+.45)*8,y-Math.sin(angle+.45)*8);ctx.closePath();ctx.fillStyle=ctx.strokeStyle;ctx.fill();}
      if(active)labels.push({edge,a,b,chosen,length});
    }
    for(const point of positions.values()) {
      const p=screen(point);if(p.x<-15||p.x>w+15||p.y<-15||p.y>h+15)continue;
      const isTarget=targets.has(point.node.i),radius=point.node.i===selectedId?9:Math.max(3,Math.min(isTarget?9:6,view.scale*(isTarget?9:6)));
      ctx.beginPath();ctx.arc(p.x,p.y,radius,0,Math.PI*2);ctx.fillStyle=isTarget?'#ff8796':colors[point.node.t]||colors['?'];ctx.fill();
      if(point.node.i===selectedId){ctx.strokeStyle='#fff';ctx.lineWidth=2;ctx.stroke();}
    }
    const boxes=drawNodeLabels(w,h);
    ctx.font='12px Segoe UI,Arial,sans-serif';ctx.textBaseline='middle';ctx.textAlign='center';
    labels.sort((a,b)=>Number(b.chosen)-Number(a.chosen));
    for(const item of labels.slice(0,150)) {
      const {edge,a,b,length}=item,width=ctx.measureText(edge.ty).width+12;
      const isVertical=Math.abs(b.x-a.x)<24;
      // Screen-space spacing and collision checks suppress dense overview labels.
      if(length<(isVertical?52:width+20))continue;
      const x=isVertical?(a.x+b.x)/2+22+width/2:(a.x+b.x)/2;
      const y=isVertical?(a.y+b.y)/2:(a.y+b.y)/2-13;
      const box={l:x-width/2,r:x+width/2,t:y-10,b:y+10};
      if(box.l<4||box.r>w-4||box.t<40||box.b>h-4||boxes.some(b=>overlap(box,b)))continue;
      boxes.push(box);ctx.fillStyle='#0e1a2d';ctx.fillRect(box.l,box.t,width,20);ctx.fillStyle=item.chosen?'#f6c777':'#c2d6f3';ctx.fillText(edge.ty,x,y);
    }
    byId('graphZoomValue').textContent=Math.round(view.scale*100)+'%';
  }
  function selectAt(point,touch) {
    let closest=null,distance=touch?24:16;
    for(const p of positions.values()){const s=screen(p),d=Math.hypot(s.x-point.x,s.y-point.y);if(d<distance){distance=d;closest=p.node.i;}}
    if(!closest)closest=labelHits.find(b=>point.x>=b.l&&point.x<=b.r&&point.y>=b.t&&point.y<=b.b)?.id;
    if(closest){selectNode(closest,false,true);return;}
    let best=null,bestDistance=touch?14:9;
    for(const edge of edges){const p=positions.get(edge.s),q=positions.get(edge.t);if(!p||!q)continue;const a=screen(p),b=screen(q),dx=b.x-a.x,dy=b.y-a.y,squared=dx*dx+dy*dy;if(!squared)continue;const t=Math.max(0,Math.min(1,((point.x-a.x)*dx+(point.y-a.y)*dy)/squared)),d=Math.hypot(point.x-a.x-t*dx,point.y-a.y-t*dy);if(d<bestDistance){best=edge;bestDistance=d;}}
    if(best){selectedEdge=best;selectedId=null;updateSelection();scheduleDraw();if(!mobile())explainSelection();}
  }
  const local = event => {const r=canvas.getBoundingClientRect();return{x:event.clientX-r.left,y:event.clientY-r.top};};
  function pair() {const p=[...pointers.values()];return{mid:{x:(p[0].x+p[1].x)/2,y:(p[0].y+p[1].y)/2},distance:Math.max(1,Math.hypot(p[0].x-p[1].x,p[0].y-p[1].y))};}
  canvas.addEventListener('pointerdown',event=>{
    if(event.pointerType==='mouse'&&event.button!==0)return;
    const p=local(event);pointers.set(event.pointerId,p);
    if(pointers.size===1){moved=false;tapOrigin=p;}else moved=true;
    try{canvas.setPointerCapture(event.pointerId);}catch{}canvas.style.cursor='grabbing';
  });
  canvas.addEventListener('pointermove',event=>{
    if(!pointers.has(event.pointerId))return;
    const old=pointers.get(event.pointerId),before=pointers.size>=2?pair():null,next=local(event);pointers.set(event.pointerId,next);
    if(pointers.size>=2){const after=pair();moved=true;zoomBetween(before.mid,after.mid,after.distance/before.distance);}
    else{if(tapOrigin&&Math.hypot(next.x-tapOrigin.x,next.y-tapOrigin.y)>6)moved=true;view.x+=next.x-old.x;view.y+=next.y-old.y;scheduleDraw();}
  });
  function endPointer(event,cancelled) {
    if(!pointers.has(event.pointerId))return;
    const tap=!cancelled&&!moved&&pointers.size===1;
    pointers.delete(event.pointerId);
    try{if(canvas.hasPointerCapture(event.pointerId))canvas.releasePointerCapture(event.pointerId);}catch{}
    if(tap)selectAt(local(event),event.pointerType!=='mouse');
    if(pointers.size){moved=true;tapOrigin=null;}else canvas.style.cursor='grab';
  }
  canvas.addEventListener('pointerup',event=>endPointer(event,false));
  canvas.addEventListener('pointercancel',event=>endPointer(event,true));
  canvas.addEventListener('lostpointercapture',event=>endPointer(event,true));
  canvas.addEventListener('wheel',event=>{event.preventDefault();const p=local(event);zoomBetween(p,p,Math.exp(-Math.max(-120,Math.min(120,event.deltaY))*.003));},{passive:false});
  canvas.addEventListener('keydown',event=>{
    const pan={ArrowLeft:[45,0],ArrowRight:[-45,0],ArrowUp:[0,45],ArrowDown:[0,-45]};
    if(pan[event.key]){event.preventDefault();view.x+=pan[event.key][0];view.y+=pan[event.key][1];scheduleDraw();}
    else if(event.key==='+'||event.key==='='){event.preventDefault();zoom(1.3);}
    else if(event.key==='-'){event.preventDefault();zoom(1/1.3);}
    else if(event.key==='0'){event.preventDefault();fitAll();}
  });
  byId('graphZoomIn').addEventListener('click',()=>zoom(1.3));byId('graphZoomOut').addEventListener('click',()=>zoom(1/1.3));
  byId('resetGraph').addEventListener('click',fitAll);
  byId('graphRead').addEventListener('click',()=>{const id=selectedId||nodes[0]?.i;if(id)selectNode(id,true);});
  byId('graphPrevious').addEventListener('click',()=>{const index=nodes.findIndex(n=>n.i===selectedId);if(index>0)selectNode(nodes[index-1].i,true);});
  byId('graphNext').addEventListener('click',()=>{const index=nodes.findIndex(n=>n.i===selectedId);if(index>=0&&index<nodes.length-1)selectNode(nodes[index+1].i,true);});
  nodePicker.addEventListener('change',()=>selectNode(nodePicker.value,true));explain.addEventListener('click',explainSelection);
  byId('restorePreview').addEventListener('click',()=>{
    nodes=defaults.nodes;edges=defaults.edges;pathMode=false;selectedId=null;selectedEdge=null;layout();renderPicker();updateSelection();wrap.style.display=nodes.length?'block':'none';resize();fitAll();
    note.textContent=nodes.length?`${nodes.length} nodes and ${edges.length} relationships in the bounded four-hop preview.`:DATA.Ego.Reason||'No privileged-group preview is included.';
  });
  window.addEventListener('bh-highlight-path',event=>{
    edges=event.detail.steps;const ids=[...new Set(edges.flatMap(e=>[e.s,e.t]))];
    nodes=ids.map(id=>DATA.Analysis?.Objects.find(n=>n.i===id)||(()=>{const e=edges.find(e=>e.s===id||e.t===id);return{i:id,n:e.s===id?e.sn:e.tn,t:'?'};})());
    pathMode=true;selectedEdge=null;selectedId=nodes[0]?.i||null;layout();renderPicker();updateSelection();wrap.style.display=nodes.length?'block':'none';resize();
    note.textContent=`${edges.length} recorded steps in the selected finding. Select a node or relationship for details.`;
    if(vertical)focusNode(selectedId);else fitAll();
  });
  window.addEventListener('bh-select-node',event=>{if(positions.has(event.detail)){selectedId=event.detail;selectedEdge=null;updateSelection();scheduleDraw();}});
  window.addEventListener('resize',resize);
  if(typeof ResizeObserver!=='undefined'){const observer=new ResizeObserver(()=>{if(ready)resize();});observer.observe(canvas);}
  layout();renderPicker();updateSelection();wrap.style.display=nodes.length?'block':'none';
  note.textContent=nodes.length?`${nodes.length} nodes and ${edges.length} relationships in the bounded four-hop preview.`:DATA.Ego.Reason||'No privileged-group preview is included.';
  resize();fitAll();ready=true;
})();
