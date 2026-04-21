/*
  chat-v2.js — v2-only UI glue. Loaded AFTER chat.js.

  Responsibilities:
   1. Wrap #file-chips in a #context-block with a "CONTEXT ATTACHED" label
      that auto-shows when chips exist and auto-hides when empty.
   2. Wire the model/mode pills: click to cycle, persist in localStorage,
      announce changes to the extension host via postMessage so chat-provider
      can read them on send. (Host can ignore the message if not yet handled;
      the label still flips locally.)
   3. Keep everything defensive — chat.js might not have rendered chips yet.

  No changes needed in chat.js.
*/
(function () {
  'use strict';

  const $ = (sel, root = document) => root.querySelector(sel);
  const vscode = (typeof acquireVsCodeApi === 'function') ? null : null; // chat.js already grabbed it
  const post = (msg) => {
    try { window.parent.postMessage(msg, '*'); } catch (_) {}
  };

  // ── 1. CONTEXT ATTACHED wrapper ────────────────────────────────────
  const chips = $('#file-chips');
  if (chips && !$('#context-block')) {
    const block = document.createElement('div');
    block.id = 'context-block';
    const label = document.createElement('div');
    label.className = 'context-label';
    label.textContent = 'Context attached';
    block.appendChild(label);
    chips.parentNode.insertBefore(block, chips);
    block.appendChild(chips); // move chips inside the wrapper
    const sync = () => {
      block.classList.toggle('visible', chips.children.length > 0);
    };
    sync();
    new MutationObserver(sync).observe(chips, { childList: true });
  }

  // ── 2. Cancel button visibility shim ────────────────────────────────
  // chat.js toggles #cancel-btn via style.display — with the new absolute
  // positioning we prefer a class. Observe it and mirror to `.visible`.
  const cancelBtn = $('#cancel-btn');
  if (cancelBtn) {
    const sync = () => {
      const shown = cancelBtn.style.display && cancelBtn.style.display !== 'none';
      cancelBtn.classList.toggle('visible', !!shown);
    };
    sync();
    new MutationObserver(sync).observe(cancelBtn, { attributes: true, attributeFilter: ['style'] });
  }

  // ── 3. Model + mode pills ──────────────────────────────────────────
  const MODES  = ['agent', 'ask', 'edit'];
  const MODELS = ['sonnet-4', 'sonnet-3.7', 'haiku-4.5', 'opus-4'];

  const cycle = (pillId, labelAttr, options, storageKey, msgType) => {
    const pill = $('#' + pillId);
    if (!pill) return;
    const labelEl = pill.querySelector('[' + labelAttr + ']');
    let current = localStorage.getItem(storageKey) || options[0];
    if (!options.includes(current)) current = options[0];
    if (labelEl) labelEl.textContent = current;
    pill.addEventListener('click', () => {
      const i = options.indexOf(current);
      current = options[(i + 1) % options.length];
      if (labelEl) labelEl.textContent = current;
      localStorage.setItem(storageKey, current);
      post({ type: msgType, value: current });
    });
  };

  cycle('mode-pill',  'data-mode-label',  MODES,  'solvra.ui.mode',  'setMode');
  cycle('model-pill', 'data-model-label', MODELS, 'solvra.ui.model', 'setModel');

  // Attach pill → synthesize "@" keystroke so chat.js opens the file picker.
  const attachPill = $('#attach-pill');
  const input = $('#input');
  if (attachPill && input) {
    attachPill.addEventListener('click', () => {
      input.focus();
      // Append "@" and dispatch input event so existing autocomplete fires.
      const v = input.value;
      const needsSpace = v.length > 0 && !/\s$/.test(v);
      input.value = v + (needsSpace ? ' @' : '@');
      input.dispatchEvent(new Event('input', { bubbles: true }));
    });
  }
})();
