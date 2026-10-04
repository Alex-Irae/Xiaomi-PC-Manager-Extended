/* Purpose: interpret optional first/last extension tokens for the search UI.
 * Dependencies: browser JavaScript. Outputs: existing ext: filter syntax.
 * Launch: loaded by search.html; check: node tests/check_frontend.cjs. */
(() => {
  const extensions=['pdf','doc','docx','txt','md','rtf','odt','xls','xlsx','csv','ppt','pptx','epub','html','json','xml','py','js','cs','cpp','h','png','jpg','jpeg','gif','webp','svg','bmp','heic','mp3','wav','flac','m4a','mp4','mkv','avi','mov','zip','7z','rar','exe'];
  const known=new Set(extensions);
  function extension(token){
    const value=token.toLowerCase();
    if(known.has(value))return value;
    const explicit=/^(?:ext:|\*\.|\.)([a-z0-9][a-z0-9_-]{0,15})$/.exec(value);
    return explicit?explicit[1]:'';
  }
  function parse(query){
    const words=query.trim().split(/\s+/).filter(Boolean);
    const first=extension(words[0]||''),last=words.length>1?extension(words.at(-1)):'';
    if(first&&last&&first!==last)return {text:query.trim(),extension:'',error:'Choose one file type, or quote a type word to search for it.'};
    if(last)words.pop();
    if(first)words.shift();
    return {text:words.join(' '),extension:first||last,error:''};
  }
  window.SearchFilters={extensions,parse};
})();
