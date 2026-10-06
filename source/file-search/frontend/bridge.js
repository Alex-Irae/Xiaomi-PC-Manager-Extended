/* Purpose: adapt Xiaomi's WebView RPC to the local Python service.
 * Dependencies: native WebView2 transport; legacy pywebview fallback preserved.
 * Outputs: UI results/status. Launch: Launch Search.cmd or Launch AI Center.cmd.
 */
(() => {
  const desktopTransport = location.hostname === 'ai-center.local' ? window.chrome?.webview : null;
  const listeners = new Set();
  const waiting = new Map();
  const subscriptions = new Map();
  let readyResolve;
  let navigated = false;
  const ready = new Promise(resolve => { readyResolve = resolve; });
  const receive = message => {
    if(message.kind==='windows_theme'){windowsDark=message.dark;suitePalette={background:message.background,surface:message.surface};appearance(currentAppearance);return;}
    if(message.kind==='ready')for(const envelope of subscriptions.values())send(envelope);
    if(message.kind==='backend_stopped'){
      for(const callback of listeners)callback({data:message});
      for(const pending of waiting.values()){clearTimeout(pending.timer);pending.reject(new Error('Search paused. Type to resume.'));}
      waiting.clear();return;
    }
    for (const callback of listeners) callback({ data: message });
    const pending = waiting.get(message.id);
    if (pending) {
      waiting.delete(message.id);
      clearTimeout(pending.timer);
      const response = message.response || {};
      response.code === 0 ? pending.resolve(response.data) : pending.reject(new Error(response.message || 'Local request failed'));
    }
    const data = message.response?.data;
    if (data?.local_timing) feedback(data.local_warnings?.[0] || `Local search · ${data.local_timing.total_ms} ms`, Boolean(data.local_warnings?.length));
  };
  async function send(message) {
    let readyTimer;
    const timeout = new Promise((_, reject) => {readyTimer=setTimeout(() => reject(new Error('Desktop bridge is unavailable. Use Launch Search.cmd.')), 8000);});
    try {
      await Promise.race([ready, timeout]);
      if (desktopTransport) desktopTransport.postMessage(message);
      else receive(await window.pywebview.api.rpc(message));
    } catch (error) {
      receive({ id: message.data.id, response: { code: 1, message: error.message } });
      feedback(error.message, true);
    }finally{clearTimeout(readyTimer);}
  }
  function call(method, params = {}, timeoutMs = 120000) {
    const id = Math.floor(Math.random() * Number.MAX_SAFE_INTEGER);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { waiting.delete(id); reject(new Error('Request timed out. Indexing and cold model compilation may take longer.')); }, timeoutMs);
      waiting.set(id, { resolve, reject, timer });
      send({ mode: 0, data: { id, persistent: false, request: { method, params } } });
    });
  }
  function subscribe(method, callback) {
    const id = Math.floor(Math.random() * Number.MAX_SAFE_INTEGER);
    const listener = event => {
      if (event.data.id === id && event.data.response?.code === 0) callback(event.data.response.data);
    };
    listeners.add(listener);
    const envelope={ mode: 0, data: { id, persistent: true, request: { method, params: {} } } };
    subscriptions.set(id,envelope);send(envelope);
    return () => { listeners.delete(listener);subscriptions.delete(id);send({ mode: 1, data: { id } }); };
  }
  function feedback(text, error = false) {
    const label = document.getElementById('local-feedback');
    if (label) { label.textContent = text; label.classList.toggle('error', error); label.title = text; }
  }
  // Xiaomi calls chrome.webview directly, before pywebviewready fires. A queue
  // keeps the original React application and persistent subscription protocol.
  window.chrome = window.chrome || {};
  const native = {
    addEventListener: (type, callback) => { if (type === 'message') listeners.add(callback); },
    removeEventListener: (type, callback) => listeners.delete(callback),
    postMessage: send
  };
  // This dedicated frontend uses our shim; pywebview's own injected transport
  // must retain the native WebView postMessage function it captured separately.
  const host = (action, params={}) => desktopTransport ? desktopTransport.postMessage({host:action,...params}) : call('local_'+action);
  let currentAppearance={theme:'system'};
  let windowsDark;
  let suitePalette=null;
  const colorScheme=window.matchMedia?.('(prefers-color-scheme: dark)');
  function appearance(settings){
    currentAppearance=settings;
    const dark=settings.theme==='dark'||(settings.theme==='system'&&(windowsDark??Boolean(colorScheme?.matches)));
    document.documentElement.dataset.theme=dark?'dark':'light';
    document.documentElement.style.setProperty('--accent',settings.accent_color||'#3482ff');
    document.documentElement.style.setProperty('--font',settings.font_family||'MiSans');
    // In these pages --surface is the window colour and --subtle the cards.
    if(typeof Revamp!=='undefined')Revamp.palette(suitePalette,{background:'--surface',surface:'--subtle'});
    host('theme',{dark});
  }
  colorScheme?.addEventListener('change',()=>appearance(currentAppearance));
  window.addEventListener('settings-changed',event=>appearance(event.detail));
  window.localBridge = { receive, call, subscribe, feedback, native, host, appearance };
  if (desktopTransport) { desktopTransport.addEventListener('message', event => receive(event.data)); readyResolve(); }
  window.addEventListener('pywebviewready', () => readyResolve());
  if (window.pywebview?.api?.rpc) readyResolve();
  document.addEventListener('keydown', event => {
    if (!document.body.dataset.xiaomi) return;
    const state = window.__xiaomiSearchState?.getState();
    if (event.key === 'Escape') {
      event.preventDefault(); event.stopImmediatePropagation(); call('local_hide').catch(error => feedback(error.message, true));
      return;
    }
    if (event.ctrlKey && event.key === ',') {
      event.preventDefault(); call('local_manage').catch(error => feedback(error.message, true)); return;
    }
    if (!state) return;
    const route = location.hash.replace(/^#/, '').split('?')[0];
    const type = { '/detail/docs': 124, '/detail/image': 256, '/detail/audio': 128, '/detail/video': 512, '/detail/zip': 1024, '/detail/other': 1 }[route];
    const rows = type ? (state.typeListData[type]?.list || []) : [...state.allSearchData.best_match, ...state.allSearchData.file_list.flatMap(group => group.files)];
    const unique = [...new Map(rows.map(row => [row.file_id, row])).values()];
    const active = state.onActiveInfo;
    if (['ArrowDown', 'ArrowUp'].includes(event.key) && unique.length && !event.ctrlKey) {
      event.preventDefault(); event.stopImmediatePropagation();
      let index = unique.findIndex(row => row.id === active?.id);
      index = Math.max(0, Math.min(unique.length - 1, index + (event.key === 'ArrowDown' ? 1 : -1)));
      state.setOnActiveInfo(unique[index]);
      navigated = true;
    } else if (event.key === 'Enter' && active && (event.ctrlKey || navigated || !event.target.matches('input'))) {
      event.preventDefault(); event.stopImmediatePropagation();
      call(event.ctrlKey ? 'open_file_folder' : 'open_file', { file_id: active.file_id }).catch(error => feedback(error.message, true));
    } else if (event.ctrlKey && event.key.toLowerCase() === 'c' && active && !window.getSelection()?.toString() && !(event.target.matches('input') && event.target.selectionStart !== event.target.selectionEnd)) {
      event.preventDefault(); call('copy_file_path_to_clipboard', { file_id: active.file_id }).catch(error => feedback(error.message, true));
    }
  }, true);
  document.addEventListener('input', () => { navigated = false; });
  window.addEventListener('local-focus', () => document.querySelector('input[type=text]')?.focus());
})();
