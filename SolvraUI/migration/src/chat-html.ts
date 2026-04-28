/**
 * chat-html.ts — webview HTML templates, split by UI version.
 *
 * Drop this file at: vscode-extension/src/chat-html.ts
 * Then import it from chat-provider.ts (see phase-4-chat-provider.md).
 *
 * Both templates:
 *  - Use the same element IDs (#header, #messages, #input, #send-btn, etc.)
 *    so chat.js works unchanged against either.
 *  - Keep the same CSP contract.
 *  - Only visual markup + crest branding differ.
 */
import * as vscode from 'vscode';

export type UiVersion = 'legacy' | 'v2';

function getNonce(): string {
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  let nonce = '';
  for (let i = 0; i < 32; i++) nonce += chars.charAt(Math.floor(Math.random() * chars.length));
  return nonce;
}

export function getChatHtml(
  webview: vscode.Webview,
  extensionUri: vscode.Uri,
  version: UiVersion
): string {
  const nonce = getNonce();

  const cssFile = version === 'v2' ? 'chat-v2.css' : 'chat.css';
  const cssUri = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'media', cssFile));
  const fontsUri = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'media', 'solvra-fonts.css'));
  const jsUri = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'media', 'chat.js'));
  const v2JsUri = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'media', 'chat-v2.js'));
  const crestUri = webview.asWebviewUri(
    vscode.Uri.joinPath(extensionUri, 'resources', 'solvra-crest.svg')
  );

  if (version === 'v2') {
    return v2Html({ webview, nonce, cssUri, fontsUri, jsUri, v2JsUri, crestUri });
  }
  return legacyHtml({ webview, nonce, cssUri, jsUri });
}

// ─── v2 (new crest-branded design) ──────────────────────────────────────

function v2Html(p: {
  webview: vscode.Webview;
  nonce: string;
  cssUri: vscode.Uri;
  fontsUri: vscode.Uri;
  jsUri: vscode.Uri;
  v2JsUri: vscode.Uri;
  crestUri: vscode.Uri;
}): string {
  const { webview, nonce, cssUri, fontsUri, jsUri, v2JsUri, crestUri } = p;
  return /* html */ `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<meta http-equiv="Content-Security-Policy"
  content="default-src 'none'; img-src ${webview.cspSource} data:; font-src ${webview.cspSource}; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}';">
<link rel="stylesheet" href="${fontsUri}">
<link rel="stylesheet" href="${cssUri}">
</head>
<body data-ui-version="v2">
  <div id="header">
    <div id="header-left">
      <!-- Solvra crest, inline so it inherits currentColor cleanly -->
      <svg class="solvra-crest" viewBox="0 0 100 100" fill="none" stroke="currentColor" stroke-linejoin="round" stroke-linecap="round" aria-hidden="true">
        <path d="M50 6 L90 20 L86 60 Q82 82 50 96 Q18 82 14 60 L10 20 Z" stroke-width="8"/>
        <path d="M36 36 L50 30 L64 36" stroke-width="9"/>
        <path d="M36 50 L50 44 L64 50" stroke-width="9"/>
        <path d="M36 64 L50 58 L64 64" stroke-width="9"/>
      </svg>
      <span>Solvra</span>
    </div>
    <div id="header-right">
      <button class="header-btn" id="new-session-btn" title="New Session">
        <svg viewBox="0 0 16 16" fill="currentColor"><path d="M8 2a.75.75 0 01.75.75v4.5h4.5a.75.75 0 010 1.5h-4.5v4.5a.75.75 0 01-1.5 0v-4.5h-4.5a.75.75 0 010-1.5h4.5v-4.5A.75.75 0 018 2z"/></svg>
      </button>
      <button class="header-btn" id="open-tab-btn" title="Open in Editor Tab">
        <svg viewBox="0 0 16 16" fill="currentColor"><path d="M3.5 1h9A1.5 1.5 0 0114 2.5v11a1.5 1.5 0 01-1.5 1.5h-9A1.5 1.5 0 012 13.5v-11A1.5 1.5 0 013.5 1zm0 1a.5.5 0 00-.5.5v11a.5.5 0 00.5.5h9a.5.5 0 00.5-.5v-11a.5.5 0 00-.5-.5h-9zM5 4h6v1.5H5V4z"/></svg>
      </button>
    </div>
  </div>

  <div id="messages">
    <div class="empty-state" id="empty-state">
      <svg class="empty-state-icon" viewBox="0 0 100 100" fill="none" stroke="currentColor" stroke-linejoin="round" stroke-linecap="round" aria-hidden="true">
        <path d="M50 6 L90 20 L86 60 Q82 82 50 96 Q18 82 14 60 L10 20 Z" stroke-width="6"/>
        <path d="M36 36 L50 30 L64 36" stroke-width="7"/>
        <path d="M36 50 L50 44 L64 50" stroke-width="7"/>
        <path d="M36 64 L50 58 L64 64" stroke-width="7"/>
      </svg>
      <div class="empty-state-title">What can I help you with?</div>
      <div class="empty-state-subtitle">Ask questions, write code, debug issues, or explore your codebase.</div>
      <div class="empty-state-hints">
        <span><kbd>/</kbd> for commands</span>
        <span><kbd>@</kbd> to attach files</span>
        <span><kbd>Shift+Enter</kbd> for new line</span>
      </div>
    </div>
  </div>

  <div class="progress-container" id="progress">
    <div class="progress-dots">
      <div class="progress-dot"></div><div class="progress-dot"></div><div class="progress-dot"></div>
    </div>
    <div class="progress-label" id="progress-label">Thinking…</div>
  </div>

  <div id="file-chips"></div>

  <div id="input-wrapper">
    <div id="autocomplete"></div>
    <div id="input-area">
      <div id="input-container">
        <textarea id="input" rows="1" placeholder="Follow up…"></textarea>
        <div id="input-footer">
          <span>Shift+Enter for new line</span>
          <span id="char-count"></span>
        </div>
        <button class="action-btn" id="send-btn" title="Send (Enter)" aria-label="Send">
          <svg viewBox="0 0 16 16" fill="currentColor"><path d="M8 1.5a.75.75 0 01.53.22l4.5 4.5a.75.75 0 01-1.06 1.06L8.75 4.06v9.69a.75.75 0 01-1.5 0V4.06L4.03 7.28A.75.75 0 012.97 6.22l4.5-4.5A.75.75 0 018 1.5z"/></svg>
        </button>
        <button class="action-btn" id="cancel-btn" title="Stop" aria-label="Stop">
          <svg viewBox="0 0 16 16" fill="currentColor"><rect x="3" y="3" width="10" height="10" rx="1"/></svg>
        </button>
      </div>

      <!-- Model / mode pill row -->
      <div id="composer-pills">
        <button class="composer-pill accent" id="mode-pill" title="Switch mode" type="button">
          <svg viewBox="0 0 16 16" fill="currentColor" aria-hidden="true"><path d="M8 1.5l5.5 3.25v6.5L8 14.5 2.5 11.25v-6.5L8 1.5zM4 6.15v4.2L8 12.7l4-2.35v-4.2L8 3.8 4 6.15z"/></svg>
          <span data-mode-label>agent</span>
          <span class="chev">▾</span>
        </button>
        <button class="composer-pill" id="model-pill" title="Switch model" type="button">
          <svg viewBox="0 0 16 16" fill="currentColor" aria-hidden="true"><circle cx="8" cy="8" r="3"/><circle cx="8" cy="8" r="6.5" fill="none" stroke="currentColor" stroke-width="1.2" opacity="0.5"/></svg>
          <span data-model-label>sonnet-4</span>
          <span class="chev">▾</span>
        </button>
        <span class="composer-pill-spacer"></span>
        <button class="composer-pill" id="attach-pill" title="Attach file (@)" type="button">
          <svg viewBox="0 0 16 16" fill="currentColor" aria-hidden="true"><path d="M10.5 3.5l-5 5a2.5 2.5 0 003.54 3.54l5-5a4 4 0 00-5.66-5.66l-5.5 5.5a.5.5 0 00.71.71l5.5-5.5a3 3 0 014.24 4.24l-5 5a1.5 1.5 0 01-2.12-2.12l5-5a.5.5 0 00-.71-.71z"/></svg>
          <span>Attach</span>
        </button>
      </div>
    </div>
  </div>

  <!-- Preload crest for ::before backgrounds -->
  <link rel="preload" as="image" href="${crestUri}">

<script nonce="${nonce}" src="${jsUri}"></script>
<script nonce="${nonce}" src="${v2JsUri}"></script>
</body>
</html>`;
}

// ─── legacy (unchanged from current repo) ───────────────────────────────

function legacyHtml(p: {
  webview: vscode.Webview;
  nonce: string;
  cssUri: vscode.Uri;
  jsUri: vscode.Uri;
}): string {
  const { webview, nonce, cssUri, jsUri } = p;
  return /* html */ `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<meta http-equiv="Content-Security-Policy"
  content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
<link rel="stylesheet" href="${cssUri}">
</head>
<body data-ui-version="legacy">
  <div id="header">
    <div id="header-left">Solvra</div>
    <div id="header-right">
      <button class="header-btn" id="new-session-btn" title="New Session">
        <svg viewBox="0 0 16 16" fill="currentColor"><path d="M8 2a.75.75 0 01.75.75v4.5h4.5a.75.75 0 010 1.5h-4.5v4.5a.75.75 0 01-1.5 0v-4.5h-4.5a.75.75 0 010-1.5h4.5v-4.5A.75.75 0 018 2z"/></svg>
      </button>
      <button class="header-btn" id="open-tab-btn" title="Open in Editor Tab">
        <svg viewBox="0 0 16 16" fill="currentColor"><path d="M3.5 1h9A1.5 1.5 0 0114 2.5v11a1.5 1.5 0 01-1.5 1.5h-9A1.5 1.5 0 012 13.5v-11A1.5 1.5 0 013.5 1zm0 1a.5.5 0 00-.5.5v11a.5.5 0 00.5.5h9a.5.5 0 00.5-.5v-11a.5.5 0 00-.5-.5h-9zM5 4h6v1.5H5V4z"/><path d="M10.854 7.146a.5.5 0 010 .708l-3 3a.5.5 0 01-.708-.708l3-3a.5.5 0 01.708 0z"/><path d="M7.146 7.146a.5.5 0 01.708 0l3 3a.5.5 0 01-.708.708l-3-3a.5.5 0 010-.708z"/></svg>
      </button>
    </div>
  </div>

  <div id="messages">
    <div class="empty-state" id="empty-state">
      <div class="empty-state-icon">S</div>
      <div class="empty-state-title">What can I help you with?</div>
      <div class="empty-state-subtitle">Ask questions, write code, debug issues, or explore your codebase.</div>
      <div class="empty-state-hints">
        <span><kbd>/</kbd> for commands</span>
        <span><kbd>@</kbd> to attach files</span>
        <span><kbd>Shift+Enter</kbd> for new line</span>
      </div>
    </div>
  </div>

  <div class="progress-container" id="progress">
    <div class="progress-dots">
      <div class="progress-dot"></div><div class="progress-dot"></div><div class="progress-dot"></div>
    </div>
    <div class="progress-label" id="progress-label">Thinking...</div>
  </div>

  <div id="file-chips"></div>

  <div id="input-wrapper">
    <div id="autocomplete"></div>
    <div id="input-area">
      <div id="input-container">
        <textarea id="input" rows="1" placeholder="Ask Solvra... (/ for commands, @ for files)"></textarea>
        <div id="input-footer">
          <span>Shift+Enter for new line</span>
          <span id="char-count"></span>
        </div>
      </div>
      <button class="action-btn" id="send-btn" title="Send (Enter)">
        <svg viewBox="0 0 16 16" fill="currentColor"><path d="M1.724 1.053a.5.5 0 01.555-.033l12 7a.5.5 0 010 .86l-12 7A.5.5 0 011.5 15.5V.5a.5.5 0 01.224-.447zM3 2.31v4.19h4.5a.5.5 0 010 1H3v4.19l9.144-4.69L3 2.31z"/></svg>
        Send
      </button>
      <button class="action-btn" id="cancel-btn" title="Stop">
        <svg viewBox="0 0 16 16" fill="currentColor"><rect x="3" y="3" width="10" height="10" rx="1"/></svg>
        Stop
      </button>
    </div>
  </div>

<script nonce="${nonce}" src="${jsUri}"></script>
</body>
</html>`;
}
