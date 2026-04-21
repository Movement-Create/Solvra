# Solvra — design refresh migration

Drop-in migration package for the `new-design` branch of
[Movement-Create/Solvra](https://github.com/Movement-Create/Solvra).

Every change is **additive and feature-flagged** behind a new setting,
`solvra.ui.version`. The legacy UI keeps working until you flip the default.

---

## What's in this folder

```
migration/
├── README.md                       ← you are here
├── phase-2-package-json.md         ← exact package.json diffs
├── phase-4-chat-provider.md        ← exact chat-provider.ts diffs
├── resources/
│   ├── solvra-crest.svg            ← new brand mark (toolbar-optimized)
│   └── solvra-crest-lg.svg         ← hero / marketplace variant
├── media/
│   └── chat-v2.css                 ← new webview stylesheet
└── src/
    └── chat-html.ts                ← new HTML template module
```

---

## Prereqs

```bash
# On your machine — not here
gh repo clone Movement-Create/Solvra   # or: git clone git@github.com:Movement-Create/Solvra.git
cd Solvra
git checkout main && git pull
git checkout -b new-design
```

---

## Phase 1 — Ship the crest (safest change, biggest visual win)

```bash
# From your Solvra checkout root:
cp path/to/migration/resources/solvra-crest.svg    vscode-extension/resources/
cp path/to/migration/resources/solvra-crest-lg.svg vscode-extension/resources/
```

Edit `vscode-extension/package.json` per **`phase-2-package-json.md` → section 2a**
(swap all three `resources/icon.svg` references to `resources/solvra-crest.svg`,
optionally add the top-level marketplace `icon`).

```bash
git add vscode-extension/resources/solvra-crest*.svg vscode-extension/package.json
git commit -m "design: new Solvra crest — activity bar, views, marketplace icon"
```

Reload the Extension Development Host (F5). Confirm the activity bar icon + view
headers show the new crest. **If anything is off, revert this one commit and
stop.**

---

## Phase 2 — Editor-title "Ask Solvra" button + UI-version flag

Edit `package.json` per **`phase-2-package-json.md` → sections 2b and 2c**:

- Add `icon` to the existing `solvra.askSolvra` command.
- Add the `editor/title` menu entry.
- Add the `alt+cmd+s` keybinding.
- Add the `solvra.ui.version` configuration property.

In `src/commands.ts`, make sure `solvra.askSolvra` works when nothing is
selected (fall back to the whole active document).

```bash
git add vscode-extension/package.json vscode-extension/src/commands.ts
git commit -m "feat: editor-title Ask Solvra button + ui.version flag"
```

Reload, open any file, click the crest in the editor tab bar, confirm chat
opens with a prompt seeded from the file/selection.

---

## Phase 3 — Add the v2 stylesheet (hidden behind the flag)

```bash
cp path/to/migration/media/chat-v2.css vscode-extension/media/
git add vscode-extension/media/chat-v2.css
git commit -m "design: add chat-v2.css (new crest-branded UI, behind flag)"
```

No one sees it yet — the flag still defaults to `legacy`.

---

## Phase 4 — Wire the HTML template switch

```bash
cp path/to/migration/src/chat-html.ts vscode-extension/src/
```

Edit `vscode-extension/src/chat-provider.ts` per **`phase-4-chat-provider.md`**:

1. Add the `import { getChatHtml, UiVersion } from './chat-html';` import.
2. Replace the `_getHtml` method body with the 4-line version that calls
   `getChatHtml`.
3. Delete the standalone `getNonce()` function (moved into chat-html.ts).
4. Optionally, hook `onDidChangeConfiguration` so toggling the flag re-renders
   the webview live.

```bash
git add vscode-extension/src/chat-html.ts vscode-extension/src/chat-provider.ts
git commit -m "refactor: split chat webview HTML into chat-html.ts + ui.version switch"
```

Reload. Everything should look identical (flag is `legacy`).

---

## Phase 4b — Branded composer extras (CONTEXT label, model pills, capsule input)

Adds the three elements from the reference screenshot that the baseline v2 was
missing. Self-contained; depends only on Phase 4.

```bash
cp path/to/migration/media/chat-v2.js vscode-extension/media/
# Overwrite the earlier copies with the updated versions:
cp path/to/migration/media/chat-v2.css vscode-extension/media/
cp path/to/migration/src/chat-html.ts  vscode-extension/src/
git add vscode-extension/media/chat-v2.js vscode-extension/media/chat-v2.css vscode-extension/src/chat-html.ts
git commit -m "design(v2): CONTEXT ATTACHED label, model/mode pills, capsule composer"
```

What this adds:
- **`CONTEXT ATTACHED`** pill-label row above file chips — auto-shows when
  chips exist, auto-hides when empty. No chat.js changes; it's wired via a
  MutationObserver in chat-v2.js.
- **Model + mode pill row** under the composer (`agent` / `sonnet-4` / `Attach`).
  Click cycles values, persists to localStorage, and posts `{type:'setMode'}`
  / `{type:'setModel'}` to the extension host. Host can ignore these messages
  for now — the UI still works locally.
- **Capsule composer** — rounded input with the send button inline (absolute)
  on the right instead of as a sibling. Amber focus ring.

If you want the host to actually respect model/mode selection, add handlers
in `chat-provider.ts`'s `onDidReceiveMessage` for `setMode` and `setModel`
and thread them into your provider call. That's optional — ship the visual
first.

---

## Phase 5 — Preview the new UI locally

Add this to your user settings (File → Preferences → Settings → JSON):

```jsonc
{
  "solvra.ui.version": "v2"
}
```

The chat view reskins to the new crest-branded design. `chat.js` runs unchanged.
Flip back to `"legacy"` any time.

**Dogfood for a few days.** File bugs as follow-up commits on the same branch.

---

## Phase 6 — Flip the default

When the team is happy:

```jsonc
// package.json
"solvra.ui.version": {
  ...
  "default": "v2"   // was "legacy"
}
```

```bash
git add vscode-extension/package.json
git commit -m "design: default to v2 UI"
```

---

## Phase 7 — Remove the legacy path (next release, not now)

After 1 release cycle with v2 as default and no escape-hatch complaints:

```bash
git rm vscode-extension/media/chat.css
# delete the legacyHtml function from chat-html.ts
# remove solvra.ui.version from package.json configuration
git commit -m "chore: remove legacy chat UI"
```

---

## Push the branch

```bash
git push -u origin new-design
gh pr create --base main --head new-design \
  --title "Design refresh: Solvra crest + v2 chat UI" \
  --body "Phases 1–6 implemented. Flag-gated via solvra.ui.version."
```

---

## Rollback guide

| Problem | Rollback |
|---|---|
| Crest icon looks wrong in activity bar | `git revert <phase-1 commit>` |
| Editor-title button fires wrong command | `git revert <phase-2 commit>` |
| v2 CSS breaks a specific markdown rendering | User sets `"solvra.ui.version": "legacy"` — no revert needed |
| `getChatHtml` throws on load | `git revert <phase-4 commit>` — legacy path falls back to inline markup? No — you'll also need to restore `_getHtml`. Keep the pre-edit version handy. |

Because each phase is a single commit, reverts are surgical. Don't squash
these commits before merging — keep them as the audit trail.
