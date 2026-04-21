# Plan — Migrate Solvra VSCode extension to the new v2 design

## Context

The design team dropped a self-contained migration package at [SolvraUI/migration/](SolvraUI/migration/) (README, phase docs, new SVG crests, `chat-v2.css`, and a new `chat-html.ts` module). It refreshes the brand mark (shield crest) and reskins the chat webview, all gated behind a new `solvra.ui.version` setting (`legacy` | `v2`).

Goal: execute phases 1–6 — ship the crest, wire the flag, add the v2 chat UI, and flip the default to `v2`. The `legacy` path remains as an escape hatch. Each phase is a single, revertable commit.

Scope decisions (confirmed with user):
- **Phases 1–6** (flip default to `v2`).
- **Include** the optional `onDidChangeConfiguration` live-reload so toggling the flag re-renders the webview without a window reload.
- **Phase 7** (remove legacy) is explicitly deferred to a future release.

## Pre-existing state (what's already there)

Verified via exploration of [vscode-extension/](vscode-extension/):

- [vscode-extension/package.json](vscode-extension/package.json) — existing `resources/icon.svg` references at **line 28** (viewsContainers), **line 38** (sessionsView), **line 45** (chatView). `solvra.askSolvra` command at **line 62** (no `icon` yet). `editor/context` menu binding at line 86. Keybindings lines 89–96. Configuration lines 97–175 (no `solvra.ui.version` yet). No top-level marketplace `icon`.
- [vscode-extension/src/chat-provider.ts](vscode-extension/src/chat-provider.ts) — `_getHtml` at **lines 443–526** (inline HTML, loads `media/chat.css` + `media/chat.js`). Standalone `getNonce()` at **lines 530–537**.
- [vscode-extension/src/commands.ts:182-200](vscode-extension/src/commands.ts#L182-L200) — `solvra.askSolvra` **requires a selection** and warns + returns if none. Must be updated to fall back to the whole active document (README Phase 2 requirement).
- [vscode-extension/resources/](vscode-extension/resources/) — only `icon.svg` exists.
- [vscode-extension/media/](vscode-extension/media/) — `chat.css` and `chat.js` exist; no `chat-v2.css`.
- [vscode-extension/src/](vscode-extension/src/) — no `chat-html.ts` exists.

Migration assets ready to copy from [SolvraUI/migration/](SolvraUI/migration/):
- `resources/solvra-crest.svg`, `resources/solvra-crest-lg.svg`
- `media/chat-v2.css` (476 lines, reuses existing DOM IDs so `chat.js` stays unchanged)
- `src/chat-html.ts` (exports `getChatHtml(webview, extensionUri, version)` and `UiVersion = 'legacy' | 'v2'`; internally includes the `getNonce()` helper)

---

## Execution plan (6 commits, one per phase)

### Phase 1 — Ship the crest

**Copy:**
- `SolvraUI/migration/resources/solvra-crest.svg` → `vscode-extension/resources/solvra-crest.svg`
- `SolvraUI/migration/resources/solvra-crest-lg.svg` → `vscode-extension/resources/solvra-crest-lg.svg`

**Edit** [vscode-extension/package.json](vscode-extension/package.json):
- Line 28: `"icon": "resources/icon.svg"` → `"resources/solvra-crest.svg"` (viewsContainers)
- Line 38: same swap (sessionsView)
- Line 45: same swap (chatView)
- Add top-level `"icon": "resources/solvra-crest.svg"` (for marketplace listing)

**Commit:** `design: new Solvra crest — activity bar, views, marketplace icon`

**Verify:** F5 → Extension Development Host. Activity-bar + view-header icons show the new shield crest.

---

### Phase 2 — Editor-title "Ask Solvra" button + `ui.version` flag

**Edit** [vscode-extension/package.json](vscode-extension/package.json) per `phase-2-package-json.md` sections 2b & 2c:
- Add `"icon": "resources/solvra-crest.svg"` to the `solvra.askSolvra` command entry at line 62.
- Add a new `contributes.menus."editor/title"` entry:
  `{ "command": "solvra.askSolvra", "group": "navigation", "when": "resourceScheme == file" }`
- Add keybinding (append to the existing keybindings array lines 89–96):
  `{ "command": "solvra.askSolvra", "key": "alt+ctrl+s", "mac": "alt+cmd+s" }`
- Add to `configuration.properties`:
  ```jsonc
  "solvra.ui.version": {
    "type": "string",
    "default": "legacy",
    "enum": ["legacy", "v2"],
    "description": "Which chat UI to render. Flip to v2 to preview the new design."
  }
  ```

**Edit** [vscode-extension/src/commands.ts:182-200](vscode-extension/src/commands.ts#L182-L200):
- Remove the early-return on empty selection. If `editor.selection` is empty, use `editor.document.getText()` (whole document) as the prompt seed.
- Keep the existing prompt-building / `chatProvider.sendPrompt()` call unchanged.

**Commit:** `feat: editor-title Ask Solvra button + ui.version flag`

**Verify:** Reload EDH. Open any file. Crest appears in editor tab bar; click fires chat with file contents as context even with no selection. `alt+ctrl+s` on Windows triggers the same.

---

### Phase 3 — Add the v2 stylesheet (hidden behind flag)

**Copy:** `SolvraUI/migration/media/chat-v2.css` → `vscode-extension/media/chat-v2.css`

**Commit:** `design: add chat-v2.css (new crest-branded UI, behind flag)`

**Verify:** No visible change — flag still defaults to `legacy`.

---

### Phase 4 — Wire the HTML template switch + live reload

**Copy:** `SolvraUI/migration/src/chat-html.ts` → `vscode-extension/src/chat-html.ts`

**Edit** [vscode-extension/src/chat-provider.ts](vscode-extension/src/chat-provider.ts):
1. Add import near the top:
   ```ts
   import { getChatHtml, UiVersion } from './chat-html';
   ```
2. Replace the entire `_getHtml` body (lines 443–526) with:
   ```ts
   private _getHtml(webview: vscode.Webview): string {
     const version = vscode.workspace
       .getConfiguration('solvra')
       .get<UiVersion>('ui.version', 'legacy');
     return getChatHtml(webview, this._extensionUri, version);
   }
   ```
3. Delete the standalone `getNonce()` function at lines 530–537 (it now lives in `chat-html.ts`).
4. **Live-reload hook** — in `resolveWebviewView` (wherever the webview is first set up), register:
   ```ts
   const sub = vscode.workspace.onDidChangeConfiguration((e) => {
     if (e.affectsConfiguration('solvra.ui.version')) {
       webviewView.webview.html = this._getHtml(webviewView.webview);
     }
   });
   webviewView.onDidDispose(() => sub.dispose());
   ```
   (Import `vscode` is already present; dispose-on-close prevents leaks.)

**Commit:** `refactor: split chat webview HTML into chat-html.ts + ui.version switch`

**Verify:** Reload EDH. With flag still at `legacy`, UI is visually identical. No regression in chat.js behavior.

---

### Phase 5 — Dogfood v2 locally

No code change. In VS Code user settings (JSON):
```jsonc
{ "solvra.ui.version": "v2" }
```

**Verify (critical):**
- Chat view reskins to the crest-branded v2 design without reloading the window (live-reload hook from Phase 4).
- Send a message, stream a response, open a file chip, toggle sessions — confirm `chat.js` interactions all still work against the new CSS.
- Flip back to `"legacy"` → old UI returns, still live.

No commit. If issues surface, patch them on top of Phase 4 before moving on.

---

### Phase 6 — Flip the default to v2

**Edit** [vscode-extension/package.json](vscode-extension/package.json) `solvra.ui.version.default`:
- `"default": "legacy"` → `"default": "v2"`

**Commit:** `design: default to v2 UI`

**Verify:** Fresh EDH with no user override → chat view renders v2 by default. Users who want the old UI can set `"solvra.ui.version": "legacy"`.

---

## Files modified / added (summary)

**Added:**
- `vscode-extension/resources/solvra-crest.svg`
- `vscode-extension/resources/solvra-crest-lg.svg`
- `vscode-extension/media/chat-v2.css`
- `vscode-extension/src/chat-html.ts`

**Modified:**
- `vscode-extension/package.json` (icon swaps, command icon, editor/title menu, keybinding, `ui.version` config, default flip)
- `vscode-extension/src/chat-provider.ts` (import, `_getHtml` body, delete `getNonce`, add config listener)
- `vscode-extension/src/commands.ts` (`solvra.askSolvra` fallback to whole document)

**Not touched:** `chat.js`, `chat.css`, `icon.svg` (kept for rollback).

---

## End-to-end verification

1. After all 6 commits: `pnpm -w install` (or `npm install`) in `vscode-extension/`, then F5 to launch EDH.
2. **Icons:** activity bar + sidebar view headers + editor tab bar all show the shield crest.
3. **Editor-title button:** open any file → click crest → chat opens with file/selection seeded. Keybinding `alt+ctrl+s` (win) / `alt+cmd+s` (mac) does the same.
4. **Default UI:** chat view renders v2 design out of the box.
5. **Flag toggle:** set `solvra.ui.version` to `legacy` in settings → chat view flips to old design without a reload (live-reload hook). Flip back → returns to v2.
6. **Chat functionality:** send a prompt, stream a response, attach a file, open a session, clear a session — all work identically under v2.
7. **Rollback drill:** `git revert <phase-N>` on any single commit cleanly removes just that phase's change.

## Rollback matrix (from README)

| Problem | Rollback |
|---|---|
| Crest looks wrong in activity bar | `git revert <phase-1>` |
| Editor-title button misfires | `git revert <phase-2>` |
| v2 CSS breaks a specific rendering | User sets `"solvra.ui.version": "legacy"` (no revert) |
| `getChatHtml` throws | `git revert <phase-4>` and `<phase-6>` |
| Need to fully abandon | Revert phases 6→1 in reverse order |

Do not squash these commits — they are the audit trail.
