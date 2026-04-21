# Phase 4c — Session bucketing + ⌘K model picker

Two separate additions, each in its own file. Ship together or separately.

---

## 4c-1. Date-bucketed session tree

Replaces the flat session list with collapsible **Today / Yesterday / This week / Earlier** groups.

```bash
cp path/to/migration/src/sessions-provider-v2.ts vscode-extension/src/sessions-provider.ts
```

Check two imports at the top of the copied file and rewire to your real names:

```ts
import { SessionStore } from './session-store';   // ← match your file path
import type { Session } from './types';           // ← match your type file
```

Also make sure your `Session` type has:

- `id: string`
- `title: string`
- `updatedAt: number` (ms since epoch) — rename `updatedAt` in the new file if your field is called something else (e.g. `mtime`, `lastUsed`).

If your `SessionStore` doesn't expose an `onDidChange` event, remove line 53
and call `provider.refresh()` manually wherever you save/delete sessions.

```bash
git add vscode-extension/src/sessions-provider.ts
git commit -m "feat(sidebar): group sessions by date — Today / Yesterday / This week / Earlier"
```

**Limitations:** VS Code TreeView styling is locked down — the active-session
amber row from the close-up isn't achievable here without rebuilding the view
as a webview. This TreeView version ships today; webview version is a separate
project (Phase 5+).

---

## 4c-2. ⌘K model picker

Adds a **Solvra: Pick Model** command bound to `⌘K M` that opens a rich
QuickPick with model name, price, context window, and a checkmark on the
current selection.

```bash
cp path/to/migration/src/pick-model.ts vscode-extension/src/
```

Edit `vscode-extension/src/extension.ts`:

```diff
+ import { registerPickModel } from './pick-model';

  export function activate(context: vscode.ExtensionContext) {
    ...
+   registerPickModel(context, (modelId) => {
+     // Re-render the chat view so the model pill reflects the new value.
+     // (Only needed if you wired the pill to read from settings; safe to omit.)
+     chatProvider.refreshModel?.(modelId);
+   });
  }
```

Edit `vscode-extension/package.json`:

```diff
     "commands": [
+      { "command": "solvra.pickModel", "title": "Solvra: Pick Model", "category": "Solvra", "icon": "$(sparkle)" },
       ...
     ],
     "keybindings": [
+      { "command": "solvra.pickModel", "key": "ctrl+k m", "mac": "cmd+k m" },
       ...
     ],
     "configuration": {
       "properties": {
+        "solvra.model": {
+          "type": "string",
+          "default": "claude-sonnet-4",
+          "description": "Default model. Use Solvra: Pick Model (⌘K M) to change."
+        },
         ...
       }
     }
```

Optional — link the v2 chat pill to the picker. In `chat-v2.js`, replace the
`cycle('model-pill', …)` call with:

```js
const modelPill = document.getElementById('model-pill');
if (modelPill) {
  const labelEl = modelPill.querySelector('[data-model-label]');
  // Ask host for current model on mount
  post({ type: 'getModel' });
  window.addEventListener('message', (e) => {
    if (e.data?.type === 'modelChanged' && labelEl) labelEl.textContent = e.data.model;
  });
  modelPill.addEventListener('click', () => post({ type: 'pickModel' }));
}
```

And in `chat-provider.ts`'s `onDidReceiveMessage`:

```ts
case 'pickModel':
  vscode.commands.executeCommand('solvra.pickModel');
  break;
case 'getModel':
  webview.postMessage({
    type: 'modelChanged',
    model: vscode.workspace.getConfiguration('solvra').get('model'),
  });
  break;
```

```bash
git add vscode-extension/src/pick-model.ts vscode-extension/src/extension.ts \
        vscode-extension/package.json vscode-extension/media/chat-v2.js
git commit -m "feat: ⌘K model picker + chat pill wiring"
```

---

## What this phase does NOT do

- **Pixel-match the close-up's active-row amber highlight in the session tree** — VS Code TreeView doesn't allow per-item styling. A webview rebuild of the sessions panel is needed for full fidelity.
- **Pixel-match the close-up's custom modal** for model selection — VS Code QuickPick is the idiomatic choice and the closest we can get without a webview modal.

If you want either of those at pixel fidelity, that's a separate "Phase 5 — webview rebuild of the sidebar" project. Happy to scope that when 4a/b/c are landed.
