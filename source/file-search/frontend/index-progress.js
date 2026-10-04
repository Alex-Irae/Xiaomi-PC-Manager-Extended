/* Purpose: known-work coverage and approximate ETA from existing status snapshots.
 * Dependencies: center.html, settings.js; no extra worker calls or database reads.
 * Outputs: three progress indicators. Launch: Development/Launch.cmd, Search settings. */
(() => {
  const $=id=>document.getElementById(id), samples=[];
  let scope='', lastSemantic, lastAdvance={files:0,vectors:0,overall:0};
  const count=value=>Number.isFinite(value)&&value>=0?value:0;
  const duration=seconds=>seconds<60?'less than a minute':seconds<3600?Math.ceil(seconds/60)+' min':Math.ceil(seconds/3600)+' hr';
  function update(value) {
    const now=Date.now(), counts=value.counts||{}, worker=value.indexer||{};
    const semantic=value.semantic_enabled!==false;
    const files=count(counts.files), done=Math.max(0,files-count(counts.pending));
    const chunks=count(counts.chunks), vectors=Math.min(chunks,count(counts.vectors));
    const current={at:now,files:done,vectors,overall:done+(semantic?vectors:0)};
    const nextScope=JSON.stringify(value.roots||[]), previous=samples.at(-1);
    // Reconciliation/reset and scope changes invalidate old throughput samples.
    if(nextScope!==scope||semantic!==lastSemantic||worker.mode==='paused'||
       previous&&(done<previous.files||vectors<previous.vectors)) {
      samples.length=0;lastAdvance={files:now,vectors:now,overall:now};
    }
    scope=nextScope;lastSemantic=semantic;
    for(const key of ['files','vectors','overall'])if(previous&&current[key]>previous[key])lastAdvance[key]=now;
    samples.push(current);
    while(samples.length>1&&samples[0].at<now-120000)samples.shift();
    // Bound memory even if a fixture or subscription delivers unusually fast updates.
    while(samples.length>240)samples.shift();
    const base=samples[0], elapsed=(now-base.at)/1000;
    function eta(key,remaining,blocked) {
      if(worker.mode==='paused')return 'Paused';
      if(blocked)return 'Blocked: '+blocked;
      if(!remaining)return worker.busy?'Checking for more work…':'Known work complete';
      if(!worker.busy)return 'Waiting for indexing';
      if(elapsed<30)return 'Estimating remaining time…';
      const gained=current[key]-base[key];
      if(gained<=0||now-lastAdvance[key]>=30000)return 'No recent progress';
      return 'Approx. '+duration(remaining/(gained/elapsed))+' remaining for known work';
    }
    function draw(id,completed,total,noun,key,blocked,unavailable='') {
      const bar=$('progress-'+id), label=$('progress-'+id+'-value'), detail=$('progress-'+id+'-detail');
      if(!bar||!label||!detail)return;
      if(unavailable||!total){bar.removeAttribute('value');label.textContent=unavailable||'No '+noun+' yet';detail.textContent=worker.mode==='paused'?'Paused':worker.busy?'Discovering work…':'Waiting for work';return;}
      const percent=100*completed/total;
      bar.value=percent;label.textContent=(percent===100?'100':percent.toFixed(1))+'%';
      detail.textContent=completed.toLocaleString()+' / '+total.toLocaleString()+' '+noun+' · '+eta(key,total-completed,blocked);
    }
    const failure=semantic&&(worker.semantic_error||value.model?.error);
    draw('indexing',done,files,'files processed','files','');
    draw('embedding',vectors,chunks,'passages embedded','vectors',failure,
      !semantic?'Disabled':counts.chunks===undefined?'Unavailable':'');
    draw('overall',current.overall,files+(semantic?chunks:0),'files + passages','overall',failure,
      semantic&&counts.chunks===undefined?'Unavailable':'');
    $('progress-errors').textContent=counts.error?count(counts.error).toLocaleString()+' files failed extraction; included as processed attempts.':'';
  }
  function stopped() {
    samples.length=0;
    for(const id of ['overall','indexing','embedding'])if($('progress-'+id+'-detail'))$('progress-'+id+'-detail').textContent='Worker stopped; waiting for a fresh status';
  }
  window.IndexProgress={update,stopped};
})();
