/* Purpose: English library/settings UI backed exclusively by the local service.
 * Dependencies: bridge.js and pywebview. Outputs: search results and saved config.
 * Launch: python -m xiaomi_search desktop.
 */
(() => {
  const $ = selector => document.querySelector(selector);
  let results = [], selected = -1, requestedType, generation = 0, navigated = false;
  const notice = (text, error = false) => {
    const element = $('#notice');
    element.hidden = false; element.textContent = text; element.classList.toggle('error', error);
  };
  const rpc = (method, params) => window.localBridge.call(method, params);
  const safe = action => async event => {
    try { await action(event); } catch (error) { notice(error.message, true); }
  };
  function panel(name) {
    for (const element of document.querySelectorAll('.panel')) element.hidden = element.id !== `${name}-panel`;
    for (const button of document.querySelectorAll('.nav-item')) button.classList.toggle('active', button.dataset.panel === name);
    $('#page-label').textContent = { search: 'Search library', index: 'Index & folders', settings: 'Settings' }[name];
  }
  for (const button of document.querySelectorAll('.nav-item')) button.onclick = () => panel(button.dataset.panel);
  $('#setup-link').onclick = () => panel('settings');
  $('#edit-folders').onclick = () => panel('settings');
  $('#floating').onclick = safe(() => rpc('local_show_search'));

  function select(index) {
    selected = Math.max(0, Math.min(results.length - 1, index));
    for (const element of document.querySelectorAll('.result')) element.setAttribute('aria-selected', String(Number(element.dataset.index) === selected));
    const active = document.querySelector(`.result[data-index="${selected}"]`);
    active?.scrollIntoView({ block: 'nearest' });
  }
  async function fileAction(method) {
    const result = results[selected];
    if (!result) return;
    if (method === 'copy') await rpc('copy_file_path_to_clipboard', { file_id: result.file_id });
    else {
      const response = await rpc(method, { file_id: result.file_id });
      if (response.status !== 0) notice('The file no longer exists. Rescan its folder.', true);
    }
  }
  function render(data) {
    results = data.results; selected = -1; navigated = false;
    const container = $('#results'); container.replaceChildren();
    $('#results-title').textContent = `${results.length} matching file${results.length === 1 ? '' : 's'}`;
    $('#search-timing').textContent = `${Math.round(data.timing.total_ms)} ms · local retrieval`;
    if (data.warnings.length) notice(data.warnings.join(' '), true);
    if (!results.length) {
      const empty = document.createElement('div'); empty.className = 'empty';
      const title = document.createElement('h3'); title.textContent = 'No matching files yet';
      const hint = document.createElement('p'); hint.textContent = 'Try another description or inspect your index status.';
      empty.append(title, hint); container.append(empty);
    }
    results.forEach((result, index) => {
      const row = document.createElement('div'); row.className = 'result'; row.dataset.index = index; row.tabIndex = 0;
      row.setAttribute('role', 'option'); row.setAttribute('aria-selected', 'false');
      const extension = result.name.split('.').pop().toLowerCase();
      const code = ['py', 'js', 'ts', 'tex', 'cpp', 'c', 'h', 'json', 'yaml', 'toml'].includes(extension);
      const icon = document.createElement('span'); icon.className = `file-icon ${code ? 'code' : extension === 'pdf' ? '' : 'doc'}`; icon.textContent = code ? '{ }' : extension.toUpperCase().slice(0, 5);
      const content = document.createElement('div'); content.className = 'result-content';
      const name = document.createElement('div'); name.className = 'result-name';
      const title = document.createElement('span'); title.textContent = result.name;
      const badge = document.createElement('span'); badge.className = 'match-label'; badge.textContent = result.matches.join(' + ');
      name.append(title, badge);
      const path = document.createElement('div'); path.className = 'result-path'; path.textContent = result.file_path; path.title = result.file_path;
      content.append(name, path);
      if (result.snippet) { const snippet = document.createElement('p'); snippet.className = 'snippet'; snippet.textContent = result.snippet; content.append(snippet); }
      if (result.location) { const location = document.createElement('div'); location.className = 'location'; location.textContent = result.location; content.append(location); }
      row.append(icon, content); row.onclick = () => select(index);
      row.ondblclick = safe(async () => { select(index); await fileAction('open_file'); });
      container.append(row);
    });
  }
  async function search(event) {
    event?.preventDefault();
    const thisGeneration = ++generation;
    const text = $('#query').value.trim();
    const params = { text, semantic: false };
    if (requestedType) params.file_type = requestedType;
    $('#notice').hidden = true;
    $('#search-timing').textContent = 'Searching filenames and content...';
    const lexical = await rpc('local_search', params);
    if (thisGeneration !== generation) return;
    render(lexical);
    if ($('#semantic').checked && text) {
      $('#search-timing').textContent = 'Filename results ready · searching meaning...';
      const hybrid = await rpc('local_search', { ...params, semantic: true });
      if (thisGeneration === generation) render(hybrid);
    }
  }
  $('#search-form').onsubmit = safe(search);
  $('#query').oninput = () => { navigated = false; ++generation; };
  $('#semantic').onchange = safe(async () => {
    status(await rpc('local_semantic', { enabled: $('#semantic').checked }));
    if ($('#query').value.trim()) await search();
  });
  for (const filter of document.querySelectorAll('.filter')) filter.onclick = safe(async () => {
    requestedType = filter.dataset.type ? Number(filter.dataset.type) : undefined;
    for (const button of document.querySelectorAll('.filter')) button.classList.toggle('active', button === filter);
    await search();
  });
  document.addEventListener('keydown', safe(async event => {
    if ($('#search-panel').hidden || !results.length) return;
    if (['ArrowDown', 'ArrowUp'].includes(event.key)) { event.preventDefault(); select(selected + (event.key === 'ArrowDown' ? 1 : -1)); navigated = true; }
    else if (event.key === 'Enter' && selected >= 0 && (event.ctrlKey || navigated || !event.target.matches('input'))) { event.preventDefault(); await fileAction(event.ctrlKey ? 'open_file_folder' : 'open_file'); }
    else if (event.ctrlKey && event.key.toLowerCase() === 'c' && selected >= 0 && !window.getSelection()?.toString() && !(event.target.matches('input') && event.target.selectionStart !== event.target.selectionEnd)) { event.preventDefault(); await fileAction('copy'); }
  }), true);

  function status(data) {
    $('#count-files').textContent = data.counts.files.toLocaleString();
    $('#count-chunks').textContent = data.counts.chunks.toLocaleString();
    $('#count-vectors').textContent = data.counts.vectors.toLocaleString();
    $('#index-state').textContent = data.indexer.mode === 'paused' ? 'Paused' : data.indexer.busy ? 'Indexing' : data.indexer.queued_embedding_files ? 'Embedding queued' : 'Idle';
    $('#index-detail').textContent = data.indexer.semantic_error || data.indexer.scan_error || `${data.indexer.queued_files} files queued · ${data.indexer.queued_embedding_files} files awaiting embeddings${data.indexer.current ? ' · ' + data.indexer.current : ''}`;
    $('#roots-list').replaceChildren();
    if (!data.roots.length) $('#roots-list').textContent = 'No folders selected. Save your selection in Settings and restart.';
    for (const path of data.roots) { const row = document.createElement('div'); row.className = 'folder-item'; row.textContent = path; $('#roots-list').append(row); }
    $('#model-state').textContent = data.model.error || (data.model.loaded ? `Active on ${data.model.device}. Will release after idle.` : data.model.available ? 'Local model found. Loaded only when indexing or searching meaning.' : 'No local model found. Check the model directory. Filename and content search still work.');
    $('#shortcut-label').textContent = data.shortcut === 'double_ctrl' ? 'Ctrl + Ctrl' : 'Alt + Space';
  }
  $('#scan').onclick = safe(async () => status(await rpc('local_scan')));
  $('#pause').onclick = safe(async () => status(await rpc('local_mode', { mode: 'paused' })));
  $('#resume').onclick = safe(async () => status(await rpc('local_mode', { mode: 'battery_saver' })));
  $('#release-model').onclick = safe(async () => status(await rpc('local_model_release')));
  $('#browse').onclick = safe(async () => {
    const path = await rpc('local_choose_folder');
    if (path) $('#roots').value = [$('#roots').value.trim(), path].filter(Boolean).join('\n');
  });
  $('#refresh-errors').onclick = safe(async () => {
    const errors = await rpc('local_errors');
    const list = $('#errors-list'); list.replaceChildren();
    if (!errors.length) list.textContent = 'No recorded extraction errors.';
    for (const error of errors) { const row = document.createElement('div'); row.className = 'folder-item'; row.textContent = `${error.path}: ${error.error}`; list.append(row); }
  });
  $('#settings-form').onsubmit = safe(async event => {
    event.preventDefault();
    await rpc('local_save_config', { roots: $('#roots').value.split('\n').map(value => value.trim()).filter(Boolean), model_path: $('#model-path').value.trim(), devices: $('#devices').value.split(','), shortcut: $('#shortcut').value, indexing_mode: $('#mode').value });
    notice('Settings saved. Quit resident search and launch it again to apply them.');
  });
  $('#quit').onclick = safe(() => rpc('local_quit'));
  window.addEventListener('pywebviewready', safe(async () => {
    status(await rpc('local_status'));
    const config = await rpc('local_config');
    $('#roots').value = config.settings.roots.join('\n');
    $('#model-path').value = config.settings.model_path;
    $('#devices').value = config.settings.devices.join(',');
    $('#shortcut').value = config.settings.shortcut;
    $('#mode').value = config.settings.indexing_mode;
    $('#semantic').checked = config.settings.semantic_enabled;
    $('#config-location').textContent = `Configuration: ${config.path}`;
    window.localBridge.subscribe('register_local_status', status);
  }));
  // A plain browser has no access to the machine or any fake search data.
  if (!window.pywebview) $('#model-state').textContent = 'Launch the desktop app to connect to your local index.';
})();
