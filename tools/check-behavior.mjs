// Purpose: exercise real frontend bridge, coalescing and event handlers using an explicit in-memory fixture.
// Dependencies: Node.js standard library. Outputs: PASS lines; no files/device writes, shell, network or packages.
// Command: node tools/check-behavior.mjs. This is programmatic behavior, not native/optical latency validation.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import vm from 'node:vm';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const source = file => readFileSync(path.join(root, 'www', file), 'utf8');
const decode = value => value.replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');
const listeners = new Map(), nodes = new Map(), requests = [], shortcutRows = [], shortcutItems = [], styleValues = {};
class Node {
  constructor() { this.disabled = false; this.dataset = {}; this.classList = { toggle() {} }; this.open = false; this._html = ''; }
  get innerHTML() { return this._html; }
  set innerHTML(value) { this._html = value; this.buttons = null; }
  closest(selector) { return selector === 'button' ? this : null; }
  hasAttribute() { return false; }
  setAttribute() {}
  showModal() { this.open = true; }
  close() { this.open = false; }
  addEventListener() {}
  click() { if (!this.disabled) listeners.get('click')?.({ target: this }); }
}
function element(id) { if (!nodes.has(id)) nodes.set(id, new Node()); return nodes.get(id); }
function buttons() {
  const page = element('page');
  if (page.buttons) return page.buttons;
  page.buttons = [...page.innerHTML.matchAll(/<button\b([^>]*)>/g)].map(([,attrs]) => {
    const node = new Node(); node.disabled = /\bdisabled\b/.test(attrs); node.selected = /class="[^"]*\bselected\b/.test(attrs);
    for (const [,key,value] of attrs.matchAll(/data-([a-z]+)="([^"]*)"/g)) node.dataset[key] = decode(value);
    return node;
  });
  return page.buttons;
}
const document = {
  hidden: false, activeElement: { matches: () => false }, getElementById: element,
  addEventListener: (name, fn) => listeners.set(name, fn),
  querySelectorAll: selector => selector === '.shortcut-row' ? shortcutRows : selector.includes('button') ? buttons() : [],
  querySelector: selector => selector.includes('.selected') ? buttons().find(node => node.selected && node.dataset.action === 'performance.set') : null,
  documentElement: { dataset: {} }
};
const hardware = { deviceName: 'Fixture PC', model: 'Fixture laptop', powerSource: 'Online', mode: 'Auto', brightness: 50,
  chargeLimit: 70, requestedChargeLimit: 70, batteryPercent: 50, fullWh: 60, travel: false, refreshRate: 120, refreshRates: [60,120],
  hapticsAvailable: true, vibration: 'Medium', pressure: 120, touchpadEnabled: true, touchscreenEnabled: true,
  preferences: { acMode: 'Auto', powerProfiles: true, appearance: 'light' }, customization: { quickIcons: {}, quickLinks: [] } };
const settingRows = [{ key: 'OsdPosition', group: 'notifications', label: 'Position', kind: 'choice', options: ['Top','Bottom'], value: 'Top' },
  { key: 'KeyboardRoutingEnabled', group: 'keyboard', label: 'Handle keys', kind: 'toggle', value: true },
  { key: 'AiKeyAction', group: 'keyboard', label: 'AI action', kind: 'choice', options: ['none','panel','app','page.battery'], value: 'panel' },
  { key: 'AiKeyCommand', group: 'keyboard', label: 'AI custom command', kind: 'text', value: '' }];
const keyApps = {};
let hostMessage;
const host = {
  addEventListener: (_, listener) => { hostMessage = listener; },
  postMessage({id, method, args}) {
    requests.push({ method, args });
    let data;
    if (method === 'display.brightness') { hardware.brightness = args.value; data = { brightness: args.value }; }
    else if (method === 'performance.set') { hardware.mode = args.value; hardware.preferences.acMode = args.value; data = { message: 'Fixture confirmed' }; }
    else if (method === 'window.state') data = { awake: false, isolationStatus: 'Fixture only' };
    else if (method === 'settings.read') data = { rows: settingRows, keys: { enabled: true, running: true }, startupRegistered: true, keyApps, shortcuts: shortcutItems };
    else if (method === 'settings.history') data = { canUndo: true, canRedo: false, undoLabel: 'Performance mode' };
    else if (method === 'settings.addApp') {
      hardware.customization.quickLinks.push({ id: '1234567890abcdef1234567890abcdef', label: 'Fixture chosen', path: 'C:/fixture/chosen.exe', icon: 'app' });
      data = { message: 'App link selection finished.' };
    }
    else if (method === 'settings.shortcutApp') {
      data = { id: '1234567890abcdef1234567890abcdef', label: 'Fixture chosen', path: 'C:/fixture/chosen.exe', icon: 'app' };
      hardware.customization.quickLinks.push(data);
    }
    else if (method === 'settings.shortcuts') { shortcutItems.splice(0, shortcutItems.length, ...args.shortcuts); data = {}; }
    else if (method === 'settings.customization') data = { quickLinks: [], quickIcons: {}, icons: ['settings','message','home'] };
    else if (method === 'xiaomi.components') data = { manager: null, store: null, ai: null };
    else if (method === 'settings.appearance') { Object.assign(hardware.preferences, { themePreset: args.preset, themeAccent: args.accent, themeBackground: args.background, themeSurface: args.surface, osdStyle: args.osdStyle, developerMode: args.developerMode, originalPopupEnabled: args.originalPopup }); data = {}; }
    else if (method === 'settings.paletteSave') {
      const id = 'saved:' + String((hardware.preferences.themePalettes || []).length + 1).padStart(32, '0');
      const palette = { id, name: args.name, accent: hardware.preferences.themeAccent,
        background: hardware.preferences.themeBackground, surface: hardware.preferences.themeSurface };
      hardware.preferences.themePalettes = [...(hardware.preferences.themePalettes || []), palette];
      hardware.preferences.themePreset = palette.id;
      data = { id: palette.id };
    }
    else if (method === 'settings.apply') { settingRows.find(r=>r.key===args.key).value=args.value; data={}; }
    else if (method === 'settings.keyApp') { settingRows.find(r=>r.key===args.slot+'Action').value='app'; keyApps[args.slot]={label:'Fixture app',path:'C:/fixture/app.exe'}; data={message:'Fixture app selected'}; }
    else if (method === 'quick.read' || method === 'state.read') data = { hardware: structuredClone(hardware), telemetry: { batteryWatts: 30, batteryPowerState: 5 } };
    else throw new Error('Unexpected fixture method: ' + method);
    setTimeout(() => hostMessage({ data: { id, ok: true, data } }), method === 'display.brightness' ? 3 : 0);
  }
};
const context = vm.createContext({ console, document, window: { chrome: { webview: host }, addEventListener() {} }, location: { hash: '' },
  matchMedia: () => ({ matches: false, addEventListener() {} }),
  getComputedStyle: () => ({ getPropertyValue: key => ({ '--preset-bg': document.documentElement.dataset.theme === 'dark' ? '#171a1f' : '#ffffff', '--preset-surface': document.documentElement.dataset.theme === 'dark' ? '#1e2228' : '#ffffff' })[key] || styleValues[key] || '' }),
  Option: function(label, value) { return { label, value }; },
  setTimeout: (fn, ms) => ms > 1000 ? 0 : setTimeout(fn, ms), clearTimeout, setInterval: () => 0 });
vm.runInContext(source('bridge.js'), context);
vm.runInContext(source('quick-tools.js'), context);
const evaluate = code => vm.runInContext(code, context);
const estimate = (h, t) => JSON.parse(evaluate(`JSON.stringify(UI.chargeEstimate(${JSON.stringify(h)},${JSON.stringify(t)}))`));
assert.equal(estimate(hardware, {batteryWatts:30,batteryPowerState:5}).minutes, 24); // 12 Wh remaining / 30 W.
assert.equal(estimate({...hardware,travel:true}, {batteryWatts:30,batteryPowerState:5}).minutes, 60);
assert.equal(estimate({...hardware,travel:true}, {batteryWatts:-30,batteryPowerState:5}).minutes, 60);
assert.equal(estimate({...hardware,batteryPercent:75}, {}).status, 'Target reached');
assert.equal(estimate({...hardware,powerSource:'Offline'}, {batteryWatts:30}).status, 'Connect the charger');
assert.equal(estimate(hardware, {batteryWatts:0,batteryPowerState:1}).status, 'Not charging');
assert.equal(estimate(hardware, {batteryWatts:null}).status, 'Waiting for charging power');
assert.equal(estimate({...hardware,fullWh:null}, {}).status, 'Unavailable');
console.log('PASS remaining-energy charge estimate: care, travel, battery power, zero flow and missing capacity.');
evaluate('globalThis.brightnessConfirmations = []; for (const value of [51,52,53,54,55]) BrightnessInput.set(value, n => brightnessConfirmations.push(n), error => { throw new Error(error); });');
async function settle(expression) {
  for (let i=0;i<200;i++) { if (evaluate(expression)) return; await new Promise(resolve => setTimeout(resolve, 2)); }
  throw new Error('Fixture did not settle: ' + expression);
}
await settle('!BrightnessInput.pending');
assert.deepEqual(requests.filter(r=>r.method==='display.brightness').map(r=>r.args.value), [51,55]);
assert.deepEqual(JSON.parse(evaluate('JSON.stringify(brightnessConfirmations)')), [55]);
console.log('PASS slider coalescing: first/latest writes only, latest confirmation only.');
vm.runInContext(source('app.js'), context);
await settle('!reading && snapshot != null');
assert.equal(element('undo').title, 'Undo Performance mode (Ctrl+Z)');
evaluate('showPage("performance")');
await settle('!reading');
const silent = buttons().find(node=>node.dataset.action==='performance.set' && JSON.parse(node.dataset.args).value==='Quiet');
assert(silent && !silent.disabled, 'The real mode handler must receive an enabled button.');
silent.click();
await settle('!busy && !reading');
assert.equal(hardware.mode, 'Quiet');
assert.equal(JSON.parse(document.querySelector('[data-action="performance.set"].selected').dataset.args).value, 'Quiet');
assert.equal(requests.find(r=>r.method==='performance.set').args.source, 'Online');
console.log('PASS mode button click: production handler, source-bound request and selected readback.');
evaluate('showPage("notifications")'); await settle('!reading');
assert(element('page').innerHTML.includes('value="Top" selected'), 'Saved notification position must render as selected.');
assert(!element('page').innerHTML.includes('Advanced settings'));
shortcutItems.push({ chord: 'Ctrl+Alt+K', action: 'panel' });
evaluate('showPage("keyboard")'); await settle('!reading');
assert(element('page').innerHTML.includes('value="pick-app"'), 'Each shortcut dropdown must offer an app picker.');
assert(!element('page').innerHTML.includes('data-shortcut-pick'), 'App selection must not have a competing button.');
const selectedApp = { value: 'pick-app', dataset: {committed:'panel'}, options: [], add(option) { this.options.push(option); }, matches: selector => selector === '[data-shortcut-action]' }, chord = { value: 'Ctrl+Alt+K' };
const shortcutRow = { querySelector: selector => selector === '[data-shortcut-action]' ? selectedApp : chord };
shortcutRows.push(shortcutRow);
listeners.get('change')({ target: selectedApp });
await settle('!busy && !reading && settingState?.shortcuts?.[0]?.action === "app"');
assert.equal(shortcutItems[0].appId, '1234567890abcdef1234567890abcdef');
assert(evaluate('shortcutActions().includes("app:1234567890abcdef1234567890abcdef")'), 'Selected apps must become shortcut actions.');
assert(!element('page').innerHTML.includes('Subscribed directly to Xiaomi firmware events.'), 'Everyday mode must hide key diagnostics.');
hardware.preferences.developerMode = true; evaluate('refresh(true)'); await settle('!reading');
assert(element('page').innerHTML.includes('Subscribed directly to Xiaomi firmware events.'), 'Developer mode must expose key diagnostics.');
assert(element('page').innerHTML.includes('Open Battery'));
assert(!element('page').innerHTML.includes('AI custom command'));
listeners.get('change')({target:{dataset:{setting:'AiKeyAction'},value:'app'}});
await settle('!busy && !reading');
assert.equal(requests.find(r=>r.method==='settings.keyApp').args.slot, 'AiKey');
assert(element('page').innerHTML.includes('Fixture app · C:/fixture/app.exe'));
assert(buttons().some(b=>b.dataset.action==='settings.keyApp'));
console.log('PASS key app selection uses native picker contract, selected app readback and manager page labels.');
evaluate('showPage("battery")'); await settle('!reading');
assert(element('page').innerHTML.includes('id="charge-limit"'));
const popup = source('quick.html');
assert(!popup.includes('id="charge-limit"') && popup.includes('id="travel-label"'));
console.log('PASS categorized settings, subscription status and manager-only care selector.');
document.documentElement.style = { setProperty: (key, value) => styleValues[key] = value, removeProperty: key => delete styleValues[key] };
evaluate('UI.applyPreferences({themePreset:"red",themeAccent:"#49b899"})');
assert.equal(styleValues['--blue'], '#ff615e', 'A named preset must use its actual accent, not a stale custom color.');
evaluate('showPage("settings")'); await settle('!reading');
const reorderedEditor = evaluate('(() => { customization.quickSystemActions = ["monitor", "screenoff"]; return customizationPage(); })()');
assert(reorderedEditor.indexOf('data-system-choice="monitor"') < reorderedEditor.indexOf('data-system-choice="screenoff"'));
assert(reorderedEditor.includes('data-action="system-move"') && reorderedEditor.includes('Move Monitor down'), 'Selected controls need ordering buttons.');
evaluate('customization.quickSystemActions = undefined; render()');
assert(element('page').innerHTML.includes('id="theme-surface" type="color" value="#ffffff"'), 'Named preset color picker must show the rendered light card color.');
evaluate('snapshot.hardware.preferences.appearance = "dark"; render()');
assert(element('page').innerHTML.includes('id="theme-surface" type="color" value="#1e2228"'), 'Named preset color picker must show the rendered dark card color.');
assert(element('page').innerHTML.includes('id="theme-background" type="color" value="#171a1f"'), 'Named preset background picker must show its rendered color.');
for (const [id,value] of Object.entries({'theme-preset':'pink','theme-accent':'#49b899','theme-background':'#20262e','theme-surface':'#303842','osd-style':'xiaomi'})) element(id).value=value;
element('developer-mode').checked=false; element('original-popup').checked=false;
listeners.get('change')({target:{id:'theme-preset',value:'pink',dataset:{}}});
await settle('!busy && !reading');
assert.equal(hardware.preferences.themeAccent, '#e980bc');
assert.equal(hardware.preferences.themeBackground, '#171a1f');
assert.equal(hardware.preferences.themeSurface, '#1e2228');
element('theme-accent').value='#49b899';
listeners.get('change')({target:{id:'theme-accent',value:'#49b899',dataset:{}}});
await settle('!busy && !reading');
assert.equal(styleValues['--blue'], '#49b899', JSON.stringify({ preferences:hardware.preferences, recent:requests.slice(-8) }));
assert.equal(hardware.preferences.themePreset, 'custom', 'Editing the accent must also leave the named preset.');
element('theme-background').value = '#171a1f'; element('theme-surface').value = '#20242a';
listeners.get('change')({target:{id:'theme-surface',value:'#20242a',dataset:{}}});
await settle('!busy && !reading');
assert.equal(hardware.preferences.themePreset, 'custom');
assert.equal(styleValues['--surface'], '#20242a');
assert.equal(styleValues['--bg'], '#171a1f');
element('palette-name').value = 'Midnight';
evaluate('performAction("save-palette")');
await settle('!busy && !reading');
assert.equal(hardware.preferences.themePreset, 'saved:00000000000000000000000000000001');
assert(element('page').innerHTML.includes('Midnight'), 'Saved palettes must appear in the preset picker.');
assert(element('page').innerHTML.includes('id="theme-surface" type="color" value="#20242a"'));
element('theme-background').value = '#101010';
listeners.get('change')({target:{id:'theme-background',value:'#101010',dataset:{}}});
await settle('!busy && !reading');
element('palette-name').value = 'Evening';
evaluate('performAction("save-palette")');
await settle('!busy && !reading');
assert.equal(hardware.preferences.themePalettes.length, 2, 'Two named palettes must coexist.');
element('theme-preset').value = hardware.preferences.themePalettes[0].id;
listeners.get('change')({target:{id:'theme-preset',value:hardware.preferences.themePalettes[0].id,dataset:{}}});
await settle('!busy && !reading');
assert.equal(hardware.preferences.themeBackground, '#171a1f', 'Selecting a saved palette must restore all its colors.');
assert(!element('page').innerHTML.includes('Save appearance') && !element('page').innerHTML.includes('Save panel customization'));
evaluate('change("settings.apply",{key:"OsdPosition",value:"Top"}); change("settings.apply",{key:"OsdPosition",value:"Bottom"});');
await settle('!busy && !reading');
assert.equal(settingRows[0].value, 'Bottom');
console.log('PASS true preset colors, instant custom edits, named palette save and serialized rapid edits.');
console.log('Fixture tests passed. No native device behavior or optical timing is inferred.');
