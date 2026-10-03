/* Purpose: compact search with lexical results before semantic retrieval.
 * Dependencies: bridge.js. Outputs: indexed results. Launch: Launch Search.cmd. */
(() => {
  const $=id=>document.getElementById(id),call=(...args)=>localBridge.call(...args);
  const categories=[['All',4095],['Folders',2],['Documents',124],['Images',256],['Audio',128],['Video',512],['Archives',1024],['Other',1]];
  let rows=[],visible=[],selected=0,type=4095,sequence=0,timer,historyTimer,settings={},pending=false,dismissed=false;
  const historyKey='local-search-history-v1';
  let history=[],historyRevision=0,menuRow=null,enterRequest=null;
  try{const saved=JSON.parse(localStorage.getItem(historyKey)||'[]');if(Array.isArray(saved))history=saved.filter(value=>typeof value==='string'&&value.trim()&&value.length<=256).slice(0,20);}catch{}
  call('local_history_get').then(async data=>{
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
  function showMenu(row,index,x,y){select(index);menuRow=row;const menu=$('result-menu');menu.hidden=false;$('menu-open-with').disabled=row.file_type===2;menu.style.left=Math.max(8,Math.min(x,window.innerWidth-menu.offsetWidth-8))+'px';menu.style.top=Math.max(8,Math.min(y,window.innerHeight-menu.offsetHeight-8))+'px';$('menu-open').focus();}
  function reset(){
    ++sequence;enterRequest=null;clearTimeout(timer);clearTimeout(historyTimer);closeMenu();rows=[];pending=false;type=4095;$('query').value='';
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
    const filtered=rows.filter(row=>type===4095||(row.file_type&type));
    const groups=type===4095?[['Best matches',filtered.slice(0,5)],...categories.slice(1).map(([label,mask])=>[label,filtered.slice(5).filter(row=>row.file_type&mask).slice(0,12)])]:[[categories.find(c=>c[1]===type)[0],filtered.slice(0,60)]];
    for(const [label,items] of groups){
      if(!items.length)continue;const heading=document.createElement('div');heading.className='group-label';heading.textContent=label;host.append(heading);
      for(const row of items){
        const index=visible.length;visible.push(row);const button=document.createElement('button');button.className='result'+(row.file_type===2?' folder':'');button.role='option';
        const icon=document.createElement('span');icon.className='kind';icon.textContent=row.file_type===2?'▱':row.name.split('.').pop().slice(0,3).toUpperCase();
        const detail=document.createElement('div');detail.className='details';const name=document.createElement('div');name.className='name';name.innerHTML=row.file_name_with_highlight;
        const path=document.createElement('div');path.className='path';path.textContent=row.file_path;path.title=row.file_path;detail.append(name,path);
        if(row.snippet){const snippet=document.createElement('div');snippet.className='snippet';snippet.textContent=row.snippet.replace(/\s+/g,' ');detail.append(snippet);}
        const hint=document.createElement('span');hint.className=row.matches?.includes('Previously opened')?'meaning-hint':row.matches?.includes('Semantic')?'meaning-hint':'open-hint';hint.textContent=row.matches?.includes('Previously opened')?'Recent choice':row.matches?.includes('Semantic')?'Meaning':'↵';hint.title=row.matches?.join(' · ')||'Open';button.append(icon,detail,hint);button.addEventListener('click',()=>{select(index);open('open_file');});button.addEventListener('contextmenu',event=>{event.preventDefault();showMenu(row,index,event.clientX,event.clientY);});host.append(button);
      }
    }
    if(!visible.length){const empty=document.createElement('div');empty.className='empty';empty.textContent=pending?'Searching…':'No matching files or folders.';host.append(empty);}
    $('categories').hidden=!$('query').value.trim();host.hidden=!$('query').value.trim();renderHistory();select(0);resize();
  }
  async function search(rememberQuery=true){
    if(dismissed)return;
    const ticket=++sequence,query=$('query').value.trim();if(!query){rows=[];pending=false;localBridge.feedback('Local search · start typing');render();return;}
    pending=true;localBridge.feedback('Searching locally…');
    try{
      if(settings.name_enabled!==false||settings.content_enabled!==false){
        const lexical=await call('local_search',{text:query,semantic:false});if(ticket!==sequence)return;rows=lexical.results;pending=false;render();
        localBridge.feedback(`${rows.length} results · ${lexical.timing.total_ms} ms${lexical.timing.cache_hit?' · cached':''}`);if(openWhenReady(query,ticket))return;if(rememberQuery)scheduleHistory(query,ticket);
      }
      if(settings.semantic_enabled){const hybrid=await call('local_search',{text:query,semantic:true});if(ticket!==sequence)return;rows=hybrid.results;pending=false;render();localBridge.feedback(hybrid.warnings[0]||`${rows.length} results · ${hybrid.timing.total_ms} ms${hybrid.timing.cache_hit?' · cached':''}`,Boolean(hybrid.warnings.length));if(openWhenReady(query,ticket))return;}
      enterRequest=null;
      if(rememberQuery&&settings.name_enabled===false&&settings.content_enabled===false)scheduleHistory(query,ticket);
    }catch(error){if(ticket===sequence){enterRequest=null;pending=false;localBridge.feedback(error.message,true);render();}}
  }
  function scheduleHistory(query,ticket){clearTimeout(historyTimer);historyTimer=setTimeout(()=>{if(!dismissed&&ticket===sequence&&$('query').value.trim()===query)remember(query);},800);}
  function openWhenReady(query,ticket){if(!enterRequest||enterRequest.query!==query||ticket!==sequence||!visible.length)return false;const method=enterRequest.method;enterRequest=null;open(method);return true;}
  function open(method,row=visible[selected]){if(!row)return;const query=$('query').value.trim();clearTimeout(historyTimer);closeMenu();if(method!=='open_file_folder')remember(query);call(method,{file_id:row.file_id,file_path:row.file_path,query:query.length<=256?query:''}).catch(error=>{if(!dismissed)localBridge.feedback(error.message,true);});}
  for(const [id,method] of [['menu-open','open_file'],['menu-reveal','open_file_folder'],['menu-open-with','open_file_with']])$(id).addEventListener('click',()=>open(method,menuRow));
  document.addEventListener('pointerdown',event=>{if(!$('result-menu').contains(event.target))closeMenu();});
  for(const [label,mask] of categories){const button=document.createElement('button');button.textContent=label;button.className=mask===type?'active':'';button.addEventListener('click',()=>{type=mask;document.querySelectorAll('nav button').forEach(b=>b.classList.toggle('active',b===button));render();});$('categories').append(button);}
  $('query').addEventListener('input',()=>{++sequence;enterRequest=null;closeMenu();clearTimeout(historyTimer);rows=[];pending=Boolean($('query').value.trim());localBridge.feedback(pending?'Searching locally…':'Local search · start typing');render();clearTimeout(timer);timer=setTimeout(search,180);});
  $('search-form').addEventListener('submit',event=>{event.preventDefault();clearTimeout(timer);search();});
  document.querySelectorAll('[data-drag]').forEach(element=>{
    let start=null;
    element.addEventListener('pointerdown',event=>{if(event.button!==0)return;event.preventDefault();start={x:event.screenX,y:event.screenY};element.setPointerCapture(event.pointerId);localBridge.host('drag_start');});
    element.addEventListener('pointermove',event=>{if(start)localBridge.host('drag_move',{dx:event.screenX-start.x,dy:event.screenY-start.y});});
    const end=()=>{start=null;};element.addEventListener('pointerup',end);element.addEventListener('lostpointercapture',end);
  });
  document.addEventListener('keydown',event=>{
    if(event.key==='Escape'){event.preventDefault();if(!$('result-menu').hidden){closeMenu();$('query').focus();}else call('local_hide').catch(()=>{});}
    else if(event.target?.closest?.('[role="menu"],#history'))return;
    else if(['ArrowDown','ArrowUp'].includes(event.key)&&visible.length){event.preventDefault();select(selected+(event.key==='ArrowDown'?1:-1));}
    else if(event.key==='Enter'&&$('query').value.trim()){event.preventDefault();const method=event.ctrlKey?'open_file_folder':'open_file';if(visible.length)open(method);else{enterRequest={query:$('query').value.trim(),method};clearTimeout(timer);search();}}
    else if(event.ctrlKey&&event.key.toLowerCase()==='c'&&visible.length&&$('query').selectionStart===$('query').selectionEnd&&!window.getSelection()?.toString()){event.preventDefault();call('copy_file_path_to_clipboard',{file_id:visible[selected].file_id}).catch(()=>{});}
  });
  function loadSettings(){call('local_config').then(data=>appearance(data.settings)).catch(error=>{if(!dismissed)localBridge.feedback(error.message,true);});}
  window.addEventListener('local-search-open',()=>{dismissed=false;reset();loadSettings();$('query').focus();});
  window.addEventListener('local-search-dismiss',()=>{dismissed=true;reset();});
  window.addEventListener('settings-changed',event=>{++sequence;appearance(event.detail);if(!dismissed)search();});
  loadSettings();reset();$('query').focus();
})();
