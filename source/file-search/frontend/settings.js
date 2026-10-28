/* Purpose: immediate settings with undo/redo and transactional folder editor.
 * Dependencies: bridge.js. Outputs: config and one-path-per-line scope files.
 * Launch from project root: Launch AI Center.cmd, then Search settings. */
(() => {
  const $=id=>document.getElementById(id),call=(...args)=>localBridge.call(...args);
  const lists=['excluded_extensions','content_only_extensions','name_only_extensions','run_extensions'],keys=[...lists,'model_standby','preferred_device','shortcut','follow_suite_appearance','run_at_startup','center_at_startup','indexing_mode','indexing_load','indexing_frequency','name_enabled','content_enabled','semantic_enabled','windows_semantic_enabled','programs_enabled','theme','index_protection','accent_color','font_family','bar_size'].filter(key=>$(key));
  const copy=value=>JSON.parse(JSON.stringify(value)),same=(a,b)=>JSON.stringify(a)===JSON.stringify(b);
  let settings=null,committed=null,history=[],position=-1,chain=Promise.resolve(),revision=0,selected='roots',draft=[],draftHistory=[],draftPosition=0,colorTimer;
  const expandedPaths={roots:false,excluded_folders:false};
  const say=(text,failed=false)=>Revamp.toast(text,{error:failed}),error=e=>say(e.message,true);
  let lastIndexRead=0,lastBackupPath='',backupPending=false;
  const size=bytes=>{const units=['B','KiB','MiB','GiB','TiB'];let unit=0;while(bytes>=1024&&unit<units.length-1){bytes/=1024;unit++;}return bytes.toFixed(unit?2:0)+' '+units[unit];};
  async function indexInfo(){try{const value=await call('local_index_info');$('index-location').textContent=value.path;$('embedding-model-location').textContent=value.model_path;$('index-storage').textContent=value.saved?size(value.bytes)+' on disk · checkpoint saved '+new Date(value.saved).toLocaleString():'No checkpoint saved yet.';lastIndexRead=Date.now();}catch(e){$('index-storage').textContent=e.message;}}
  function backupStatus(value){if(!value)return;$('backup-index').disabled=backupPending||!!value.active;if(!value.phase)return;$('index-backup-status').className=value.error?'error':'';$('index-backup-status').textContent=value.error?'Backup failed: '+value.error:value.active?value.phase+'… Search and indexing remain available.':value.path?'Backup complete · '+size(value.bytes)+'\n'+value.path:value.phase;if(value.path&&value.path!==lastBackupPath){lastBackupPath=value.path;indexInfo();}}
  $('index-folder').addEventListener('click',()=>call('local_index_folder').catch(error));
  $('backup-index').addEventListener('click',async()=>{backupPending=true;$('backup-index').disabled=true;try{await chain;$('index-backup-status').className='';$('index-backup-status').textContent='Choose a backup folder…';const value=await call('local_backup_index');backupPending=false;if(value.cancelled){$('index-backup-status').textContent='Backup cancelled.';$('backup-index').disabled=false;}else{backupStatus((await call('local_status')).backup);}}catch(e){backupPending=false;$('backup-index').disabled=false;$('index-backup-status').className='error';$('index-backup-status').textContent='Backup failed: '+e.message;}});
  const remember=()=>call('local_settings_history_save',{snapshots:history,position}).catch(()=>{});
  function buttons(){$('settings-undo').disabled=position<1;$('settings-redo').disabled=position>=history.length-1;}
  function render(){
    if(!settings)return;
    localBridge.appearance(settings);
    for(const key of keys){const field=$(key);if(field.type==='checkbox')field.checked=settings[key];else field.value=Array.isArray(settings[key])?settings[key].join(', '):String(settings[key]);}
    keywordRows();
    const paths=settings[selected],summary=$('paths-summary');summary.replaceChildren();
    const count=document.createElement('div');count.className='paths-count';count.textContent=paths.length+' '+(selected==='roots'?'included':'excluded')+' folders'+(selected==='excluded_folders'?' including every subfolder':'');summary.append(count);
    for(const path of paths.slice(0,expandedPaths[selected]?paths.length:4)){const line=document.createElement('div');line.className='scope-path';line.title=path;const split=Math.max(path.lastIndexOf('\\'),path.lastIndexOf('/'))+1,parent=document.createElement('span'),name=document.createElement('span');parent.className='scope-parent';parent.textContent=path.slice(0,split);name.className='scope-leaf';name.textContent=path.slice(split);line.append(parent,name);summary.append(line);}
    if(paths.length>4){const more=document.createElement('button');more.type='button';more.className='paths-more';more.textContent=expandedPaths[selected]?'⌃ Show less':'⌄ Show '+(paths.length-4)+' more';more.setAttribute('aria-expanded',String(expandedPaths[selected]));more.addEventListener('click',()=>{expandedPaths[selected]=!expandedPaths[selected];render();});summary.append(more);}
    for(const [id,key] of [['paths-include','roots'],['paths-exclude','excluded_folders']]){$(id).classList.toggle('active',selected===key);$(id).setAttribute('aria-pressed',String(selected===key));}
    buttons();
  }
  function apply(next,record=true){
    if(!next.name_enabled&&!next.content_enabled&&!next.semantic_enabled&&!next.windows_semantic_enabled){error(new Error('Enable at least one search channel.'));render();return Promise.resolve(false);}
    if(same(settings,next))return chain;
    const target=copy(next),changes=Object.fromEntries(Object.keys(target).filter(key=>!same(target[key],settings[key])).map(key=>[key,target[key]]));
    settings=target;
    if(record){history=[...history.slice(0,position+1),copy(target)].slice(-30);position=history.length-1;}
    remember();
    render();const ticket=++revision;say('Applying…');
    // Send now rather than retaining unsent edits in a WebView that can close.
    // The backend serializes preference writes in receipt order.
    const pending=call('local_save_config',changes).then(result=>({result}),failure=>({failure}));
    chain=chain.then(async()=>{
      try{const {result,failure}=await pending;if(failure)throw failure;committed=copy(result.settings||{...committed,...changes});if(ticket===revision){settings=copy(committed);history[position]=copy(settings);remember();render();say(result.restart_required?'Applied. Model or protection changes need a relaunch.':'Applied.');}return true;}
      catch(e){if(ticket===revision){settings=copy(committed);history=[copy(committed)];position=0;remember();render();}error(e);return false;}
    });return chain;
  }
  async function load(){try{const value=await call('local_config');if(!settings){const saved=await call('local_settings_history_get').catch(()=>null);settings=copy(value.settings);if($('app-version'))$('app-version').textContent='AI Center version '+(value.version||'unknown');committed=copy(settings);if(saved?.snapshots&&same(saved.snapshots[saved.position],settings)){history=copy(saved.snapshots);position=saved.position;}else{history=[copy(settings)];position=0;}render();}}catch(e){error(e);}}
  load();indexInfo();window.addEventListener('local-center-focus',()=>{load();indexInfo();});
  window.addEventListener('suite-settings',event=>{if(!settings)return; settings={...settings,...event.detail};committed={...committed,...event.detail};history[position]=copy(settings);render();});
  window.addEventListener('suite-owner',event=>{$('suite-owner').textContent=event.detail;});
  $('suite-manager').addEventListener('click',()=>call('local_suite_manager').catch(error));
  for(const key of keys){$(key).addEventListener('change',()=>{clearTimeout(colorTimer);const field=$(key),value=field.type==='checkbox'?field.checked:lists.includes(key)?field.value.split(/[\s,;]+/).filter(Boolean):field.value;apply({...settings,[key]:value});});}
  // Keywords: one row each and an empty row at the end to add one. A row opens a program, file or folder (stored as
  // its path), runs a script ({run, window}) or runs a command line ({command, shell, window}).
  const scriptTypes=/\.(bat|cmd|ps1|py|sh)$/i;
  function keywordRows(){
    const host=$('keywords');if(!host)return;
    const entries=Object.entries(settings.keywords||{}),save=next=>apply({...settings,keywords:Object.fromEntries(next)});
    const kindOf=value=>typeof value==='string'?'open':value.command!==undefined?'command':'run';
    const problem=(word,kind,text,index)=>!word||/[\s=]/.test(word)||word.length>32?'A keyword is one word of at most 32 characters.':entries.some(([other],at)=>other===word&&at!==index)?'"'+word+'" is already a keyword.':!text.trim()?(kind==='command'?'Type the command "'+word+'" runs.':'Choose what "'+word+'" '+(kind==='run'?'runs':'opens')+': type a path or use Browse.'):kind==='run'&&!scriptTypes.test(text)?'Run works with .bat, .cmd, .ps1, .py and .sh files; use Open for anything else.':'';
    const select=(label,options,value)=>{const field=document.createElement('select');field.setAttribute('aria-label',label);for(const [key,text] of options){const option=document.createElement('option');option.value=key;option.textContent=text;field.append(option);}field.value=value;return field;};
    host.replaceChildren();
    [...entries,['','']].forEach(([word,value],index)=>{
      const fresh=index===entries.length,kind=kindOf(value),row=document.createElement('div'),key=document.createElement('input'),target=document.createElement('input'),browse=document.createElement('button'),last=document.createElement('button');
      const action=select('What '+(word||'the new keyword')+' does',[['open','Open'],['run','Run'],['command','Command']],kind);
      const shell=select('Shell',[['pwsh','PowerShell 7'],['cmd','Command Prompt'],['bash','Git Bash']],value.shell||'pwsh');
      const mode=select('Window',[['hidden','Hidden, notice'],['terminal','Terminal window']],value.window||'hidden');
      row.className='keyword-row';key.type=target.type='text';key.spellcheck=target.spellcheck=false;key.maxLength=32;key.value=word;
      target.value=typeof value==='string'?value:value.command??value.run;
      key.placeholder='keyword';key.setAttribute('aria-label',fresh?'New keyword':'Keyword '+word);
      browse.type=last.type='button';browse.textContent='Browse…';last.textContent=fresh?'+':'−';last.setAttribute('aria-label',fresh?'Add keyword':'Remove keyword '+word);
      const show=()=>{const now=action.value;shell.hidden=now!=='command';mode.hidden=now==='open';browse.hidden=now==='command';
        target.placeholder=now==='command'?'command, for example ipconfig /flushdns':now==='run'?'script: .bat, .cmd, .ps1, .py or .sh':'program, file or folder';
        target.setAttribute('aria-label',(now==='command'?'Command ':'Path ')+(fresh?'of the new keyword':'of '+word));};
      // A command is kept exactly as typed; a path loses surrounding quotes.
      const read=()=>{const now=action.value,text=now==='command'?target.value.trim():target.value.trim().replace(/^"|"$/g,'');return [key.value.trim().toLowerCase(),now,text,now==='open'?text:now==='run'?{run:text,window:mode.value}:{command:text,shell:shell.value,window:mode.value}];};
      // An existing row saves as soon as it is edited; the new row waits until it has both a word and a target.
      const commit=explicit=>{const [nextWord,nextKind,text,stored]=read();if(fresh&&!explicit&&(!nextWord||!text))return;const wrong=problem(nextWord,nextKind,text,fresh?-1:index);if(wrong){error(new Error(wrong));if(!fresh)render();return;}const next=entries.map(pair=>[...pair]);next[index]=[nextWord,stored];save(next);};
      for(const field of [key,target,shell,mode])field.addEventListener('change',()=>commit(false));
      action.addEventListener('change',()=>{show();commit(false);});
      browse.addEventListener('click',async()=>{try{const chosen=await call('local_keyword_browse');if(!chosen?.path)return;target.value=chosen.path;if(fresh&&!read()[0])key.focus();else commit(false);}catch(e){error(e);}});
      last.addEventListener('click',()=>{if(fresh)commit(true);else save(entries.filter((_,at)=>at!==index));});
      show();row.append(key,action,target,shell,mode,browse,last);host.append(row);
    });
  }
  $('accent_color').addEventListener('input',()=>{localBridge.appearance({...settings,accent_color:$('accent_color').value});clearTimeout(colorTimer);const value=$('accent_color').value;colorTimer=setTimeout(()=>apply({...settings,accent_color:value}),250);});
  $('settings-form').addEventListener('submit',event=>event.preventDefault());
  function step(delta){const next=position+delta;if(next<0||next>=history.length)return;position=next;apply(copy(history[position]),false);remember();buttons();}
  Revamp.history($('settings-undo'),$('settings-redo'),forward=>step(forward?1:-1));
  for(const [id,key] of [['paths-include','roots'],['paths-exclude','excluded_folders']])$(id).addEventListener('click',()=>{selected=key;render();});
  $('path-add').addEventListener('click',async()=>{const path=$('path-input').value.trim();if(!path){error(new Error('Enter an absolute folder path.'));return;}if(await apply({...settings,[selected]:[...new Set([...settings[selected],path])]}))$('path-input').value='';});
  function draftRender(){
    $('paths-rows').replaceChildren();$('paths-undo').disabled=draftPosition===0;$('paths-redo').disabled=draftPosition===draftHistory.length-1;
    draft.forEach((path,index)=>{const row=document.createElement('div');row.className='path-row';const input=document.createElement('input');input.type='text';input.value=path;input.spellcheck=false;input.setAttribute('aria-label','Folder path '+(index+1));input.addEventListener('change',()=>{const next=[...draft];next[index]=input.value;draftChange(next);});const remove=document.createElement('button');remove.type='button';remove.textContent='−';remove.setAttribute('aria-label','Remove folder '+(index+1));remove.addEventListener('click',()=>draftChange(draft.filter((_,i)=>i!==index)));row.append(input,remove);$('paths-rows').append(row);});
  }
  function draftChange(next){if(same(draft,next))return;draft=next;draftHistory=draftHistory.slice(0,draftPosition+1);draftHistory.push([...next]);draftPosition++;draftRender();}
  $('path-manage').addEventListener('click',()=>{draft=[...settings[selected]];draftHistory=[[...draft]];draftPosition=0;$('paths-title').textContent=selected==='roots'?'Included folders':'Excluded folders';$('paths-error').textContent='';draftRender();$('paths-dialog').showModal();});
  $('paths-new').addEventListener('click',()=>{draftChange([...draft,'']);$('paths-rows').lastElementChild?.querySelector('input')?.focus();});
  for(const [id,delta] of [['paths-undo',-1],['paths-redo',1]])$(id).addEventListener('click',()=>{const next=draftPosition+delta;if(next<0||next>=draftHistory.length)return;draftPosition=next;draft=[...draftHistory[next]];draftRender();});
  $('paths-cancel').addEventListener('click',()=>$('paths-dialog').close());
  $('paths-save').addEventListener('click',async()=>{$('paths-save').disabled=true;try{const paths=[...new Set(draft.map(path=>path.trim()).filter(Boolean))];if(await apply({...settings,[selected]:paths}))$('paths-dialog').close();else $('paths-error').textContent=$('toast').textContent;}finally{$('paths-save').disabled=false;}});
  $('scan-pc').addEventListener('click',async()=>{try{const {roots}=await call('local_drives');if(!roots.length)throw new Error('No accessible local drives.');await apply({...settings,roots});}catch(e){error(e);}});
  function status(value){window.IndexProgress?.update(value);backupStatus(value.backup);if(Date.now()-lastIndexRead>15000)indexInfo();$('pause-index').textContent=value.indexer.mode==='paused'?'Resume':'Pause';$('status').textContent=`${value.counts.files} files · ${value.counts.folders} folders · ${value.counts.vectors} semantic passages\n${value.indexer.busy?(value.indexer.phase||'Indexing')+' · '+value.indexer.queued_files+' queued files · '+value.indexer.queued_embedding_files+' embedding jobs':value.indexer.mode} · model: ${value.model.device||'unloaded'}${value.indexer.current?'\n'+value.indexer.current:''}${value.model.error?'\n'+value.model.error:''}${value.indexer.scan_error?'\nSkipped an inaccessible folder: '+value.indexer.scan_error:''}${value.indexer.semantic_error?'\nSemantic indexing paused: '+value.indexer.semantic_error:''}`;}
  localBridge.subscribe('register_local_status',status);
  localBridge.native.addEventListener('message',event=>{if(event.data.kind==='backend_stopped'){window.IndexProgress?.stopped();$('status').textContent=$('status').textContent.split('\n')[0]+'\nSearch worker is idle · opens when needed.';}});
  async function indexNow(method){$('scan').disabled=true;$('reset-index').disabled=true;try{await chain;const value=await call(method,{force:true,wait:false});status(value);say('Indexing in the background. Progress appears below.');}catch(e){error(e);}finally{$('scan').disabled=false;$('reset-index').disabled=false;}}
  $('scan').addEventListener('click',()=>indexNow('local_index_now'));
  $('pause-index').addEventListener('click',async()=>{const mode=$('pause-index').textContent==='Resume'?'normal':'paused';try{await apply({...settings,indexing_mode:mode});status(await call('local_mode',{mode}));}catch(e){error(e);}});
  $('reset-index').addEventListener('click',()=>$('reset-dialog').showModal());$('cancel-reset').addEventListener('click',()=>$('reset-dialog').close());$('confirm-reset').addEventListener('click',()=>{$('reset-dialog').close();indexNow('local_reset_index');});
  $('clear-search-history')?.addEventListener('click',async()=>{try{await call('local_history_clear');say('Search history and remembered file choices cleared.');}catch(e){error(e);}});
})();
