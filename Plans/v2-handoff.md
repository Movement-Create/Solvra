# Solvra v2 UI — handoff for a fresh agent

Repo: `c:\AI\Solvra` · Branch: `new-design` · Base branch: `main`
Extension root: `vscode-extension/`

## 1. What's already done

Commits on `new-design` (oldest first):

| SHA | What it shipped |
|---|---|
| `722e0de` | Copied `resources/solvra-crest.svg` + `solvra-crest-lg.svg`. Swapped three `resources/icon.svg` references in `package.json` (activitybar, sessionsView, chatView) to the crest. |
| `7e038c4` | Added `"icon"` to the `solvra.askSolvra` command, a new `editor/title` menu entry, the `alt+ctrl+s` / `alt+cmd+s` keybinding, and the `solvra.ui.version` (`legacy` \| `v2`) config property. Updated `solvra.askSolvra` in `src/commands.ts` to fall back to the whole active document when there's no selection. |
| `51d9c51` | Added `media/chat-v2.css` (initial version). |
| `2d81d88` | Created `src/chat-html.ts` with `getChatHtml(webview, extensionUri, version)` and `UiVersion` type. Replaced the inline `_getHtml` + `getNonce()` in `src/chat-provider.ts` with a call to `getChatHtml`. Added a live-reload hook: `onDidChangeConfiguration('solvra.ui.version')` re-renders the webview without a window reload. |
| `349eeed` | Flipped `solvra.ui.version.default` from `"legacy"` to `"v2"`. |
| `4682d57` | Fixed `src/session-panel-manager.ts` — it used to have its own hardcoded HTML that always loaded `chat.css` + the legacy `"S"` empty state, so opening a session in an editor tab ignored the flag. Now it routes through `getChatHtml` and re-renders on `solvra.ui.version` change. |
| `6dff572` | (Author: Ahmed, not the agent.) Rolled up earlier v2 work — safe to ignore, state is consistent. |
| `10671cd` | **Phase 4b** — copied an updated `media/chat-v2.css`, new `media/chat-v2.js`, and updated `src/chat-html.ts`. Introduced the "CONTEXT ATTACHED" label over `#file-chips`, a model/mode pill row (`#mode-pill` / `#model-pill` / `#attach-pill`), and a capsule-style composer with inline circular Send. |

Typecheck (`npx tsc --noEmit -p .`) passes after every commit. No tests exist in the extension package.

## 2. How the v2 path is wired today

- `package.json` → `solvra.ui.version` defaults to `"v2"`.
- `src/chat-provider.ts` (sidebar) and `src/session-panel-manager.ts` (editor tab) both read the flag and call `getChatHtml(webview, extensionUri, version)` from `src/chat-html.ts`.
- `src/chat-html.ts` picks `chat-v2.css` + `chat-v2.js` for `v2`, or `chat.css` alone for `legacy`. For v2 it renders markup that includes `#composer-pills` (mode/model/attach) and the inline Send inside `#input-container`.
- `media/chat-v2.js` runs **after** `chat.js` and:
  1. Wraps `#file-chips` in a `#context-block` with a `.context-label` "Context attached", toggling `.visible` based on whether chips exist (MutationObserver on `#file-chips`).
  2. Mirrors `#cancel-btn` inline `display` style to a `.visible` class so the v2 CSS can position it absolutely.
  3. Cycles `#mode-pill` through `['agent','ask','edit']` and `#model-pill` through `['sonnet-4','sonnet-3.7','haiku-4.5','opus-4']` on click; persists to `localStorage`; emits `{ type: 'setMode'|'setModel', value }`.
  4. Makes `#attach-pill` synthesize an `@` keystroke into `#input` so the existing file-picker opens.

## 3. Known pending / broken items

### P0 — `setMode` / `setModel` postMessages never reach the extension host
`media/chat-v2.js` emits these with `window.parent.postMessage(msg, '*')`:

```js
const post = (msg) => { try { window.parent.postMessage(msg, '*'); } catch (_) {} };
```

In a VS Code webview, messages to the host must go through the API returned by `acquireVsCodeApi()`. `window.parent.postMessage` does not reach `webview.onDidReceiveMessage`. `chat.js` has already called `acquireVsCodeApi()` and it can only be called once per webview, so `chat-v2.js` cannot call it again.

Effect today: pills flip their label and persist to `localStorage`, but the runner's model/mode is never updated. `chat-provider.ts` and `session-panel-manager.ts` do **not** have `case 'setMode':` / `case 'setModel':` in their `onDidReceiveMessage` switches (verified).

Two things must happen together:

**a. Make the message actually leave the webview.** Pick one:
- Preferred: in `media/chat.js`, after `const vscode = acquireVsCodeApi()`, expose it as `window.__solvraVscode = vscode`. Then in `chat-v2.js`, replace the `post` function with `const post = (m) => window.__solvraVscode?.postMessage(m)`.
- Alternative: in `chat-v2.js`, dispatch a `CustomEvent('solvra:host', { detail: msg })` on `document` and have `chat.js` add a listener that forwards `e.detail` through its own `vscode.postMessage`.

**b. Handle the messages in both hosts.**
- `src/chat-provider.ts` — add to the switch inside `webviewView.webview.onDidReceiveMessage` (around line 41):
  ```ts
  case 'setModel':
    this._runner.setConfig({ model: message.value });
    break;
  case 'setMode':
    // there is no 'mode' concept in AgentRunner today — either
    // (i) persist to globalState and send through as a system-prompt prefix,
    // (ii) map 'ask'/'edit'/'agent' to maxTurns + autoApprove combinations, or
    // (iii) no-op and leave it as a UI-only indicator. Decide with the user.
    break;
  ```
- `src/session-panel-manager.ts` — same two cases in its `panel.webview.onDidReceiveMessage` switch (around line 108).

Check `AgentRunner.setConfig` signature in `src/agent-runner.ts` before wiring — you may need to look up which provider the new model belongs to (there's a `models` table in `session-panel-manager.ts:141-150` that maps model → provider; consider extracting it to a shared module).

### P1 — user reports "still some missing parts"
The user has seen the reference mock once (the screenshot with `CONTEXT ATTACHED` + `YOU` / `SOLVRA` labels + pill row). Ask the user to name what's still missing before guessing. Likely suspects:
- Model pills show hardcoded names (`sonnet-4`, `haiku-4.5`, etc.) that don't match what's in the provider table in `session-panel-manager.ts:141-150` and don't match `solvra.provider` / `solvra.model` settings. Initial pill value is `localStorage.getItem('solvra.ui.model') || 'sonnet-4'` — it should seed from the extension config instead.
- Pills do not reflect settings changed elsewhere (e.g. via `/model` slash command). No bidirectional sync.
- Mode pill shows `agent` by default but there's no runtime difference between modes.
- Attach pill types `@` but if the input already has text, spacing is naive (see `chat-v2.js:82-88`).
- `#cancel-btn` visibility is driven by a MutationObserver watching `style.display`. If `chat.js` ever switches to toggling a class instead, the observer goes silent. Fragile.

### P2 — Phase 7 deferred per original plan
Removing the legacy path (delete `media/chat.css`, drop `legacyHtml` from `chat-html.ts`, remove the `solvra.ui.version` config). Do not do this until v2 has shipped a release and no one is using `legacy`.

### P3 — Minor cleanups
- `chat-v2.js:19` — `const vscode = (typeof acquireVsCodeApi === 'function') ? null : null;` is dead code. Remove.
- `src/session-panel-manager.ts:_getHtml(webview, _title)` keeps `_title` only for signature shape. Drop the parameter if no other caller needs it.
- There's no top-level marketplace `icon` in `package.json` — the marketplace requires PNG ≥128×128 and SVG is rejected (tried during Phase 1, reverted). If you want one, export a PNG from `resources/solvra-crest.svg` and add `"icon": "resources/solvra-crest.png"`.

## 4. Relevant files (read these first)

| File | Why |
|---|---|
| `vscode-extension/src/chat-html.ts` | Both sidebar & editor-tab webviews render from here. v2 markup is in `v2Html()`. |
| `vscode-extension/media/chat-v2.js` | v2-only glue. All pill-cycling + context-label logic lives here. |
| `vscode-extension/media/chat-v2.css` | v2 visual layer. Selectors match `chat-html.ts` markup. |
| `vscode-extension/media/chat.js` | Shared webview logic — owns the single `acquireVsCodeApi()` call, file picker, streaming, markdown render. |
| `vscode-extension/src/chat-provider.ts` | Sidebar webview host. `onDidReceiveMessage` switch at line 41. |
| `vscode-extension/src/session-panel-manager.ts` | Editor-tab webview host. `onDidReceiveMessage` switch at line 108. Mirror any switch change here. |
| `vscode-extension/src/agent-runner.ts` | `AgentRunner.setConfig({ model, provider, maxTurns, ... })` is how the runner picks up changes. Read before wiring `setModel`. |
| `vscode-extension/package.json` | `solvra.ui.version` at `configuration.properties`. Existing model config at `solvra.model`, provider at `solvra.provider`. |

## 5. Suggested execution order for the next agent

1. **Ask the user** to enumerate "what's still missing" — don't guess from screenshots, there are too many degrees of freedom in the reference design.
2. Fix the `setMode` / `setModel` plumbing (P0 above). One commit. Smallest safe change.
3. Seed the pills from extension config instead of `localStorage` defaults, so a fresh install shows the real current model. Add bidirectional sync (extension → webview via `postMessage` when config changes). Another commit.
4. Whatever the user listed in step 1.
5. Update the README in `SolvraUI/migration/README.md` to describe Phase 4b as landed, and document the `setMode` / `setModel` wiring in a new Phase 4c section.

## 6. Verification checklist

After changes:
- `cd vscode-extension && npx tsc --noEmit -p .` — no errors.
- F5 → Extension Development Host.
- Sidebar Solvra view renders v2 UI (crest header, capsule composer, pills under input).
- Open a session in editor tab (click the "open in tab" icon) — same v2 UI, no `"S"` empty state.
- Click `#mode-pill` / `#model-pill` — label cycles, and Solvra output panel / `_runner` actually switches model (after P0 fix).
- Settings → set `"solvra.ui.version": "legacy"` → both surfaces flip to legacy without window reload.
- Settings → `"solvra.ui.version": "v2"` → both flip back.

## 7. What NOT to do

- Do not `git rebase` / squash the commits on `new-design`. The per-phase commits are the audit trail and rollback story.
- Do not delete `media/chat.css` or `legacyHtml` yet (Phase 7 is deferred).
- Do not add a top-level `package.json` `icon` with an SVG — VS Code marketplace rejects it (already tried, reverted).
- Do not call `acquireVsCodeApi()` a second time from `chat-v2.js` — it throws.
