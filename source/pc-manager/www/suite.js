/* Purpose: suite app launchers, shared bindings and optional shared appearance.
 * Dependencies: app.js and its native bridge. Outputs: suite settings. Run: PCManager.exe --manager. */
let suiteState;
const shortcutSaved={};
const suiteLabel = action => ({
  'file-search.open': 'Open File Search', 'screen-translator.toggle': 'Toggle translation',
  'screen-translator.screen': 'Translate screen', 'screen-translator.region': 'Translate region',
  'screen-translator.original': 'Original / translated', 'screen-translator.filter': 'Translation filter'
}[action] || action);
const shortcutPresets = [['none','Disabled'],['double_ctrl','Double Ctrl'],['Copilot','Copilot'],
  ...['Ctrl+Alt+F','Ctrl+Alt+T','Ctrl+Alt+O','Ctrl+Alt+D','Ctrl+Alt+K','Ctrl+Alt+S','Ctrl+Shift+T','Ctrl+Alt+Space','Alt+Space','F8'].map(chord=>[chord,chord])];
function shortcutPicker(chord, id='', action='') {
  const choices = [...shortcutPresets];
  if (!chord) choices.unshift(['','Choose a shortcut']);
  else if (!choices.some(([value])=>value===chord)) choices.push([chord,chord]);
  choices.push(['record','Press a shortcut…']);
  return `<select class="field-select shortcut-picker" data-chord data-committed="${UI.escape(chord)}" ${id?`id="${id}"`:''} ${action?`data-suite-action="${UI.escape(action)}"`:''} aria-label="${UI.escape(action?suiteLabel(action):'Custom action')} shortcut">${options(choices,chord)}</select>`;
}
async function chooseShortcut(node) {
  const previous=node.dataset.committed || '';
  const wasDirty=dirty;
  if (node.value==='record') {
    dirty=true;
    try {
      const result=await Native.call('settings.recordShortcut',{chord:previous || 'none'});
      if(result.cancelled) { node.value=previous;dirty=wasDirty; return; }
      if(!Array.from(node.options).some(option=>option.value===result.chord)) node.add(new Option(result.chord,result.chord),node.options.length-1);
      node.value=result.chord;
    } catch(error) {node.value=previous;dirty=wasDirty;notice(error.message,true);return;}
  }
  dirty=true;
  if(!node.dataset.suiteAction) {
    if(node.value && node.value!=='none')
      document.querySelectorAll('.shortcut-row [data-chord]').forEach(other=> {if(other!==node && other.value===node.value) {const replacement=previous || 'none';if(!Array.from(other.options).some(option=>option.value===replacement))other.add(new Option(replacement,replacement),other.options.length-1);other.value=replacement;other.dataset.committed=other.value;}});
    node.dataset.committed=node.value;
    return performAction('shortcut-save');
  }
    delete shortcutSaved[node.dataset.suiteAction];
  const save=node.closest('.MiSettingRow').querySelector('[data-action="suite-shortcut"]');
  if(save){save.textContent='Save';save.removeAttribute('data-saved');}
}
function suiteKeyboardCard() {
  if (!suiteState?.enabled) return '';
  return card('Connected app shortcuts', `<p class="inline-note shortcut-intro">Choose a shortcut or select Press a shortcut to record your keys. Saving an assigned shortcut swaps the two actions' keys automatically. Each app handles its shortcuts when PC Manager is closed.</p><div class="suite-shortcut-list">${(suiteState.bindings || []).map(item => {const saved=shortcutSaved[item.action];const label=saved?.chord===item.chord?(saved.active?'Saved ✓':'Saved · inactive'):'Save';return row(suiteLabel(item.action), item.action.startsWith('file-search') ? 'Semantic File Search' : 'Screen Translator', `${shortcutPicker(item.chord,'suite-'+item.action.replaceAll('.','-'),item.action)}${button(label, 'suite-shortcut', { action: item.action })}`);}).join('')}</div>${suiteState.error ? `<p class="error" role="status">${UI.escape(suiteState.error)}</p>` : ''}`, 'keyboard');
}
function suiteToolsCard() {
  if (!suiteState?.enabled) return '';
  return (suiteState.components || []).map(item => card(item.component === 'file-search' ? 'Semantic File Search' : 'Screen Translator', `<p class="inline-note">${UI.escape(item.installed ? `${item.state} · Shortcuts: ${item.owner}` : 'Optional component is not installed. Add it with Xiaomi Revamp Setup.')}</p>${item.error ? `<p class="error">${UI.escape(item.error)}</p>` : ''}${item.installed ? `<div class="actions">${button('Open', 'suite.open', { action: `${item.component}.${item.component === 'file-search' ? 'open' : 'screen'}` })}${button('Settings', 'suite.open', { action: `${item.component}.settings` })}</div>` : ''}`, item.component === 'file-search' ? 'search' : 'translate')).join('') + card('Shared appearance', row('Share theme and accent', 'Applications can opt out in their own settings.', `<label class="MiToggle"><input type="checkbox" id="suite-appearance" ${suiteState.sharedAppearance ? 'checked' : ''}><span class="toggle-track"></span></label>${button('Apply', 'suite-appearance-save')}`), 'settings');
}
