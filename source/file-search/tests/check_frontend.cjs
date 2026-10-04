/* Purpose: deterministic UI behavior checks without desktop input or a browser.
 * Dependencies: existing Node.js only. Outputs: PASS lines, no application data.
 * Command from project root: node tests/check_frontend.cjs
 * This checks DOM/event logic, not actual WebView rendering or Windows input. */
const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
class Element {
  constructor(tag='div'){this.tag=tag;this.children=[];this.events={};this.hidden=false;this.value='';this.offsetHeight=20;this.scrollHeight=100;this.scrollTop=0;this.style={setProperty(){}};this.dataset={};this.classes=new Set();this.classList={toggle:(key,on)=>on?this.classes.add(key):this.classes.delete(key)};}
  addEventListener(type,fn){(this.events[type]??=[]).push(fn);}
  emit(type,event={}){for(const fn of this.events[type]||[])fn({preventDefault(){},...event});}
  append(...children){this.children.push(...children);}
  replaceChildren(){this.children=[];}
  setAttribute(){} focus(){this.focusCalls=(this.focusCalls||0)+1;} scrollIntoView(){} setPointerCapture(){}
  querySelector(){return this.children[0]??(this.children[0]=new Element());} removeAttribute(){}
  showModal(){this.open=true;}close(){this.open=false;}
  contains(node){return node===this||this.children.some(child=>child.contains(node));}
}
function fixture(){
  const ids=Object.fromEntries(['file-type','query','categories','results','history','result-menu','menu-open','menu-reveal','menu-open-with','search-form','local-feedback','window-minimize','window-maximize','window-close','launch-search','launch-playground','profile-picture','profile-image','reset-picture','message','home','preferences'].map(key=>[key,new Element()]));
  ids['search-form'].offsetHeight=76;
  const footer=new Element(),grip=new Element(),document=new Element(),window=new Element();document.body=new Element();document.documentElement=new Element();
  document.getElementById=id=>ids[id];document.createElement=tag=>new Element(tag);
  document.querySelector=selector=>selector==='footer'?footer:selector==='[data-center-drag]'?grip:ids.query;
  document.querySelectorAll=selector=>selector==='nav button'?ids.categories.children:selector==='.result'?ids.results.children.filter(node=>node.className?.startsWith('result')):[];
  window.getSelection=()=>'';
  window.innerWidth=700;window.innerHeight=600;
  let clock=0,next=0;const timers=new Map(),stored=new Map();
  const context={document,window,console,location:{hostname:'ai-center.local'},setTimeout:(fn,ms)=>{timers.set(++next,{fn,at:clock+ms});return next;},clearTimeout:id=>timers.delete(id),requestAnimationFrame:fn=>fn(),localStorage:{getItem:key=>stored.get(key),setItem:(key,value)=>stored.set(key,value),removeItem:key=>stored.delete(key)}};
  vm.createContext(context);
  return {ids,context,window,grip,stored,advance(ms){clock+=ms;for(const [id,timer] of [...timers])if(timer.at<=clock){timers.delete(id);timer.fn();}}};
}
const flush=async()=>{for(let i=0;i<8;i++)await Promise.resolve();};
async function main(){
  const f=fixture(),settings={semantic_enabled:true,accent_color:'#3482ff',font_family:'MiSans',bar_size:'regular'},calls=[];
  f.stored.set('local-search-history-v1',JSON.stringify(['estimation-free','EFS','budget']));
  let deferred,encryptedHistory=[];
  f.context.localBridge={feedback(){},host(){},appearance(){},call:async(method,params)=>{calls.push({method,params});if(method==='local_config')return {settings};if(method==='local_history_get')return {queries:encryptedHistory};if(method==='local_history_save'){encryptedHistory=[...params.queries];return {saved:true};}if(method==='local_history_remember'){encryptedHistory=[params.query,...encryptedHistory.filter(q=>q!==params.query)];return {queries:encryptedHistory};}if(method==='local_history_remove'){encryptedHistory=encryptedHistory.filter(q=>q!==params.query);return {queries:encryptedHistory};}if(method==='local_history_clear'){encryptedHistory=[];return {queries:encryptedHistory};}if(method==='local_search'){if(params.text==='slow')return new Promise(resolve=>deferred=resolve);return {results:[{file_id:7,name:'article.pdf',file_type:32,file_name_with_highlight:'article.pdf',file_path:'C:/article.pdf',matches:params.semantic?['Semantic']:['Content']}],timing:{total_ms:1},warnings:[]};}return null;}};
  vm.runInContext(fs.readFileSync('frontend/query-filters.js','utf8'),f.context);
  vm.runInContext(fs.readFileSync('frontend/search.js','utf8'),f.context);await flush();
  assert.equal(f.ids.query.value,'');assert.equal(f.ids.history.hidden,true);
  f.ids.query.value='est';f.ids.query.emit('input');
  assert.equal(f.ids.history.hidden,false);assert.equal(f.ids.history.children[1].children[0].textContent,'estimation-free');
  f.ids.history.children[1].children[0].emit('click');await flush();
  assert(calls.some(call=>call.method==='local_search'&&call.params.semantic));
  assert(f.ids.results.children.some(node=>node.children?.some(child=>child.className==='meaning-hint')));
  f.advance(800);assert(encryptedHistory.includes('estimation-free'));assert.equal(f.stored.has('local-search-history-v1'),false);
  assert.equal(f.ids.history.hidden,false,'Exact previous query must remain visible');
  const resultButton=f.ids.results.children.find(node=>node.className?.startsWith('result'));
  resultButton.emit('contextmenu',{clientX:300,clientY:150});assert.equal(f.ids['result-menu'].hidden,false);assert.equal(f.ids['menu-open-with'].disabled,false);
  f.ids['menu-open-with'].emit('click');await flush();assert(calls.some(c=>c.method==='open_file_with'&&c.params.file_id===7&&c.params.query==='estimation-free'));assert.equal(f.ids['result-menu'].hidden,true);
  f.ids.history.children[1].children[1].emit('click');await flush();f.advance(900);await flush();assert(!encryptedHistory.includes('estimation-free'),'Removal must not resave the active query');
  console.log('PASS: exact history match, immediate action history, context-menu Open with, remove without auto-resave');
  const openedBefore=calls.filter(c=>c.method==='open_file'||c.method==='open_file_folder').length;
  f.ids.query.value='efs';f.ids.query.emit('input');f.context.document.emit('keydown',{key:'Enter'});await flush();
  f.context.document.emit('keydown',{key:'Enter',ctrlKey:true});await flush();
  assert.equal(calls.filter(c=>c.method==='open_file'||c.method==='open_file_folder').length,openedBefore);
  assert(calls.some(c=>c.method==='local_search'&&c.params.text==='efs'));
  console.log('PASS: Enter before/after results and Ctrl+Enter search without opening a document');
  const parse=f.window.SearchFilters.parse;
  for(const value of ['invoice pdf','PDF invoice','invoice .pdf','invoice *.pdf','pdf invoice pdf']){const parsed=parse(value);assert.equal(parsed.extension,'pdf');assert.equal(parsed.text,'invoice');assert.equal(parsed.error,'');}
  assert.equal(parse('pdf').text,'');assert.equal(parse('pdf').extension,'pdf');
  assert.equal(parse('invoice pdf annual').extension,'');assert.equal(parse('"pdf" invoice').extension,'');assert.equal(parse('invoice "pdf"').extension,'');assert.equal(parse('report.pdf').extension,'');
  assert.equal(parse('invoice .log').extension,'log');assert(parse('pdf invoice docx').error);
  console.log('PASS: boundary extensions, case, explicit extensions, single type, literal quotes/middle words and ambiguous types');
  async function typeQuery(value){f.ids.query.value=value;f.ids.query.emit('input');f.context.document.emit('keydown',{key:'Enter'});await flush();}
  await typeQuery('invoice pdf');assert.equal(f.ids['file-type'].value,'pdf');assert(calls.some(c=>c.method==='local_search'&&c.params.text==='invoice ext:pdf'&&c.params.history_query==='invoice pdf'));
  assert(f.ids.results.children.some(n=>n.className?.startsWith('result')));
  f.ids['file-type'].value='docx';f.ids['file-type'].emit('change');await flush();assert.equal(f.ids.query.value,'invoice');assert.equal(calls.filter(c=>c.method==='local_search').at(-1).params.text,'invoice ext:docx');assert(!f.ids.results.children.some(n=>n.className?.startsWith('result')),'Wrong extensions must not render');
  await typeQuery('budget');assert.equal(f.ids['file-type'].value,'docx','Manual filter survives query edits');
  f.ids['file-type'].value='';f.ids['file-type'].emit('change');await flush();
  await typeQuery('pdf invoice');await typeQuery('invoice');assert.equal(f.ids['file-type'].value,'','Removing an inferred type clears it');
  await typeQuery('pdf');assert.equal(calls.filter(c=>c.method==='local_search').at(-1).params.text,'ext:pdf');
  f.ids['file-type'].value='';f.ids['file-type'].emit('change');await flush();assert.equal(f.ids.query.value,'');assert.equal(f.ids.results.hidden,true);
  const requestsBefore=calls.filter(c=>c.method==='local_search').length;await typeQuery('pdf invoice docx');assert.equal(calls.filter(c=>c.method==='local_search').length,requestsBefore,'Ambiguous type must not silently run');
  console.log('PASS: automatic/manual selector, original history query, exact rendering, inferred filter removal, All types and ambiguous query');
  f.ids.query.value='slow';f.ids.query.emit('input');f.advance(180);await flush();assert(deferred);f.context.document.emit('keydown',{key:'Enter'});await flush();
  f.window.emit('local-search-dismiss');assert.equal(f.ids.query.value,'');assert.equal(f.ids.history.hidden,true);
  deferred({results:[{name:'stale'}],timing:{total_ms:1},warnings:[]});await flush();assert.equal(f.ids.results.hidden,true);assert.equal(calls.filter(c=>c.method==='open_file'||c.method==='open_file_folder').length,openedBefore);
  const focusedBefore=f.ids.query.focusCalls||0;
  f.ids.query.value='old';f.window.emit('local-search-open');await flush();assert.equal(f.ids.query.value,'');assert.equal(f.ids.history.hidden,true);
  assert((f.ids.query.focusCalls||0)>focusedBefore,'Invocation must request query input focus');
  console.log('PASS: matching history after typing, persisted queries, semantic badge, fresh reopen, stale result cancellation');
  settings.name_enabled=false;settings.content_enabled=false;
  calls.length=0;f.ids.query.value='meaning only';f.ids.query.emit('input');f.advance(180);await flush();
  assert.deepEqual(calls.filter(c=>c.method==='local_search').map(c=>c.params.semantic),[true]);
  console.log('PASS: meaning-only selection skips lexical preview');
  const b=fixture(),sent=[];let receive;
  let schemeChange;const scheme={matches:true,addEventListener:(type,fn)=>schemeChange=fn};b.window.matchMedia=()=>scheme;
  b.window.chrome={webview:{postMessage:value=>sent.push(value),addEventListener:(type,fn)=>receive=fn}};
  vm.runInContext(fs.readFileSync('frontend/bridge.js','utf8'),b.context);const bridge=b.window.localBridge;
  bridge.subscribe('register_local_status',()=>{});await flush();assert.equal(sent.length,1);
  receive({data:{kind:'ready'}});await flush();assert.equal(sent.length,2);
  const request=bridge.call('local_search',{text:'test'}).then(()=>false,error=>error.message.includes('paused'));await flush();
  receive({data:{kind:'backend_stopped'}});assert.equal(await request,true);
  const fresh=bridge.call('local_config');await flush();const id=sent.at(-1).data.id;receive({data:{id,response:{code:0,data:{settings}}}});assert.equal((await fresh).settings.semantic_enabled,true);
  console.log('PASS: worker-stop rejection, subscription replay, fresh RPC after restart');
  bridge.appearance({theme:'system'});assert.equal(b.context.document.documentElement.dataset.theme,'dark');
  scheme.matches=false;schemeChange();assert.equal(b.context.document.documentElement.dataset.theme,'light');
  bridge.appearance({theme:'dark'});schemeChange();assert.equal(b.context.document.documentElement.dataset.theme,'dark');
  bridge.appearance({theme:'light'});assert.equal(b.context.document.documentElement.dataset.theme,'light');
  console.log('PASS: explicit light/dark and Windows color-scheme change');
  const c=fixture(),actions=[];c.context.localBridge={call:async()=>({image:null}),host:action=>actions.push(action)};
  vm.runInContext(fs.readFileSync('frontend/center.js','utf8'),c.context);
  c.ids['window-maximize'].emit('click');c.context.document.emit('keydown',{key:'F11'});c.window.emit('window-state-changed',{detail:{maximized:false,fullScreen:true}});c.context.document.emit('keydown',{key:'Escape'});
  assert.deepEqual(actions,['maximize','fullscreen','fullscreen']);assert(c.context.document.body.classes.has('expanded'));
  console.log('PASS: maximize command, F11 fullscreen, Escape restore');
  const d=fixture(),settingsCalls=[];
  const fields=['excluded_extensions','preferred_device','shortcut','indexing_mode','indexing_frequency','name_enabled','content_enabled','semantic_enabled','theme','index_protection','accent_color','font_family','bar_size','message','status','scan-pc','settings-form','scan','reset-index','pause-index','reset-dialog','cancel-reset','confirm-reset','settings-undo','settings-redo','paths-include','paths-exclude','paths-summary','path-input','path-add','path-manage','paths-dialog','paths-title','paths-rows','paths-new','paths-undo','paths-redo','paths-save','paths-cancel','paths-error'];
  for(const key of fields)d.ids[key]=new Element();
  for(const key of ['suite-manager','suite-owner','follow_suite_appearance','model_standby','index-location','index-storage','embedding-model-location','index-folder','backup-index','index-backup-status'])d.ids[key]=new Element();
  for(const key of ['name_enabled','content_enabled','semantic_enabled'])d.ids[key].type='checkbox';
  const controls={roots:['C:/Corpus'],excluded_folders:[],excluded_extensions:['.ini','.dll'],preferred_device:'auto',shortcut:'none',indexing_mode:'paused',indexing_frequency:'daily',name_enabled:true,content_enabled:true,semantic_enabled:true,theme:'system',index_protection:'windows',accent_color:'#3482ff',font_family:'MiSans',bar_size:'comfortable'};
  let mode='paused',onStatus,backup={active:false,phase:''},cancelBackup=false;const state=()=>({counts:{files:2,folders:1,vectors:3},indexer:{mode,busy:false},model:{device:null},backup});
  const previews=[];
  d.context.localBridge={appearance:s=>previews.push(s.theme),native:new Element(),subscribe:(method,fn)=>{onStatus=fn;fn(state());},call:async(method,params)=>{settingsCalls.push({method,params});if(method==='local_index_info')return {path:'C:/Data/index.sqlite3.dpapi',bytes:2147483648,saved:'2026-10-04T00:00:00Z',model_path:'C:/Models/qwen'};if(method==='local_backup_index'){if(cancelBackup)return {cancelled:true};backup={active:true,phase:'Copying encrypted index'};return {accepted:true};}if(method==='local_config')return {settings:controls};if(method==='local_drives')return {roots:['C:/']};if(method==='local_save_config'){Object.assign(controls,params);mode=controls.indexing_mode;onStatus(state());return {saved:true};}return state();}};
  vm.runInContext(fs.readFileSync('frontend/settings.js','utf8'),d.context);await flush();
  assert.equal(d.ids['paths-summary'].children.length,2);
  d.ids['paths-exclude'].emit('click');for(const path of ['C:/One','C:/Two','C:/Three','C:/Four','C:/Five']){d.ids['path-input'].value=path;d.ids['path-add'].emit('click');await flush();}
  assert.equal(d.ids['paths-summary'].children.filter(n=>n.className==='scope-path').length,4);
  d.ids['paths-summary'].children.at(-1).emit('click');assert.equal(d.ids['paths-summary'].children.filter(n=>n.className==='scope-path').length,5);
  d.ids['paths-summary'].children.at(-1).emit('click');assert.equal(d.ids['paths-summary'].children.filter(n=>n.className==='scope-path').length,4);
  d.ids['path-manage'].emit('click');d.ids['paths-rows'].children[4].children[1].emit('click');d.ids['paths-save'].emit('click');await flush();assert(!d.ids['paths-summary'].children.some(n=>n.className==='paths-more'));
  d.ids['path-manage'].emit('click');while(d.ids['paths-rows'].children.length)d.ids['paths-rows'].children[0].children[1].emit('click');d.ids['paths-save'].emit('click');await flush();d.ids['paths-include'].emit('click');
  console.log('PASS: one scope path per row, four-row cap, conditional expansion, independent include/exclude lists');
  d.ids.theme.value='dark';d.ids.theme.emit('change');assert.equal(previews.at(-1),'dark');await flush();assert.equal(controls.theme,'dark');
  d.ids['settings-undo'].emit('click');await flush();assert.equal(controls.theme,'system');d.ids['settings-redo'].emit('click');await flush();assert.equal(controls.theme,'dark');
  assert.equal(d.ids['pause-index'].textContent,'Resume');
  d.ids.content_enabled.checked=false;d.ids.content_enabled.emit('change');await flush();assert.equal(controls.content_enabled,false);
  d.ids.name_enabled.checked=false;d.ids.name_enabled.emit('change');await flush();d.ids.semantic_enabled.checked=false;d.ids.semantic_enabled.emit('change');await flush();assert(d.ids.message.textContent.includes('at least one'));assert.equal(controls.semantic_enabled,true);
  d.ids['paths-exclude'].emit('click');d.ids['path-input'].value='C:/Excluded';d.ids['path-add'].emit('click');await flush();assert.equal(controls.excluded_folders[0],'C:/Excluded');
  d.ids['path-manage'].emit('click');assert.equal(d.ids['paths-dialog'].open,true);d.ids['paths-rows'].children[0].children[1].emit('click');assert.equal(d.ids['paths-rows'].children.length,0);assert.equal(controls.excluded_folders.length,1);
  d.ids['paths-undo'].emit('click');assert.equal(d.ids['paths-rows'].children.length,1);d.ids['paths-redo'].emit('click');assert.equal(d.ids['paths-rows'].children.length,0);d.ids['paths-save'].emit('click');await flush();assert.equal(controls.excluded_folders.length,0);assert.equal(d.ids['paths-dialog'].open,false);
  const indexBefore=settingsCalls.filter(c=>c.method==='local_index_now').length;
  d.ids['scan-pc'].emit('click');await flush();assert.equal(controls.roots[0],'C:/');assert.equal(settingsCalls.filter(c=>c.method==='local_index_now').length,indexBefore);
  d.ids.scan.emit('click');await flush();assert(settingsCalls.some(c=>c.method==='local_index_now'&&c.params.force&&c.params.wait===false));
  d.ids['pause-index'].emit('click');await flush();assert.equal(mode,'normal');
  d.ids['reset-index'].emit('click');assert.equal(d.ids['reset-dialog'].open,true);d.ids['cancel-reset'].emit('click');assert.equal(d.ids['reset-dialog'].open,false);assert(!settingsCalls.some(c=>c.method==='local_reset_index'));
  d.ids['reset-index'].emit('click');d.ids['confirm-reset'].emit('click');await flush();assert(settingsCalls.some(c=>c.method==='local_reset_index'&&c.params.wait===false));
  console.log('PASS: immediate settings, undo/redo, channel validation, transactional paths editor, PC roots only, asynchronous index, pause and reset confirmation');
  assert.equal(d.ids['index-location'].textContent,'C:/Data/index.sqlite3.dpapi');assert(d.ids['index-storage'].textContent.includes('2.00 GiB'));assert.equal(d.ids['embedding-model-location'].textContent,'C:/Models/qwen');
  d.ids['index-folder'].emit('click');await flush();assert(settingsCalls.some(c=>c.method==='local_index_folder'));
  d.ids['backup-index'].emit('click');await flush();assert.equal(d.ids['backup-index'].disabled,true);assert(d.ids['index-backup-status'].textContent.includes('Copying'));
  backup={active:false,phase:'Complete',path:'E:/Backup/AI-Center-Index-test',bytes:2147483648};onStatus(state());await flush();assert.equal(d.ids['backup-index'].disabled,false);assert(d.ids['index-backup-status'].textContent.includes('Backup complete'));
  cancelBackup=true;d.ids['backup-index'].emit('click');await flush();assert.equal(d.ids['backup-index'].disabled,false);assert.equal(d.ids['index-backup-status'].textContent,'Backup cancelled.');
  backup={active:false,phase:'Failed',error:'Disk full'};onStatus(state());assert(d.ids['index-backup-status'].textContent.includes('Disk full'));assert.equal(d.ids['index-backup-status'].className,'error');
  console.log('PASS: index/model locations, GiB size, asynchronous backup progress, completion, cancel and failure feedback');

}
main().catch(error=>{console.error(error);process.exitCode=1;});
