/* Purpose: focused real-WebView preset, swap and spacing checks.
 * Dependencies: native bridge and production app.js/suite.js. Outputs: native JSON report.
 * Command: PCManager.exe --validate-suite. Native host restores bindings and custom actions. */
globalThis.runSuiteValidation=async function(token) {
  const rows=[];
  const check=(name,ok,details={})=>rows.push({name,status:ok?'pass':'fail',...details});
  try {
    suiteState=await Native.call('suite.read');settingState=await Native.call('settings.read');showPage('keyboard');
    check('hub-running',suiteState.hubRunning&&!suiteState.error,{state:suiteState});
    const choices=[...document.querySelectorAll('.suite-shortcut-list select')];
    check('preset-list-and-last-recorder',choices.length===6&&choices.every(node=>node.options[node.options.length-1].value==='record'&&[...node.options].some(option=>option.value==='Copilot')));
    check('redundant-copilot-section-removed',![...document.querySelectorAll('.MiCard h2')].some(node=>node.textContent==='Copilot key'));
    const intro=document.querySelector('.shortcut-intro').getBoundingClientRect(), first=document.querySelector('.suite-shortcut-list .fixed-key-row').getBoundingClientRect();
    check('intro-spacing',first.top-intro.bottom>=10,{gap:first.top-intro.bottom});
    const old=Object.fromEntries(suiteState.bindings.map(item=>[item.action,item.chord]));
    const source=Object.keys(old).find(action=>old[action]==='Copilot')||'screen-translator.screen';
    await Native.call('suite.shortcut',{action:'screen-translator.filter',chord:old[source]});
    let shared=await Native.call('suite.read');let values=Object.fromEntries(shared.bindings.map(item=>[item.action,item.chord]));
    check('copilot-action-swap',values['screen-translator.filter']===old[source]&&values[source]===old['screen-translator.filter']);
    await Native.call('settings.shortcuts',{shortcuts:[{chord:'Ctrl+Alt+Shift+F21',action:'page.keyboard'},{chord:'Ctrl+Alt+Shift+F22',action:'page.tools'}]});
    await Native.call('suite.shortcut',{action:'screen-translator.filter',chord:'Ctrl+Alt+Shift+F21'});
    let saved=await Native.call('settings.read');
    check('suite-to-custom-swap',saved.shortcuts[0].chord===old[source]&&saved.shortcuts[0].action==='page.keyboard');
    await Native.call('settings.shortcuts',{shortcuts:[{chord:'Ctrl+Alt+Shift+F22',action:'page.keyboard'},{chord:'Ctrl+Alt+Shift+F22',action:'page.tools'}]});
    saved=await Native.call('settings.read');
    check('custom-swap-retains-actions',saved.shortcuts[0].chord==='Ctrl+Alt+Shift+F22'&&saved.shortcuts[1].chord===old[source]&&saved.shortcuts[0].action==='page.keyboard'&&saved.shortcuts[1].action==='page.tools');
    settingState=saved;showPage('keyboard');
    check('custom-preset-and-recorder',[...document.querySelectorAll('.shortcut-row [data-chord]')].every(node=>node.tagName==='SELECT'&&node.options[node.options.length-1].value==='record'));
    suiteState=await Native.call('suite.read');showPage('keyboard');
    const action='screen-translator.original',selected=document.getElementById('suite-'+action.replaceAll('.','-'));
    const confirmation=await performAction('suite-shortcut',{action});
    const savedLabel=document.querySelector(`[data-action="suite-shortcut"][data-args*="${action}"]`)?.textContent;
    check('toast-only-after-persistence-and-registration',confirmation.saved===true&&confirmation.active===true&&!document.getElementById('toast').hidden&&document.getElementById('toast').textContent==='Shortcut saved.',{confirmation,savedLabel});
    const after=await Native.call('suite.read');const beforeChord=after.bindings.find(item=>item.action===action).chord;
    let rejected=false;try{await Native.call('suite.shortcut',{action,chord:'Win+L'});}catch{rejected=true;}
    const retained=await Native.call('suite.read');
    check('rejected-save-retains-binding',rejected&&retained.bindings.find(item=>item.action===action).chord===beforeChord);
    const button=document.querySelector('.MiButton.secondary');const base=getComputedStyle(button).backgroundColor;
    const rules=[...document.styleSheets].flatMap(sheet=>{try{return [...sheet.cssRules];}catch{return [];}});
    const preview=document.createElement('button');preview.className=button.className;document.body.append(preview);
    const hover=rules.find(rule=>rule.selectorText?.includes('.MiButton.secondary:hover:not(:disabled)'));
    const pressed=rules.find(rule=>rule.selectorText?.includes('.MiButton.secondary:active:not(:disabled)'));
    preview.style.background=hover?.style.background;const hoverColor=getComputedStyle(preview).backgroundColor;
    preview.style.background=pressed?.style.background;const pressedColor=getComputedStyle(preview).backgroundColor;preview.remove();
    check('button-hover-and-pressed-distinct',!!hover&&!!pressed&&new Set([base,hoverColor,pressedColor]).size===3,{base,hoverColor,pressedColor});
    showPage('tools');
    const gaps=[...document.querySelectorAll('.MiCard>.inline-note+button')].map(button=>{const parent=button.parentElement, box=parent.getBoundingClientRect(), note=button.previousElementSibling.getBoundingClientRect(), rect=button.getBoundingClientRect();return {above:rect.top-note.bottom,below:box.bottom-rect.bottom-parseFloat(getComputedStyle(parent).borderBottomWidth)};});
    check('toolbox-button-spacing',gaps.length>=3&&gaps.every(gap=>gap.above>=21&&Math.abs(gap.above-gap.below)<2),{gaps});
  } catch(error) {check('request-completed',false,{error:error.message});}
  finally {chrome.webview.postMessage({validationToken:token,result:{passed:rows.every(row=>row.status==='pass'),rows,desktopCaptured:false,hardwareChanged:false}});}
};
