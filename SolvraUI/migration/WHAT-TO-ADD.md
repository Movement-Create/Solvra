# Solvra migration — what to add & every assumption I made

Drop-in migration package for the `new-design` branch of
`Movement-Create/Solvra`. Every path below is relative to `vscode-extension/`
unless noted.

---

## Files in this package

| File | Goes to | Purpose |
|---|---|---|
| `resources/solvra-crest.svg` | `resources/solvra-crest.svg` | New brand mark (14–96px) |
| `resources/solvra-crest-lg.svg` | `resources/solvra-crest-lg.svg` | Hero variant for README/marketplace |
| `media/solvra-fonts.css` | `media/solvra-fonts.css` | `@font-face` declarations for Inter + JetBrains Mono |
| `media/chat-v2.css` | `media/chat-v2.css` | New crest-branded stylesheet |
| `media/chat-v2.js` | `media/chat-v2.js` | Glue: CONTEXT label, pill wiring, cancel-btn observer |
| `src/chat-html.ts` | `src/chat-html.ts` | Webview HTML template, split legacy vs v2 |
| `src/sessions-provider-v2.ts` | `src/sessions-provider.ts` (overwrite) | Date-bucketed session tree |
| `src/pick-model.ts` | `src/pick-model.ts` | ⌘K model picker command |

---

## Fonts (you asked — they were missing from the earlier drops)

The design uses **Inter** for UI and **JetBrains Mono** for code. VS Code
webviews have strict CSP and can't reliably load Google Fonts, so the fonts
must be self-hosted with the extension.

### 1. Ship the font files

```bash
mkdir -p vscode-extension/media/fonts
cd vscode-extension/media/fonts
```

Download and drop these 6 woff2 files into `media/fonts/`:

- **Inter** — https://github.com/rsms/inter/releases → grab `Inter.zip` → copy from `Web/`:
  - `Inter-Regular.woff2`
  - `Inter-Medium.woff2`
  - `Inter-SemiBold.woff2`
  - `Inter-Bold.woff2`
- **JetBrains Mono** — https://github.com/JetBrains/JetBrainsMono/releases → `webfonts/`:
  - `JetBrainsMono-Regular.woff2`
  - `JetBrainsMono-Medium.woff2`

Both are OFL-1.1 licensed — free for commercial use. Drop each project's
`OFL.txt` next to the woff2 files (one `OFL.txt` per family).

Total size: ~500KB–1MB gzipped. Acceptable for a VS Code extension.

### 2. The CSS is already wired

- `media/solvra-fonts.css` has all the `@font-face` rules.
- `chat-v2.css` sets `--font: 'Inter', var(--vscode-font-family, …)` and
  `--mono: 'JetBrains Mono', var(--vscode-editor-font-family, …)`. If the
  woff2 files are missing, the fallback chain gracefully uses VS Code's
  theme fonts — nothing breaks.
- `chat-html.ts` already adds the `<link rel="stylesheet" href="${fontsUri}">`
  **and** adds `font-src ${webview.cspSource}` to the CSP.

### 3. Commit

```bash
git add vscode-extension/media/fonts vscode-extension/media/solvra-fonts.css
git commit -m "design: ship Inter + JetBrains Mono webfonts"
```

---

## What to edit in your repo

### `package.json`

```jsonc
{
  "contributes": {
    "viewsContainers": {
      "activitybar": [
        { "id": "solvra-sidebar", "title": "Solvra",
          "icon": "resources/solvra-crest.svg" }   // ← was icon.svg
      ]
    },
    "views": {
      "solvra-sidebar": [
        { "type": "tree", "id": "solvra.sessionsView", "name": "Sessions",
          "icon": "resources/solvra-crest.svg",    // ← was icon.svg
          "contextualTitle": "Solvra Sessions" },
        { "type": "webview", "id": "solvra.chatView", "name": "Chat",
          "icon": "resources/solvra-crest.svg",    // ← was icon.svg
          "contextualTitle": "Solvra" }
      ]
    },
    "commands": [
      { "command": "solvra.askSolvra", "title": "Solvra: Ask Solvra",
        "category": "Solvra", "icon": "resources/solvra-crest.svg" },   // ← icon added
      { "command": "solvra.pickModel", "title": "Solvra: Pick Model",
        "category": "Solvra", "icon": "$(sparkle)" }                    // ← NEW
    ],
    "menus": {
      "editor/title": [                                                 // ← NEW block
        { "command": "solvra.askSolvra", "group": "navigation",
          "when": "resourceScheme == file" }
      ]
    },
    "keybindings": [
      { "command": "solvra.askSolvra", "key": "alt+cmd+s",              // ← NEW
        "mac": "alt+cmd+s", "win": "alt+ctrl+s" },
      { "command": "solvra.pickModel", "key": "ctrl+k m",               // ← NEW
        "mac": "cmd+k m" }
    ],
    "configuration": {
      "properties": {
        "solvra.ui.version": {                                          // ← NEW
          "type": "string", "default": "legacy",
          "enum": ["legacy", "v2"],
          "description": "Which chat UI to render. Set to v2 to preview the new design."
        },
        "solvra.model": {                                               // ← NEW
          "type": "string", "default": "claude-sonnet-4",
          "description": "Default model. Use Solvra: Pick Model (⌘K M) to change."
        }
      }
    }
  }
}
```

### `src/chat-provider.ts`

Replace `_getHtml` method body with:

```ts
import { getChatHtml, UiVersion } from './chat-html';

private _getHtml(webview: vscode.Webview): string {
  const version = vscode.workspace
    .getConfiguration('solvra')
    .get<UiVersion>('ui.version', 'legacy');
  return getChatHtml(webview, this._extensionUri, version);
}
```

Delete the standalone `getNonce()` function at the bottom (it moved into `chat-html.ts`).

### `src/extension.ts`

```ts
import { registerPickModel } from './pick-model';
// inside activate():
registerPickModel(context);
```

### `src/commands.ts`

Make `solvra.askSolvra` fall back to the whole document when nothing is
selected (currently it requires a selection).

### `src/sessions-provider.ts` (overwrite with `sessions-provider-v2.ts`)

Two imports at the top must match your real files:

```ts
import { SessionStore } from './session-store';   // ← your path
import type { Session } from './types';           // ← your type
```

Your `Session` type must expose `id: string`, `title: string`,
`updatedAt: number` (ms). If your field is named differently (e.g. `mtime`,
`lastUsed`), rename all three `updatedAt` references in the new file.

If your `SessionStore` has no `onDidChange` event, delete the line that
subscribes to it and call `provider.refresh()` manually wherever you
create/delete sessions.

---

## Assumptions I made without asking — please accept, reject, or tweak

These are UX/UI choices I baked into the design unilaterally. Flag any that
are wrong.

### 1. Markdown rendering — kept your existing parser
I styled the output (code blocks, blockquotes, headers, lists, links) but
didn't touch the markdown → HTML pipeline. Whatever renders today still
renders. **Open question:** want syntax-highlighted code blocks? I did not
add highlight.js — code blocks are monochrome unless you already load one.

### 2. Link color = VS Code theme link (not amber)
Inside message bodies, links use `--vscode-textLink-foreground`, not the
Solvra amber. Conservative choice. Say if you'd rather they be amber.

### 3. Tool-use block markup — assumed a specific structure
The `.collapsible` / `.collapsible-header` / `.collapsible-body` classes
in my CSS assume chat.js emits:
```html
<div class="collapsible">
  <div class="collapsible-header">
    <span class="collapsible-chevron">▸</span>
    <span class="collapsible-icon">🔧</span>
    <span class="collapsible-label">read_file</span>
    <span class="collapsible-badge">12ms</span>
  </div>
  <div class="collapsible-body">…tool output…</div>
</div>
```
**If your chat.js emits different markup for tool calls, my styles silently
don't apply.** Send me a snippet of real tool-call HTML and I'll rename
classes to match.

### 4. Message labels: `YOU` / `SOLVRA` above each turn
I added `::before` pseudo-elements rendering uppercase labels above every
message. The `SOLVRA` label is amber with a tiny inline crest; `YOU` is
muted gray. **Alternatives:** avatars, no labels, only-SOLVRA, or initials.
Tell me your preference.

### 5. Empty state copy
"What can I help you with?" with three hints (`/` for commands, `@` for
files, `Shift+Enter` for newline) — placeholder copy. Give me your preferred
welcome copy and hints.

### 6. Model list in the picker is illustrative, not real
`pick-model.ts` hardcodes: Sonnet 4, Sonnet 3.7, Haiku 4.5, Opus 4, GPT-5,
GPT-5 mini — with **made-up prices and context windows for display**. Before
shipping replace with your real supported models + real pricing, or wire to
your existing provider registry.

### 7. Model pill cycles on click (not open the picker)
`chat-v2.js` cycles the model pill through a short hardcoded list. The ⌘K
picker is a separate command. **This is confusing UX** — same pill click
yields a different model each time. Recommend: kill cycling, make pill open
`solvra.pickModel`. See docs snippet in the commit history for how to wire it.

### 8. Mode pill hardcodes `agent / ask / edit`
If your product doesn't have a mode concept, **hide the mode pill entirely**
by deleting the `#mode-pill` block from chat-html.ts. I guessed based on
common agentic IDE patterns.

### 9. Selections persist in `localStorage`, not your SessionStore
Model/mode choices from the pills live in the webview's localStorage.
Reinstall / different window = lost. Intentional for draft UI, but real
implementation should read/write `solvra.model` through the host.

### 10. Session bucket boundaries = today / yesterday / 7-day / earlier
No "this month" or "last month" bucket. Tweakable in one function
(`bucketOf`) in `sessions-provider-v2.ts`.

### 11. Session tooltip
I added a Markdown tooltip on hover showing title + full timestamp. If your
existing provider had a different tooltip, this overwrites it.

### 12. Cancel button class observer (slightly hacky)
`chat-v2.js` uses a MutationObserver to mirror `style.display` → `.visible`
class because chat.js toggles via inline style but my CSS needs a class
(absolute positioning requires `display: flex`, not `display: block`).
Cleaner fix: change chat.js to toggle a class directly. I avoided touching
chat.js to keep the migration surgical.

### 13. "Attach" pill synthesizes `@` into the input
Clicking "Attach" inserts `@` and fires an `input` event, assuming your
existing autocomplete reacts to that. If your attach flow is different
(a specific command, a file picker), rewire the click handler in
`chat-v2.js`.

---

## Things I did NOT add (implementation notes for when you're ready)

These weren't in the mockups as working features, but you might expect them.
Each has rough scope + where to start.

### A. Inline diff rendering for tool edits
**Scope:** medium (1–2 days). **Where:** `chat.js` parser + new CSS.
When a tool call writes to a file, render the edit as a unified diff with
red/green line backgrounds instead of a collapsible blob. Requires:
- Detect `str_replace` / `write` tool calls in chat.js.
- Parse old_string / new_string into `<del>` / `<ins>` lines.
- New `.diff-block`, `.diff-add`, `.diff-del` CSS classes.
- Optional: syntax-highlight each side via `highlight.js`.

### B. Streaming cursor / typing indicator
**Scope:** small (2 hours). **Where:** `chat.js` + `chat-v2.css`.
A blinking block cursor at the end of the currently-streaming assistant
message, replacing (or alongside) the progress dots. Append a
`<span class="stream-cursor">▋</span>` while streaming, remove when done.
CSS: `@keyframes blink` on opacity.

### C. Hover message actions (copy / retry / edit)
**Scope:** medium (half day). **Where:** `chat.js` renderer + new CSS.
Show a 3-button toolbar (copy markdown, retry from this turn, edit user
message) on `.msg:hover`. Each button fires a postMessage to the host:
`{type:'copyMessage', id}`, `{type:'retryFrom', id}`, `{type:'editUser', id}`.
Host handlers go in `chat-provider.ts`'s `onDidReceiveMessage`. Retry/edit
need session-mutation APIs in your SessionStore.

### D. Session search / filter
**Scope:** large (2–3 days). **Where:** full webview rebuild of the sidebar.
VS Code TreeView can't host a search input. You'd need to replace the
`solvra.sessionsView` from `"type": "tree"` to `"type": "webview"` and build
a custom list component. Big change. Only worth it if sidebar fidelity is
important.

### E. Amber active-row highlight in session tree
**Scope:** blocked by (D). **Why:** TreeView items can't be per-item styled
in VS Code. Only achievable via the webview rebuild.

### F. Keyboard shortcuts overlay (⌘/)
**Scope:** small (3 hours). **Where:** new file `media/shortcuts-overlay.js` +
command `solvra.showShortcuts`. A floating card listing keybindings.
Trivial but needs the shortcut list written out.

### G. Drag-and-drop files onto composer
**Scope:** small (3 hours). **Where:** `chat-v2.js`.
Listen for `dragover` / `drop` on `#input-container`, extract dropped
file paths (VS Code webviews receive `text/uri-list` on drops from the
Explorer), post `{type:'attachFiles', paths}` to the host. Host resolves
paths and injects chips.

### H. Custom-styled model modal
**Scope:** medium (1 day). **Where:** new webview panel.
Replace the QuickPick with a floating webview panel styled like the
close-up mockup (model cards with pricing, context window, provider badge).
`vscode.window.createWebviewPanel` with `viewType: 'solvra.modelPicker'`.
Only worth it if you want pixel fidelity to the mockup.

### I. Host-side handling of `setMode` / `setModel` postMessages
**Scope:** tiny (30 min). **Where:** `chat-provider.ts`.
The v2 UI already posts these when the pills change. Add:
```ts
case 'setMode':  this._mode  = msg.value; break;
case 'setModel': this._model = msg.value; break;
```
…and thread `this._mode` / `this._model` into your provider call.

---

## Suggested commit order

One commit per bullet — easy to revert any piece independently.

1. `design: ship Inter + JetBrains Mono webfonts`
2. `design: new Solvra crest` — drop in SVGs + swap three icon references
3. `feat: editor-title Ask Solvra button + ui.version flag` — package.json edits
4. `design: add chat-v2.css + chat-v2.js + chat-html.ts (behind flag)` — new UI hidden by default
5. `refactor: wire ui.version switch in chat-provider.ts` — the `_getHtml` replacement
6. `feat(sidebar): group sessions by date` — the new sessions provider
7. `feat: ⌘K model picker` — pick-model.ts + package.json entries
8. *(later, after dogfooding)* `design: default to v2 UI` — flip `solvra.ui.version` default to `"v2"`
9. *(next release)* `chore: remove legacy chat UI` — delete `chat.css`, legacy branch of `chat-html.ts`, and the flag

---

## Preview the new UI

After all phases are applied, set in your user settings:

```jsonc
{ "solvra.ui.version": "v2" }
```

Reload the Extension Development Host (F5). The chat view reskins to the
new crest-branded design with Inter + JetBrains Mono. Flip back to
`"legacy"` any time.
