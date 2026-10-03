/* Purpose: load the preserved Xiaomi React UI through an independent local bridge.
 * Dependencies: bridge.js and the supplied Xiaomi bundle. Outputs: floating UI.
 * Launch: python -m xiaomi_search desktop.
 */
import('../bridge.js').then(() => {
  // Patch bundle calls to use localBridge.native rather than replacing the real
  // chrome.webview transport, which pywebview itself needs for Python RPC.
  return import('./assets/index.js');
}).then(() => {
  const feedback = document.createElement('div');
  feedback.id = 'local-feedback';
  feedback.textContent = 'LOCAL · FILENAME + CONTENT + MEANING';
  document.body.append(feedback);
  const manager = document.createElement('button');
  manager.id = 'local-manager';
  manager.textContent = 'Library';
  manager.title = 'Manage indexed folders and local model · Ctrl+,';
  manager.onclick = () => window.localBridge.call('local_manage').catch(error => window.localBridge.feedback(error.message, true));
  document.body.append(manager);
});
