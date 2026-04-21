# Phase 4 — `chat-provider.ts` changes

Two surgical edits. No logic changes, just extraction + a config read.

---

## 4a. Replace the inline `_getHtml` with the split template

**Add this import at the top of `src/chat-provider.ts`:**

```ts
import { getChatHtml, UiVersion } from './chat-html';
```

**Replace the entire `_getHtml` method** (the last ~100 lines of the class) with:

```ts
  private _getHtml(webview: vscode.Webview): string {
    const version = (vscode.workspace
      .getConfiguration('solvra')
      .get<UiVersion>('ui.version', 'legacy'));
    return getChatHtml(webview, this._extensionUri, version);
  }
```

**Delete** the free-standing `getNonce()` function at the bottom of the file — it's now inside `chat-html.ts`.

---

## 4b. Re-render when the setting changes (optional but nice)

In `resolveWebviewView`, after wiring the message listener, add:

```ts
    // Re-render when the UI version flag flips
    const cfgSub = vscode.workspace.onDidChangeConfiguration((e) => {
      if (e.affectsConfiguration('solvra.ui.version') && this._view) {
        this._view.webview.html = this._getHtml(this._view.webview);
      }
    });
    webviewView.onDidDispose(() => cfgSub.dispose());
```

Without this the user has to reload the window after flipping `solvra.ui.version`; with it, the chat re-skins on save.

---

## 4c. Verify `chat.js` still works unchanged

Both templates keep these IDs and classes intact — **do not rename them**:

- `#header`, `#header-left`, `#header-right`, `.header-btn`, `#new-session-btn`, `#open-tab-btn`
- `#messages`, `.msg`, `.msg-user`, `.msg-assistant`, `.msg-error`
- `.collapsible`, `.collapsible-header`, `.collapsible-chevron`, `.collapsible-body`, `.collapsible-badge`
- `.progress-container`, `.progress-dots`, `.progress-dot`, `.progress-label`, `#progress`
- `.empty-state`, `#empty-state`
- `#file-chips`, `.file-chip`, `.chip-remove`
- `#input-wrapper`, `#input-area`, `#input-container`, `#input`, `#input-footer`, `#char-count`
- `#autocomplete`, `.ac-item`, `.ac-name`, `.ac-desc`, `.ac-icon`
- `#send-btn`, `#cancel-btn`, `.action-btn`

Everything chat.js touches is preserved. Only the **visual skin** and the **header brand area** change.
