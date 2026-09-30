// Purpose: drive production popup handlers during a deliberately delayed backend response.
// Dependencies: Node standard library. Outputs: PASS lines, no hardware/files/network changes.
// Command: node tools/check-quick.mjs. Fixture timing is not physical hardware latency.
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const read = file => readFileSync(new URL('../www/'+file, import.meta.url), 'utf8');
const handlers = new Map(), ids = new Map();
let buttons = [], hostMessage;
class Element {
  constructor(attrs='') {
    this.dataset={}; this.attributes={}; this.classList={toggle(){}}; this.disabled=/\bdisabled\b/.test(attrs);
    for (const [,key,value] of attrs.matchAll(/(?:data-([\w-]+))="([^"]*)"/g)) this.dataset[key.replace(/-([a-z])/g,(_,c)=>c.toUpperCase())]=value;
    this.id=/\bid="([^"]+)"/.exec(attrs)?.[1]; if(this.id) ids.set(this.id,this);
  }
  set innerHTML(value) {
    this.html=value;
    if(this.id==='performance-modes' || this.id==='system-tools') {
      buttons=buttons.filter(b=>this.id==='performance-modes' ? !b.dataset.mode : !b.dataset.system);
      for(const [,attrs] of value.matchAll(/<button\b([^>]*)>/g)) buttons.push(new Element(attrs));
    }
  }
  get innerHTML(){return this.html||'';}
  setAttribute(key,value){this.attributes[key]=value;}
  closest(){return this;}
  addEventListener(){}
  click(){if(!this.disabled) handlers.get('click')({target:this});}
}
for(const [,attrs] of read('quick.html').matchAll(/<button\b([^>]*)>/g)) buttons.push(new Element(attrs));
const id = key => ids.get(key) || (ids.set(key,Object.assign(new Element(),{id:key})),ids.get(key));
function matches(node,selector){const key=/\[data-([^=\]]+)/.exec(selector)?.[1];const value=/="?([^"\]]+)"?\]/.exec(selector)?.[1];return key && key in node.dataset && (!value||node.dataset[key]===value);}
const document={hidden:false,activeElement:null,getElementById:id,documentElement:{dataset:{},style:{setProperty(){},removeProperty(){}}},
  querySelectorAll:selector=>buttons.filter(b=>selector.split(',').some(s=>matches(b,s.trim()))),
  querySelector:selector=>buttons.find(b=>matches(b,selector)),addEventListener:(key,fn)=>handlers.set(key,fn)};
const h={deviceName:'Fixture',powerSource:'Online',mode:'Auto',firmwareAvailable:true,brightness:50,requestedChargeLimit:70,chargeLimit:70,careLimit:70,travel:false,
  touchpadEnabled:true,touchscreenEnabled:true,refreshRate:120,refreshRates:[60,120],batteryPercent:50,
  preferences:{autoRefresh:false,acRefreshRate:120,batteryRefreshRate:60},customization:{quickLinks:[]}};
const requests=[];
const host={addEventListener:(_,fn)=>hostMessage=fn,postMessage({id,method,args}){
  requests.push({method,args});
  setTimeout(()=>{
    let data={};
    if(method==='quick.read') data={hardware:structuredClone(h)};
    else if(method==='window.state') data={awake:false};
    else if(method==='performance.set') h.mode=args.value;
    else if(method==='battery.care') h.requestedChargeLimit=h.chargeLimit=args.on?70:100;
    else if(method==='display.automatic') h.preferences.autoRefresh=args.on;
    else if(method==='display.cycle') h.refreshRate=h.refreshRate===120?60:120;
    else throw new Error('Unexpected fixture action '+method);
    hostMessage({data:{id,ok:true,data}});
  },method==='performance.set'?120:1);
}};
const context=vm.createContext({document,window:{chrome:{webview:host}},console,matchMedia:()=>({matches:false,addEventListener(){}}),
  getComputedStyle:()=>({getPropertyValue:key=>({'--preset-bg':'#ffffff','--preset-surface':'#ffffff'})[key]||''}),
  setTimeout,clearTimeout,setInterval:()=>0});
for(const file of ['bridge.js','quick-tools.js','quick.js']) vm.runInContext(read(file),context);
const value=code=>vm.runInContext(code,context);
async function settle(){for(let n=0;n<500;n++){if(value('snapshot && !busy && !reading'))return;await new Promise(r=>setTimeout(r,2));}throw new Error('Popup did not settle');}
await settle();
buttons.find(b=>b.dataset.mode==='Quiet').click();
assert(value('busy'));
assert.equal(id('battery-care').disabled,false,'An unrelated care button must remain usable during the mode write');
id('battery-care').click();
buttons.find(b=>b.dataset.toggle==='autorefresh').click();
buttons.find(b=>b.dataset.toggle==='refresh').click();
assert.equal(value('queuedChanges'),4);
await settle();
assert.equal(h.mode,'Quiet');assert.equal(h.requestedChargeLimit,100);assert.equal(h.preferences.autoRefresh,true);assert.equal(h.refreshRate,60);
assert.deepEqual(requests.filter(r=>!['quick.read','window.state'].includes(r.method)).map(r=>r.method),['performance.set','battery.care','display.automatic','display.cycle']);
assert.equal(value('pendingControls.size'),0);
console.log('PASS production popup handlers: four rapid selections survive a delayed backend, unrelated controls stay usable, confirmed refresh and pending flags settle.');
h.customization.quickSystemActions=['calculator','awake'];
h.customization.quickLinks=Array.from({length:5},(_,n)=>({id:String(n),label:'Link '+n,icon:'tools',showInPanel:n<4}));
await value('refresh(true)');
assert.equal(buttons.find(b=>b.dataset.system==='touchpad').hidden,true);
assert.equal(buttons.find(b=>b.dataset.system==='calculator').hidden,false);
assert.deepEqual(buttons.filter(b=>b.dataset.system && !b.hidden).map(b=>b.dataset.system),['calculator','awake'],'Popup controls must follow the saved user order.');
assert(id('quick-tools').innerHTML.includes('Link 3'));
assert(!id('quick-tools').innerHTML.includes('Link 4'));
console.log('PASS selected system controls in saved order and four visible app links.');
document.activeElement=id('brightness');
const reads=requests.filter(r=>r.method==='quick.read').length;
hostMessage({data:{brightness:65}});
assert.equal(id('brightness').value,'65');
assert.equal(id('brightness-value').textContent,'65%');
assert.equal(requests.filter(r=>r.method==='quick.read').length,reads);
console.log('PASS confirmed keyboard brightness updates the focused popup slider without a device-state round trip.');
