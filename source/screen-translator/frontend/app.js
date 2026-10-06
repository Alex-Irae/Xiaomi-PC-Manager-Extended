/* Purpose: local WebView2 bridge, settings and observable model/device status.
 * Dependencies: native C# host. Outputs: command messages, no HTTP requests.
 * Command: pwsh -NoProfile -File ./dev.ps1 with local SDK options. */
'use strict';
const $ = id => document.getElementById(id);
const bridge = window.chrome?.webview;
let state = null, dirty = false, saveTimer = 0, past = [], future = [];
function send(action, extra = {}) { bridge?.postMessage({action, ...extra}); }
function setShortcut(chord) {
  const field=$('shortcut');
  if(![...field.options].some(option=>option.value===chord))field.add(new Option(chord,chord),field.options.length-1);
  field.value=chord;field.dataset.selected=chord;
}
function page(name) {
  document.querySelectorAll('main>section').forEach(section => {section.hidden = section.id !== name;});
  document.querySelectorAll('[data-page]').forEach(button => {const active = button.dataset.page === name;button.classList.toggle('active',active);button.setAttribute('aria-selected',String(active));});
}
document.querySelectorAll('[data-page]').forEach(button => button.addEventListener('click',() => page(button.dataset.page)));
document.querySelectorAll('[data-action]').forEach(button => button.addEventListener('click',() => send(button.dataset.action)));
$('drag').addEventListener('pointerdown',event => {if(event.button === 0)send('drag');});
$('monitor').addEventListener('change',() => send('monitor',{value:Number($('monitor').value)}));
function fill(config) {
  for(const field of ['models','interval_ms','model_idle_seconds','theme','accent','profile','font_family','display_style','screenshot_folder'])$(field).value = config[field];
  setShortcut(config.shortcut);
  $('font_scale').value = Math.round(config.font_scale * 100);
  for(const field of ['cache','incremental','debug','toolbar_focus_only','autostart','font_fit','follow_suite_appearance'])$(field).checked = config[field];
  previewText();
  dirty = false;
}
function previewText() {
  const text=$('preview-text');text.style.fontFamily=$('font_family').value;
  text.style.fontSize=`${18 * Math.max(.5,Math.min(2.5,Number($('font_scale').value)/100 || 1))}px`;
  text.dataset.style=$('display_style').value;
}
// Settings save as they change. Numbers and colours wait for a short pause in typing or dragging;
// text paths save when the field is left, so a half-typed model folder is never loaded.
$('settings-form').addEventListener('input',event => {
  dirty = true;previewText();clearTimeout(saveTimer);
  if(['number','color'].includes(event.target.type))saveTimer=setTimeout(commit,700);
});
$('settings-form').addEventListener('change',() => {dirty = true;previewText();commit();});
function collect() {
  const config = {...state.config,models:$('models').value.trim(),mode:'Managed',batch_size:8};
  config.interval_ms=Number($('interval_ms').value);config.model_idle_seconds=Number($('model_idle_seconds').value);config.font_scale=Number($('font_scale').value)/100;
  for(const field of ['cache','incremental','debug','toolbar_focus_only','autostart','font_fit','follow_suite_appearance'])config[field] = $(field).checked;
  for(const field of ['theme','accent','shortcut','profile','font_family','display_style','screenshot_folder'])config[field] = $(field).value;
  return config;
}
function historyButtons() {$('settings-undo').disabled=!past.length;$('settings-redo').disabled=!future.length;}
function apply(config) {state.config=config;fill(config);send('save',{config});historyButtons();}
function commit() {
  clearTimeout(saveTimer);
  if(!state || $('shortcut').value==='record')return;
  if(!$('settings-form').checkValidity()){$('settings-form').reportValidity();return;}
  const config=collect();
  if(JSON.stringify(config)===JSON.stringify(state.config)){dirty=false;return;}
  // Undo history lasts while this window is open; fifty steps is ample for a settings page.
  past.push(state.config);if(past.length>50)past.shift();future=[];
  apply(config);Revamp.toast('Settings saved.');
}
function step(forward) {
  const from=forward?future:past,to=forward?past:future;
  if(!state||!from.length)return;
  clearTimeout(saveTimer);to.push(state.config);apply(from.pop());Revamp.toast(forward?'Change restored.':'Change undone.');
}
Revamp.history($('settings-undo'),$('settings-redo'),step);
$('shortcut').addEventListener('change',() => {
  if($('shortcut').value==='record'){setShortcut($('shortcut').dataset.selected||state.config.shortcut);send('record-shortcut');}
  else $('shortcut').dataset.selected=$('shortcut').value;
});
$('settings-form').addEventListener('submit',event => {event.preventDefault();commit();});
function render(value) {
  state = value;
  if(!$('font_family').options.length) $('font_family').replaceChildren(...(value.fonts || ['Segoe UI']).map(name=>{const option=document.createElement('option');option.value=option.textContent=name;return option;}));
  document.documentElement.dataset.theme=value.config.theme;
  document.documentElement.style.setProperty('--accent',value.config.accent);
  // In this page --surface is the window colour and --subtle the cards.
  Revamp.palette(value.palette,{background:'--surface',surface:'--subtle'});
  $('brand-picture').hidden=!value.config.picture;$('brand-glyph').hidden=Boolean(value.config.picture);
  if(value.config.picture)$('brand-picture').src=`https://screen-translator-images.local/picture.png?v=${value.pictureVersion}`;
  document.querySelector('.dev-tag').textContent=value.version ? 'Version '+value.version : 'Development';
  if(!dirty)fill(value.config);
  $('status').textContent = value.status;$('status').classList.toggle('error',Boolean(value.lastError));
  $('engine-state').textContent = value.busy ? 'Working' : value.ready ? 'Ready' : 'Stopped';$('engine-state').classList.toggle('ready',value.ready);
  for(const id of ['screen','region'])$(id).disabled = value.busy;
  $('filter').disabled = value.busy && !value.ready;
  $('original').disabled = !value.hasTranslation;
  $('stop').disabled = !value.ready && !value.busy;
  $('reload').disabled = value.busy;$('reload').textContent = value.ready ? 'Reload models' : 'Load models';
  $('download').disabled = value.busy;$('clear').disabled = !value.ready || value.busy;$('monitor').disabled = value.busy;
  $('filter').setAttribute('aria-pressed',String(value.filter));$('filter-state').textContent = value.filter ? 'On' : 'Off';
  $('original').setAttribute('aria-pressed',String(value.original));$('original').firstChild.textContent = value.original ? 'Resume translation ' : 'Show original ';
  const deviceInfos = value.models?.ocr;
  const execution = info => info?.execution_devices?.join(', ') || 'Requested';
  const deviceLabels = [deviceInfos ? execution(deviceInfos.detector) : 'Automatic',deviceInfos ? execution(deviceInfos.recognizer) : 'Automatic',value.models?.translator?.compiled_device || 'Automatic'];
  document.querySelectorAll('#devices strong').forEach((label,i) => {label.textContent = deviceLabels[i];});
  const monitors = value.monitors || [];
  $('monitor').replaceChildren(...monitors.map(screen => {const option = document.createElement('option');option.value = screen.id;option.textContent = `${screen.primary?'Primary · ':''}${screen.width} × ${screen.height} · ${screen.name}`;return option;}));
  $('monitor').value = value.monitor;
  $('diagnostic-output').textContent = value.models || value.timing || value.lastError ? JSON.stringify({models:value.models,timing:value.timing,lastError:value.lastError || undefined},null,2) : 'No model or translation report yet.';
  $('hotkeys').textContent = value.hotkeyErrors.length ? `Unavailable shortcuts: ${value.hotkeyErrors.join(', ')}` : `${value.config.shortcut}: toggle filter · Region: ${value.suiteBindings?.["screen-translator.region"] || "Ctrl Alt T"} · Original: ${value.suiteBindings?.["screen-translator.original"] || "Ctrl Alt O"} · Dismiss: Esc`;
  $("suite-owner").textContent=value.suiteOwner ? `${value.suiteOwner} manages shortcuts` : "Standalone shortcut handling";
  $('footer-status').textContent = `${value.effectiveProfile || 'Auto'} · ${value.busy ? 'Engine working' : value.ready ? 'Models resident' : 'Stopped'}`;
}
bridge?.addEventListener('message',event => {
  const value = event.data;
  if(value.kind === "suite-settings" && state) { Object.assign(state.config, {shortcut:value.shortcut,theme:value.theme,accent:value.accent}); for(const key of ["shortcut","theme","accent"]) if(document.activeElement !== $(key)) {if(key==='shortcut')setShortcut(value[key]);else $(key).value=value[key];} }
  if(value.kind === 'state')render(value);
  if(value.kind === 'path' && ['models','shortcut','screenshot_folder'].includes(value.field)) {if(value.field==='shortcut')setShortcut(value.value);else $(value.field).value = value.value;dirty = true;commit();}
});
// A single native-host check verifies real DOM state and editable settings during loading.
window.checkDevelopmentUi = () => {
  if(!state)return {ok:false,reason:'Native state not delivered'};
  const initial = structuredClone(state);
  render({...initial,config:{...initial.config,theme:'dark',accent:'#9352d7'}});
  const appearanceApplied=document.documentElement.dataset.theme==='dark' && document.documentElement.style.getPropertyValue('--accent')==='#9352d7';
  render({...initial,ready:false,busy:true});page('settings');
  const settingsDuringLoad = !$('settings').hidden && !$('models').disabled;
  const stopDuringLoad = !$('stop').disabled && $('screen').disabled;
  $('models').value = 'C:\\local-test-models';dirty = true;
  $('shortcut').value='Ctrl+Shift+T';$('font_scale').value=150;$('display_style').value='contrast';$('font_fit').checked=false;previewText();
  render({...initial,status:'Compile progress',ready:false,busy:true});
  const editsRetained = $('models').value === 'C:\\local-test-models';
  const textAndShortcutEditsRetained = $('shortcut').value==='Ctrl+Shift+T'&&Number($('font_scale').value)===150&&$('display_style').value==='contrast'&&!$('font_fit').checked&&$('preview-text').style.fontSize==='27px'&&$('font_family').options.length>0;
  dirty = false;render({...initial,ready:true,busy:false});page('overview');
  const controlsReady = !$('screen').disabled && !$('region').disabled && !$('filter').disabled;
  render({...initial,ready:true,busy:true,filter:true,hasTranslation:true});
  const viewControlsDuringInference = !$('original').disabled && !$('filter').disabled && $('screen').disabled;
  render({...initial,ready:false,busy:false,lastError:'Simulated engine failure'});
  const reloadAfterFailure = !$('reload').disabled && !$('settings-tab').disabled && !$('screen').disabled;
  render(initial);
  return {ok:settingsDuringLoad&&stopDuringLoad&&editsRetained&&textAndShortcutEditsRetained&&controlsReady&&reloadAfterFailure&&viewControlsDuringInference&&appearanceApplied,settingsDuringLoad,stopDuringLoad,editsRetained,textAndShortcutEditsRetained,controlsReady,reloadAfterFailure,viewControlsDuringInference,appearanceApplied,screenCaptured:false,backendStarted:false};
};
if(bridge)send('state');else {$('status').textContent = 'Open through dev.ps1 to connect the native controls and local engine.';}
