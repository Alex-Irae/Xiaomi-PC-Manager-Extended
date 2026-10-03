/* Purpose: focused real-WebView preset, swap and spacing checks.
 * Dependencies: native bridge and production app.js/suite.js. Outputs: native JSON report.
 * Command: PCManager.exe --validate-suite. Native host restores bindings and custom actions. */
globalThis.runSuiteValidation=async function(token) {
  const rows=[];
  const check=(name,ok,details={})=>rows.push({name,status:ok?'pass':'fail',...details});
  try {
    suiteState=await Native.call('suite.read');settingState=await Native.call('settings.read');showPage('keyboard');
    const choices=[...document.querySelectorAll('.suite-shortcut-list select')];
    check('preset-list-and-last-recorder',choices.length===6&&choices.every(node=>node.options[node.options.length-1].value==='record'&&[...node.options].some(option=>option.value==='Copilot')));
    check('redundant-copilot-section-removed',![...document.querySelectorAll('.MiCard h2')].some(node=>node.textContent==='Copilot key'));
    const intro=document.querySelector('.shortcut-intro').getBoundingClientRect(), first=document.querySelector('.suite-shortcut-list .MiSettingRow').getBoundingClientRect();
    check('intro-spacing',first.top-intro.bottom>=22,{gap:first.top-intro.bottom});
    const old=Object.fromEntries(suiteState.bindings.map(item=>[item.action,item.chord]));
    await Native.call('suite.shortcut',{action:'screen-translator.filter',chord:old['screen-translator.toggle']});
    let shared=await Native.call('suite.read');let values=Object.fromEntries(shared.bindings.map(item=>[item.action,item.chord]));
    check('copilot-action-swap',values['screen-translator.filter']===old['screen-translator.toggle']&&values['screen-translator.toggle']===old['screen-translator.filter']);
    await Native.call('settings.shortcuts',{shortcuts:[{chord:'Ctrl+Alt+Shift+F21',action:'page.keyboard'},{chord:'Ctrl+Alt+Shift+F22',action:'page.tools'}]});
    await Native.call('suite.shortcut',{action:'screen-translator.filter',chord:'Ctrl+Alt+Shift+F21'});
    let saved=await Native.call('settings.read');
    check('suite-to-custom-swap',saved.shortcuts[0].chord===old['screen-translator.toggle']&&saved.shortcuts[0].action==='page.keyboard');
    await Native.call('settings.shortcuts',{shortcuts:[{chord:'Ctrl+Alt+Shift+F22',action:'page.keyboard'},{chord:'Ctrl+Alt+Shift+F22',action:'page.tools'}]});
    saved=await Native.call('settings.read');
    check('custom-swap-retains-actions',saved.shortcuts[0].chord==='Ctrl+Alt+Shift+F22'&&saved.shortcuts[1].chord===old['screen-translator.toggle']&&saved.shortcuts[0].action==='page.keyboard'&&saved.shortcuts[1].action==='page.tools');
    settingState=saved;showPage('keyboard');
    check('custom-preset-and-recorder',[...document.querySelectorAll('.shortcut-row [data-chord]')].every(node=>node.tagName==='SELECT'&&node.options[node.options.length-1].value==='record'));
    showPage('tools');
    const gaps=[...document.querySelectorAll('.MiCard>.inline-note+button')].map(button=>{const parent=button.parentElement, box=parent.getBoundingClientRect(), note=button.previousElementSibling.getBoundingClientRect(), rect=button.getBoundingClientRect();return {above:rect.top-note.bottom,below:box.bottom-rect.bottom-parseFloat(getComputedStyle(parent).borderBottomWidth)};});
    check('toolbox-button-spacing',gaps.length>=3&&gaps.every(gap=>gap.above>=21&&Math.abs(gap.above-gap.below)<2),{gaps});
  } catch(error) {check('request-completed',false,{error:error.message});}
  finally {chrome.webview.postMessage({validationToken:token,result:{passed:rows.every(row=>row.status==='pass'),rows,desktopCaptured:false,hardwareChanged:false}});}
};
