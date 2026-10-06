/* Purpose: suite app launchers, shared bindings and optional shared appearance.
 * Dependencies: app.js and its native bridge. Outputs: suite settings. Run: PCManager.exe --manager. */
let suiteState;
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
  // A connected app's shortcut saves like every other row, as soon as a key is chosen.
  node.dataset.committed=node.value;
  return performAction('suite-shortcut',{action:node.dataset.suiteAction});
}
// Connected apps' shortcuts sit in the same list as PC Manager's own. The accent frame marks the rows
// those apps read too; a row exists only while its app is installed.
function suiteShortcutRows() {
  const items = suiteState?.enabled ? suiteState.bindings || [] : [];
  if (!items.length) return '';
  return `<div class="suite-rows suite-shortcut-list"><p class="suite-caption shortcut-intro">Connected apps. These shortcuts also show in Screen Translator and File Search, and keep working when PC Manager is closed.</p>${items.map(item => `<div class="fixed-key-row">${shortcutPicker(item.chord,'suite-'+item.action.replaceAll('.','-'),item.action)}<span class="field-input copilot-chord">${UI.escape(suiteLabel(item.action))} · ${item.action.startsWith('file-search') ? 'File Search' : 'Screen Translator'}</span><span class="neutral-link fixed-key-spacer" aria-hidden="true">Remove</span></div>`).join('')}${suiteState.error ? `<p class="error" role="status">${UI.escape(suiteState.error)}</p>` : ''}</div>`;
}
function suiteToolsCard() {
  if (!suiteState?.enabled) return '';
  return (suiteState.components || []).map(item => card(item.component === 'file-search' ? 'Semantic File Search' : 'Screen Translator', `<p class="inline-note">${UI.escape(item.installed ? `${item.state} · Shortcuts: ${item.owner}` : 'Optional component is not installed. Add it with Xiaomi Revamp Setup.')}</p>${item.error ? `<p class="error">${UI.escape(item.error)}</p>` : ''}${item.installed ? `<div class="actions">${button('Open', 'suite.open', { action: `${item.component}.${item.component === 'file-search' ? 'open' : 'screen'}` })}${button('Settings', 'suite.open', { action: `${item.component}.settings` })}</div>` : ''}`, item.component === 'file-search' ? 'search' : 'translate')).join('') + card('Shared appearance', row('Share theme and accent', 'Applications can opt out in their own settings.', `<label class="MiToggle"><input type="checkbox" id="suite-appearance" ${suiteState.sharedAppearance ? 'checked' : ''}><span class="toggle-track"></span></label>${button('Apply', 'suite-appearance-save')}`), 'settings');
}
