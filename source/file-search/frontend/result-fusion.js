/* Purpose: merge ranked local and Windows candidates without mixing raw scores.
 * Dependencies: none. Output: deduplicated rows. Loaded by search.html.
 * Static syntax: node --check frontend/result-fusion.js */
(() => {
  function merge(local, windows, text) {
    const items=new Map(), key=row=>row.file_path.replace(/\\/g,'/').toLocaleLowerCase();
    for(const [source,weight] of [[local,1],[windows,1]])source.forEach((row,rank)=>{
      // Local rows arrive already ordered by the file-type weights. A Windows row of a low-priority
      // type is lowered the same way; a preferred type gains nothing here, or Windows would always win.
      const path=key(row), found=items.get(path), share=(source===windows?Math.min(1,row.type_weight??1):1)*weight/(60+rank+1);
      if(found){found.fusion+=share;found.row.matches=[...new Set([...(found.row.matches||[]),...(row.matches||[])])];}
      else items.set(path,{row:{...row,matches:[...(row.matches||[])]},fusion:share,localRank:source===local?rank:1000000});
    });
    const query=text.trim().toLocaleLowerCase(), exact=row=>row.name.toLocaleLowerCase()===query||row.name.replace(/\.[^.]+$/,'').toLocaleLowerCase()===query;
    return [...items.values()].sort((a,b)=>{
      const rememberedA=a.row.matches.includes('Previously opened'),rememberedB=b.row.matches.includes('Previously opened');
      if(rememberedA!==rememberedB)return rememberedB-rememberedA;
      if(rememberedA){const rankA=a.row.preference_rank??a.localRank,rankB=b.row.preference_rank??b.localRank;if(rankA!==rankB)return rankA-rankB;}
      if(exact(a.row)!==exact(b.row))return Number(exact(b.row))-Number(exact(a.row));
      return b.fusion-a.fusion||a.localRank-b.localRank||key(a.row).localeCompare(key(b.row));
    }).map(item=>item.row);
  }
  window.ResultFusion={merge};
})();
