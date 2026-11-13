const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict'),path=require('node:path');
class Element {
  constructor(){this.children=[];this.events={};this.style={};this.value='';this.disabled=false;this.hidden=false;this._text='';}
  set textContent(value){this._text=String(value??'');this.children=[];}
  get textContent(){return this._text+this.children.map(c=>c.textContent).join(' ');}
  append(...items){this.children.push(...items);}
  replaceChildren(...items){this._text='';this.children=[...items];}
  addEventListener(name,handler){(this.events[name]??=[]).push(handler);}
  fire(name,event={}){for(const fn of this.events[name]||[])fn(event);}
  getBoundingClientRect(){return {left:0,top:0};}
  setPointerCapture(){} hasPointerCapture(){return true;} releasePointerCapture(){}
}
function fixture(width=340){
  const elements=new Map(),get=id=>{if(!elements.has(id))elements.set(id,new Element());return elements.get(id);};
  const canvas=get('egoCanvas');canvas.clientWidth=width;canvas.clientHeight=400;
  const drawing={texts:[],arcs:[]};
  const ctx={setTransform(){},clearRect(){drawing.texts=[];drawing.arcs=[];},beginPath(){},moveTo(){},lineTo(){},stroke(){},closePath(){},fill(){},fillRect(){},arc(...args){drawing.arcs.push(args);},measureText(value){return {width:String(value).length*7};},fillText(text,x,y,maxWidth){drawing.texts.push({text,x,y,maxWidth});}};
  canvas.getContext=()=>ctx;
  const nodes=Array.from({length:14},(_,i)=>({i:'id'+i,n:i===0?'Alice':i===13?'Domain Admins':'Delegated group '+i,t:i===0?'users':'groups'}));
  const steps=nodes.slice(0,-1).map((n,i)=>({s:n.i,t:nodes[i+1].i,ty:i===12?'AddMember':'MemberOf',sn:n.n,tn:nodes[i+1].n}));
  const events={},raf=[],inspections=[];let rafId=0;
  const window={devicePixelRatio:3,addEventListener:(n,fn)=>(events[n]??=[]).push(fn)};
  const context=vm.createContext({document:{getElementById:get,createElement:()=>new Element(),createTextNode:text=>{const e=new Element();e.textContent=text;return e;}},window,matchMedia:()=>({matches:canvas.clientWidth<850}),requestAnimationFrame:fn=>{raf.push(fn);return ++rafId;},explainNode:n=>inspections.push(['node',n.i]),explainRelationship:e=>inspections.push(['edge',e.ty]),DATA:{Ego:{Nodes:nodes.slice(-5),Edges:steps.slice(-4)},HighValueNodes:[{Id:'id13'}],Analysis:{Objects:nodes,Findings:[{Target:{i:'id13'}}]}}});
  vm.runInContext(fs.readFileSync(path.join(__dirname,'../src/report-graph.js'),'utf8'),context);
  const flush=()=>{let count=0;while(raf.length){raf.shift()();if(++count>20)throw new Error('Rendering failed to settle.');}};
  const send=(name,event)=>{for(const fn of events[name]||[])fn(event);flush();};
  const pointer=(type,id,x,y,pointerType='touch')=>{canvas.fire(type,{pointerId:id,clientX:x,clientY:y,pointerType,button:0});flush();};
  flush();send('bh-highlight-path',{detail:{steps}});
  return {get,canvas,drawing,inspections,flush,send,pointer,steps};
}
const f=fixture();
assert.equal(f.get('graphZoomValue').textContent,'100%','Mobile paths should start at readable zoom.');
assert.match(f.get('graphSelection').textContent,/Alice/);
assert.ok(f.drawing.texts.filter(t=>t.text==='MemberOf').length<=3,'Long mobile paths must not overlap all relationship labels.');
assert.equal(f.drawing.arcs[0][0],46);assert.equal(f.drawing.arcs[0][1],92);
f.pointer('pointerdown',1,100,250);f.pointer('pointermove',1,100,120);f.pointer('pointerup',1,100,120);
assert.equal(f.inspections.length,0,'Dragging must not open the inspector.');
assert.equal(f.drawing.texts.some(t=>t.text==='Alice'),false,'Panning should move Alice out of the viewport.');assert.ok(f.drawing.texts.some(t=>t.text==='Delegated group 1' && t.y===92),'Panning should bring the next node into the readable position.');
f.get('graphRead').fire('click');f.flush();
f.pointer('pointerdown',1,46,92);f.pointer('pointerup',1,46,92);
assert.equal(f.inspections.length,0,'A mobile tap must keep the graph on screen.');
f.get('graphExplain').fire('click');assert.equal(f.inspections.length,1);
f.inspections.length=0;
f.pointer('pointerdown',1,80,180);f.pointer('pointerdown',2,180,180);f.pointer('pointermove',2,280,180);
assert.equal(f.get('graphZoomValue').textContent,'200%','Pinching should change graph scale.');
assert.ok(Math.abs(f.drawing.arcs[0][0]-12)<.01 && Math.abs(f.drawing.arcs[0][1]-4)<.01,'Pinch zoom must remain anchored to its moving center.');
f.pointer('pointerup',2,280,180);f.pointer('pointerup',1,80,180);assert.equal(f.inspections.length,0,'Ending a pinch must not turn into a tap.');
f.get('graphRead').fire('click');f.flush();
f.pointer('pointerdown',3,46,157);f.pointer('pointerup',3,46,157);
assert.match(f.get('graphSelection').textContent,/MemberOf/,'Relationship lines should be selectable.');
f.get('graphExplain').fire('click');assert.equal(f.inspections[0][0],'edge');f.inspections.length=0;
f.get('graphRead').fire('click');f.flush();f.get('graphNext').fire('click');f.flush();assert.equal(f.get('graphNodeSelect').value,'id1');
f.get('graphPrevious').fire('click');f.flush();assert.equal(f.get('graphNodeSelect').value,'id0');
f.get('graphNodeSelect').value='id13';f.get('graphNodeSelect').fire('change');f.flush();assert.equal(f.get('graphNext').disabled,true);
f.pointer('pointerdown',4,100,200);f.pointer('pointercancel',4,100,200);const zoom=f.get('graphZoomValue').textContent;f.pointer('pointermove',4,100,20);assert.equal(f.get('graphZoomValue').textContent,zoom);
f.get('graphZoomIn').fire('click');f.flush();assert.equal(f.get('graphZoomValue').textContent,'130%');
f.get('resetGraph').fire('click');f.flush();assert.match(f.get('graphHint').textContent,/overview/);
const before=f.get('graphZoomValue').textContent;f.canvas.fire('wheel',{clientX:170,clientY:200,deltaY:-100,preventDefault(){}});f.flush();assert.notEqual(f.get('graphZoomValue').textContent,before);
f.canvas.clientWidth=1100;f.send('resize');assert.equal(f.get('graphNodeSelect').value,'id13');
const desktop=fixture(1100);desktop.get('graphRead').fire('click');desktop.flush();
const marker=desktop.drawing.arcs[0];desktop.pointer('pointerdown',1,marker[0],marker[1],'mouse');desktop.pointer('pointerup',1,marker[0],marker[1],'mouse');assert.equal(desktop.inspections[0][0],'node');
console.log('Graph interaction checks passed: mobile path layout, label spacing, touch pan, anchored pinch, tap/drag separation, edge selection, explicit helper, navigation, zoom controls, wheel zoom and responsive resize.');
