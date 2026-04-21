/*
  chat-v2.js — v2-only UI glue. Loaded AFTER chat.js.

  Responsibilities:
   1. Wrap #file-chips in a #context-block with a "CONTEXT ATTACHED" label
      that auto-shows when chips exist and auto-hides when empty.
   2. Cancel-button visibility shim (mirror inline style → class).
   3. Wire the model pill to open the host's model picker (solvra.pickModel).
   4. Cycle the mode pill locally; announce changes to the host via postMessage.
   5. Seed pills from an 'initPills' message and update them on 'modelChanged'
      so the webview reflects the real extension config, not localStorage.
   6. Attach pill → synthesize '@' so chat.js's file autocomplete opens.

  The vscode API is owned by chat.js (which calls acquireVsCodeApi once) and
  handed to us via window.__solvraVscode.
*/
(function () {
  'use strict';

  const $ = (sel, root = document) => root.querySelector(sel);
  const vscode = window.__solvraVscode;
  const post = (msg) => { try { vscode && vscode.postMessage(msg); } catch (_) {} };

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
    block.appendChild(chips);
    const sync = () => {
      block.classList.toggle('visible', chips.children.length > 0);
    };
    sync();
    new MutationObserver(sync).observe(chips, { childList: true });
  }

  // ── 2. Cancel button visibility shim ────────────────────────────────
  const cancelBtn = $('#cancel-btn');
  if (cancelBtn) {
    const sync = () => {
      const shown = cancelBtn.style.display && cancelBtn.style.display !== 'none';
      cancelBtn.classList.toggle('visible', !!shown);
    };
    sync();
    new MutationObserver(sync).observe(cancelBtn, { attributes: true, attributeFilter: ['style'] });
  }

  // ── 3. Model pill → opens solvra.pickModel via the host ─────────────
  const modelPill = $('#model-pill');
  const modelLabelEl = modelPill && modelPill.querySelector('[data-model-label]');

  // Shorten a full model id like "claude-sonnet-4-20250514" to "sonnet-4"
  // so the pill stays tight. Unknown shapes pass through (clipped).
  const shortModel = (m) => {
    if (!m) return '';
    let s = String(m);
    s = s.replace(/-\d{8}$/, '');          // drop date suffix
    s = s.replace(/^claude-/, '');
    s = s.replace(/^gpt-/, 'gpt-');
    s = s.replace(/^gemini-/, 'gemini-');
    return s.length > 22 ? s.slice(0, 20) + '…' : s;
  };

  const setModelLabel = (model) => {
    if (modelLabelEl && model) {
      modelLabelEl.textContent = shortModel(model);
      modelPill.title = 'Model: ' + model + ' — click to change';
    }
  };

  if (modelPill) {
    modelPill.addEventListener('click', () => {
      post({ type: 'pickModel' });
    });
  }

  // ── 4. Mode pill cycles locally (no host picker exists yet) ─────────
  const MODES = ['agent', 'ask', 'edit'];
  const modePill = $('#mode-pill');
  const modeLabelEl = modePill && modePill.querySelector('[data-mode-label]');

  const setModeLabel = (mode) => {
    if (modeLabelEl && mode) modeLabelEl.textContent = mode;
  };

  if (modePill && modeLabelEl) {
    let current = localStorage.getItem('solvra.ui.mode') || MODES[0];
    if (!MODES.includes(current)) current = MODES[0];
    setModeLabel(current);

    modePill.addEventListener('click', () => {
      const i = MODES.indexOf(current);
      current = MODES[(i + 1) % MODES.length];
      setModeLabel(current);
      localStorage.setItem('solvra.ui.mode', current);
      post({ type: 'setMode', value: current });
    });
  }

  // ── 5. Seed pills from host + react to config changes ───────────────
  window.addEventListener('message', (event) => {
    const msg = event.data;
    if (!msg || typeof msg !== 'object') return;
    switch (msg.type) {
      case 'initPills':
        if (msg.model) setModelLabel(msg.model);
        if (msg.mode)  setModeLabel(msg.mode);
        break;
      case 'modelChanged':
        if (msg.value) setModelLabel(msg.value);
        break;
    }
  });

  // Tell the host we're alive and want initial state.
  post({ type: 'requestInitPills' });

  // ── 6. Attach pill → synthesize "@" into the input ──────────────────
  const attachPill = $('#attach-pill');
  const input = $('#input');
  if (attachPill && input) {
    attachPill.addEventListener('click', () => {
      input.focus();
      const v = input.value;
      const needsSpace = v.length > 0 && !/\s$/.test(v);
      input.value = v + (needsSpace ? ' @' : '@');
      input.dispatchEvent(new Event('input', { bubbles: true }));
    });
  }
})();
