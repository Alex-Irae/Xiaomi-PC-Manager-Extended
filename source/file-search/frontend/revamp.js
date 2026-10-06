/* Purpose: the bottom notice and the undo/redo commands shared by PC Manager, AI Center, Screen Translator
 * and FileSync. Dependencies: a #toast element in the page and revamp.css. Outputs: none.
 * Edit only source/shared/ui and run tools/sync_ui.py; the copies inside each app's frontend folder are overwritten. */
'use strict';
const Revamp = (() => {
  let timer = 0;
  // Shows one line at the bottom of the window. `undo` adds an Undo button to it.
  function toast(message, {error = false, undo = null} = {}) {
    const box = document.getElementById('toast');
    box.textContent = message;
    box.classList.toggle('error', error);
    if (undo) {
      const button = document.createElement('button');
      button.type = 'button'; button.textContent = 'Undo'; button.addEventListener('click', undo);
      box.append(button);
    }
    box.hidden = false;
    clearTimeout(timer);
    timer = setTimeout(() => { box.hidden = true; }, error ? 8000 : 5000);
  }
  // Wires the two top-bar buttons and Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z to step(forward).
  // Keys are left to the field while typing, so text keeps its own undo.
  function history(undo, redo, step) {
    undo.addEventListener('click', () => step(false));
    redo.addEventListener('click', () => step(true));
    document.addEventListener('keydown', event => {
      if (!event.ctrlKey || event.altKey || event.metaKey || event.target?.matches?.('input,textarea,[contenteditable=true]')) return;
      const key = String(event.key).toLowerCase(), forward = key === 'y' || key === 'z' && event.shiftKey;
      if (!forward && key !== 'z') return;
      event.preventDefault();
      if (!(forward ? redo : undo).disabled) step(forward);
    });
  }
  return {toast, history};
})();
