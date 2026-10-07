/* Purpose: compact search with lexical results before semantic retrieval.
 * Dependencies: bridge.js. Outputs: indexed results. Launch: Launch Search.cmd. */
(() => {
  const $=id=>document.getElementById(id),call=(...args)=>localBridge.call(...args);
  const categories=[['All',4095],['Folders',2],['Documents',124],['Images',256],['Audio',128],['Video',512],['Archives',1024],['Programs',2048],['Other',1]];
  let rows=[],visible=[],selected=0,type=4095,sequence=0,timer,historyTimer,settings={},pending=false,dismissed=false;
  const historyKey='local-search-history-v1';
  let history=[],historyRevision=0,menuRow=null,autoExtension=false,openedAt={};
  // The hint on each result: when you last opened that file from search, else when the file was last changed.
  const remembered=data=>{if(data?.opened)openedAt=Object.fromEntries(Object.entries(data.opened).map(([path,time])=>[path.toLowerCase(),time]));};
  function day(time){const date=new Date(time),now=new Date();if(date.toDateString()===now.toDateString())return 'Today';return date.toLocaleDateString('en-GB',date.getFullYear()===now.getFullYear()?{day:'numeric',month:'short'}:{month:'short',year:'numeric'});}
  try{const saved=JSON.parse(localStorage.getItem(historyKey)||'[]');if(Array.isArray(saved))history=saved.filter(value=>typeof value==='string'&&value.trim()&&value.length<=256).slice(0,20);}catch{}
  call('local_history_get').then(async data=>{
    remembered(data);
    if(historyRevision===0){if(data.queries.length)history=data.queries;else if(history.length)await call('local_history_save',{queries:history});}
    localStorage.removeItem(historyKey);renderHistory();resize();
  }).catch(error=>localBridge.feedback(error.message,true));
  function remember(query){
    query=query.trim();if(!query||query.length>256)return;
    const revision=++historyRevision;
    history=[query,...history.filter(value=>value.toLocaleLowerCase()!==query.toLocaleLowerCase())].slice(0,20);
    call('local_history_remember',{query}).then(data=>{if(revision===historyRevision){history=data.queries;renderHistory();resize();}}).catch(error=>localBridge.feedback(error.message,true));
  }
  async function forget(query){const revision=++historyRevision;history=query?history.filter(value=>value.toLocaleLowerCase()!==query.toLocaleLowerCase()):[];clearTimeout(historyTimer);renderHistory();resize();try{const data=await call(query?'local_history_remove':'local_history_clear',{query});if(revision===historyRevision){history=data.queries;renderHistory();resize();}if(!dismissed&&$('query').value.trim())search(false);}catch(error){localBridge.feedback(error.message,true);}}
  localBridge.native?.addEventListener('message',event=>{if(event.data.kind==='history_changed'){++historyRevision;history=event.data.queries;clearTimeout(historyTimer);renderHistory();resize();}});
  function renderHistory(){
    const host=$('history'),query=$('query').value.trim().toLocaleLowerCase();host.replaceChildren();
    const matches=query?history.filter(value=>value.toLocaleLowerCase().includes(query)).slice(0,5):[];
    host.hidden=!matches.length;
    if(!matches.length)return;
    const label=document.createElement('span');label.className='history-label';label.textContent='Recent';host.append(label);
    for(const value of matches){const item=document.createElement('span');item.className='history-item';const chip=document.createElement('button');chip.type='button';chip.textContent=value;chip.title=value;chip.addEventListener('click',()=>{$('query').value=value;$('query').focus();clearTimeout(timer);renderHistory();search();});const remove=document.createElement('button');remove.type='button';remove.className='history-remove';remove.textContent='×';remove.setAttribute('aria-label','Remove '+value+' from history');remove.addEventListener('click',()=>forget(value));item.append(chip,remove);host.append(item);}
    const clear=document.createElement('button');clear.type='button';clear.className='history-clear';clear.textContent='Clear history';clear.addEventListener('click',()=>forget());host.append(clear);
  }
  function closeMenu(){menuRow=null;$('result-menu').hidden=true;}
  function showMenu(row,index,x,y){select(index);menuRow=row;const menu=$('result-menu');menu.hidden=false;$('menu-open-with').disabled=row.file_type===2||row.program===true;$('menu-reveal').disabled=row.program===true;menu.style.left=Math.max(8,Math.min(x,window.innerWidth-menu.offsetWidth-8))+'px';menu.style.top=Math.max(8,Math.min(y,window.innerHeight-menu.offsetHeight-8))+'px';$('menu-open').focus();}
  function reset(){
    ++sequence;clearTimeout(timer);clearTimeout(historyTimer);closeMenu();rows=[];pending=false;type=4095;autoExtension=false;$('file-type').value='';$('query').value='';
    document.querySelectorAll('nav button').forEach((button,index)=>button.classList.toggle('active',index===0));
    renderHistory();localBridge.feedback('Local search · start typing');render();
  }
  function appearance(value){settings=value;localBridge.appearance(value);document.documentElement.style.setProperty('--row',value.bar_size==='compact'?'54px':'66px');resize();}
  function resize(){requestAnimationFrame(()=>{
    const expanded=!$('categories').hidden;
    const height=expanded?$('search-form').offsetHeight+($('history').hidden?0:$('history').offsetHeight)+$('categories').offsetHeight+Math.min(430,$('results').scrollHeight)+document.querySelector('footer').offsetHeight+2:$('search-form').offsetHeight+2;
    call('window_resize',{width:700,height:Math.max(78,Math.min(620,Math.ceil(height)))}).catch(()=>{});
  });}
  function select(index){selected=Math.max(0,Math.min(visible.length-1,index));document.querySelectorAll('.result').forEach((button,i)=>{button.classList.toggle('selected',i===selected);button.setAttribute('aria-selected',String(i===selected));});if(selected===0)$('results').scrollTop=0;else document.querySelectorAll('.result')[selected]?.scrollIntoView({block:'nearest'});}
  function render(){
    const host=$('results');host.replaceChildren();visible=[];
    const extension=$('file-type').value;
    const filtered=rows.filter(row=>(type===4095||(row.file_type&type))&&(!extension||(row.file_type!==2&&row.name.toLowerCase().endsWith('.'+extension))));
    const groups=rows[0]?.typed_path?[['Path',rows]]:type===4095?[['Best matches',filtered.slice(0,5)],...categories.slice(1).map(([label,mask])=>[label,filtered.slice(5).filter(row=>row.file_type&mask).slice(0,12)])]:[[categories.find(c=>c[1]===type)[0],filtered.slice(0,60)]];
    for(const [label,items] of groups){
      if(!items.length)continue;const heading=document.createElement('div');heading.className='group-label';heading.textContent=label;host.append(heading);
      for(const row of items){
        const index=visible.length;visible.push(row);const button=document.createElement('button');button.className='result'+(row.file_type===2?' folder':'');button.role='option';
        const icon=document.createElement('span');icon.className='kind';icon.textContent=row.program?'APP':row.file_type===2?'▱':row.name.split('.').pop().slice(0,3).toUpperCase();
        const detail=document.createElement('div');detail.className='details';const name=document.createElement('div');name.className='name';name.innerHTML=row.file_name_with_highlight;
        const path=document.createElement('div');path.className='path';path.textContent=row.program?'Program':row.file_path;path.title=row.file_path;detail.append(name,path);
        if(row.snippet){const snippet=document.createElement('div');snippet.className='snippet';snippet.textContent=row.snippet.replace(/\s+/g,' ');detail.append(snippet);}
        const opened=openedAt[String(row.file_path).toLowerCase()],hint=document.createElement('span');hint.className='open-hint';
        hint.textContent=row.program?'Run':opened?'Opened '+day(opened):row.time_stamp?day(row.time_stamp):'Open';
        hint.title=[opened?'Opened from search '+new Date(opened).toLocaleString('en-GB'):'',!row.program&&row.time_stamp?'Changed '+new Date(row.time_stamp).toLocaleString('en-GB'):'',...(row.matches||[])].filter(Boolean).join(' · ')||'Open';button.append(icon,detail,hint);button.addEventListener('click',()=>{select(index);open('open_file');});button.addEventListener('contextmenu',event=>{event.preventDefault();showMenu(row,index,event.clientX,event.clientY);});host.append(button);
      }
    }
    if(!visible.length){const empty=document.createElement('div');empty.className='empty';empty.textContent=pending?'Searching…':'No matching files, folders or programs.';host.append(empty);}
    const hasQuery=Boolean($('query').value.trim()||$('file-type').value);$('categories').hidden=!hasQuery;host.hidden=!hasQuery;renderHistory();select(0);resize();
  }
  function addFileType(extension){
    if([...$('file-type').children].some(option=>option.value===extension))return;
    const option=document.createElement('option');option.value=extension;option.textContent=extension.toUpperCase();$('file-type').append(option);
  }
  function syncFileType(){
    const parsed=window.SearchFilters.parse($('query').value);
    if(parsed.extension){addFileType(parsed.extension);$('file-type').value=parsed.extension;autoExtension=true;}
    else if(autoExtension){$('file-type').value='';autoExtension=false;}
    return parsed;
  }
  for(const extension of window.SearchFilters.extensions)addFileType(extension);
  $('file-type').addEventListener('change',()=>{
    const parsed=window.SearchFilters.parse($('query').value);
    if(parsed.extension)$('query').value=parsed.text;
    autoExtension=false;type=4095;
    document.querySelectorAll('nav button').forEach((button,index)=>button.classList.toggle('active',index===0));
    clearTimeout(timer);clearTimeout(historyTimer);closeMenu();rows=[];search();
  });
  // A typed path (~\\.claude, C:\\Users, %APPDATA%\\..., \\\\server\\share) is a place to open, not words to search for.
  const typedPath=text=>/^\s*"?(~([\\/]|"?\s*$)|[A-Za-z]:[\\/]|\\\\|%[^%]+%)/.test(text);
  async function openTyped(){
    if(pending||!visible[selected]?.typed_path)await search(false);
    if(visible[selected]?.typed_path)open('open_file');else localBridge.feedback('No such file or folder',true);
  }
  async function search(rememberQuery=true){
    if(dismissed)return;
    const ticket=++sequence,query=$('query').value.trim(),parsed=syncFileType();
    if(typedPath(query)){
      pending=true;
      try{const found=await call('local_path',{text:query});if(ticket!==sequence||dismissed)return;rows=found.results;pending=false;localBridge.feedback(rows.length?'Enter opens '+rows[0].file_path+' in Explorer':'No such file or folder',!rows.length);render();}
      catch(error){if(ticket===sequence){rows=[];pending=false;localBridge.feedback(error.message,true);render();}}
      return;
    }
    if(parsed.error){rows=[];pending=false;localBridge.feedback(parsed.error,true);render();return;}
    const requestText=[parsed.text,$('file-type').value?'ext:'+$('file-type').value:''].filter(Boolean).join(' ');
    if(!requestText){rows=[];pending=false;localBridge.feedback('Local search · start typing');render();return;}
    pending=true;localBridge.feedback('Searching locally…');
    let localRows=[],windowsRows=[];
    const update=()=>{if(ticket!==sequence||dismissed)return;rows=ResultFusion.merge(localRows,windowsRows,parsed.text);pending=false;render();};
    const own=settings.name_enabled!==false||settings.content_enabled!==false;
    const lexicalTask=own?call('local_search',{text:requestText,history_query:query,semantic:false}):Promise.resolve(null);
    const windowsTask=settings.windows_semantic_enabled?call('local_windows_search',{text:requestText,history_query:query}).then(value=>{
      if(ticket!==sequence)return;windowsRows=value.results;update();
      localBridge.feedback(value.warnings[0]||rows.length+' combined results · Windows '+value.timing.total_ms+' ms',Boolean(value.warnings.length));
    }).catch(error=>{if(ticket===sequence)localBridge.feedback('Windows Search: '+error.message,true);}):Promise.resolve();
    try{
      const lexical=await lexicalTask;if(ticket!==sequence)return;
      if(lexical){localRows=lexical.results;update();localBridge.feedback(rows.length+' results · '+lexical.timing.total_ms+' ms');if(rememberQuery)scheduleHistory(query,ticket);}
      if(settings.semantic_enabled){const hybrid=await call('local_search',{text:requestText,history_query:query,semantic:true});if(ticket!==sequence)return;localRows=hybrid.results;update();localBridge.feedback(hybrid.warnings[0]||rows.length+' combined results',Boolean(hybrid.warnings.length));}
      await windowsTask;if(ticket!==sequence)return;update();
      if(rememberQuery&&!own)scheduleHistory(query,ticket);
    }catch(error){if(ticket===sequence){pending=false;localBridge.feedback(error.message,true);render();}}
  }

  function scheduleHistory(query,ticket){clearTimeout(historyTimer);historyTimer=setTimeout(()=>{if(!dismissed&&ticket===sequence&&$('query').value.trim()===query)remember(query);},800);}
  function open(method,row=visible[selected]){if(!row)return;const query=$('query').value.trim();clearTimeout(historyTimer);closeMenu();
    if(row.typed_path){remember(query);call('local_open_path',{path:row.file_path}).catch(error=>{if(!dismissed)localBridge.feedback(error.message,true);});return;}if(method!=='open_file_folder'){remember(query);if(query&&query.length<=256&&!row.program)openedAt[String(row.file_path).toLowerCase()]=Date.now();}call(method,{file_id:row.file_id,file_path:row.file_path,windows_result:row.windows_result===true,program:row.program===true,query:query.length<=256?query:''}).catch(error=>{if(!dismissed)localBridge.feedback(error.message,true);});}
  for(const [id,method] of [['menu-open','open_file'],['menu-reveal','open_file_folder'],['menu-open-with','open_file_with']])$(id).addEventListener('click',()=>open(method,menuRow));
  document.addEventListener('pointerdown',event=>{if(!$('result-menu').contains(event.target))closeMenu();});
  for(const [label,mask] of categories){const button=document.createElement('button');button.textContent=label;button.className=mask===type?'active':'';button.addEventListener('click',()=>{type=mask;document.querySelectorAll('nav button').forEach(b=>b.classList.toggle('active',b===button));render();});$('categories').append(button);}
  $('query').addEventListener('input',()=>{++sequence;syncFileType();closeMenu();clearTimeout(historyTimer);rows=[];pending=Boolean($('query').value.trim());localBridge.feedback(pending?'Searching locally…':'Local search · start typing');render();clearTimeout(timer);timer=setTimeout(search,180);});
  $('search-form').addEventListener('submit',event=>{event.preventDefault();clearTimeout(timer);search();});
  document.querySelectorAll('[data-drag]').forEach(element=>{
    let start=null;
    element.addEventListener('pointerdown',event=>{if(event.button!==0)return;event.preventDefault();start={x:event.screenX,y:event.screenY};element.setPointerCapture(event.pointerId);localBridge.host('drag_start');});
    element.addEventListener('pointermove',event=>{if(start)localBridge.host('drag_move',{dx:event.screenX-start.x,dy:event.screenY-start.y});});
    const end=()=>{start=null;};element.addEventListener('pointerup',end);element.addEventListener('lostpointercapture',end);
  });
  document.addEventListener('keydown',event=>{
    if(event.key==='Escape'){event.preventDefault();if(!$('result-menu').hidden){closeMenu();$('query').focus();}else call('local_hide').catch(()=>{});}
    else if(event.target?.closest?.('[role="menu"],#history,select'))return;
    else if(['ArrowDown','ArrowUp'].includes(event.key)&&visible.length){event.preventDefault();select(selected+(event.key==='ArrowDown'?1:-1));}
    else if(event.key==='Enter'){event.preventDefault();clearTimeout(timer);if(typedPath($('query').value))openTyped();else search();$('query').focus();}
    else if(event.ctrlKey&&event.key.toLowerCase()==='c'&&visible.length&&$('query').selectionStart===$('query').selectionEnd&&!window.getSelection()?.toString()){event.preventDefault();call('copy_file_path_to_clipboard',{file_id:visible[selected].file_id,file_path:visible[selected].file_path,windows_result:visible[selected].windows_result===true}).catch(()=>{});}
  });
  function loadSettings(){call('local_config').then(data=>appearance(data.settings)).catch(error=>{if(!dismissed)localBridge.feedback(error.message,true);});}
  window.addEventListener('local-search-open',()=>{dismissed=false;reset();loadSettings();$('query').focus();});
  window.addEventListener('local-search-dismiss',()=>{dismissed=true;reset();});
  window.addEventListener('settings-changed',event=>{++sequence;appearance(event.detail);if(!dismissed)search();});
  loadSettings();reset();$('query').focus();
})();
