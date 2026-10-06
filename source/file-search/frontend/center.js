/* Purpose: separate AI Center launcher. Dependencies: native WebView2 bridge.
 * Outputs: local app launches and settings navigation. Launch: Launch AI Center.cmd. */
(() => {
  const picture=document.getElementById('profile-image'),brand=document.getElementById('profile-picture'),reset=document.getElementById('reset-picture');
  function showPicture(value){picture.hidden=!value.image;brand.querySelector('span').hidden=Boolean(value.image);reset.hidden=!value.image;if(value.image)picture.src=value.image;else picture.removeAttribute('src');}
  function profile(method){return localBridge.call(method).then(showPicture).catch(error=>{Revamp.toast(error.message,{error:true});});}
  brand.addEventListener('click',()=>profile('local_profile_choose'));
  reset.addEventListener('click',()=>profile('local_profile_reset'));
  profile('local_profile_get');window.addEventListener('local-center-focus',()=>profile('local_profile_get'));
  document.getElementById('window-minimize').addEventListener('click',()=>localBridge.host('minimize'));
  document.getElementById('window-close').addEventListener('click',()=>localBridge.host('close'));
  document.getElementById('window-maximize').addEventListener('click',()=>localBridge.host('maximize'));
  let fullScreen=false;
  document.addEventListener('keydown',event=>{if(event.key==='F11'||(event.key==='Escape'&&fullScreen)){event.preventDefault();localBridge.host('fullscreen');}});
  window.addEventListener('window-state-changed',event=>{fullScreen=event.detail.fullScreen;document.body.classList.toggle('expanded',event.detail.maximized||fullScreen);const button=document.getElementById('window-maximize');button.setAttribute('aria-label',event.detail.maximized||fullScreen?'Restore':'Maximize');});
  const grip=document.querySelector('[data-center-drag]');let origin=null;
  grip.addEventListener('dblclick',()=>localBridge.host('maximize'));
  grip.addEventListener('pointerdown',event=>{if(event.button!==0)return;event.preventDefault();origin={x:event.screenX,y:event.screenY};grip.setPointerCapture(event.pointerId);localBridge.host('drag_start');});
  grip.addEventListener('pointermove',event=>{if(origin)localBridge.host('drag_move',{dx:event.screenX-origin.x,dy:event.screenY-origin.y});});
  const end=()=>{origin=null;};grip.addEventListener('pointerup',end);grip.addEventListener('lostpointercapture',end);
  document.querySelectorAll('[data-page]').forEach(button=>button.addEventListener('click',()=>{
    document.querySelectorAll('[data-page]').forEach(tab=>tab.classList.toggle('active',tab===button));
    document.getElementById('home').hidden=button.dataset.page!=='home';document.getElementById('preferences').hidden=button.dataset.page!=='preferences';
  }));
  document.getElementById('launch-search').addEventListener('click',()=>localBridge.host('search'));
  document.getElementById('launch-playground').addEventListener('click',()=>localBridge.host('playground'));
  // The XiaoAI card appears only when the app is installed, with the logo read from its own folder.
  const xiaoai=document.getElementById('xiaoai-card');
  if(xiaoai){
    document.getElementById('launch-xiaoai').addEventListener('click',()=>localBridge.host('xiaoai'));
    localBridge.call('local_xiaoai').then(value=>{xiaoai.hidden=!value.installed;if(value.icon)document.getElementById('xiaoai-icon').src=value.icon;}).catch(()=>{});
  }
})();
