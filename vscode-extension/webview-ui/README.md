# Solvra chat webview (v3)

React + Vite webview for the Solvra VS Code extension. It is built into `../media/v3` and loaded by
`src/chat-html.ts#getV3Html` under a strict CSP: scripts and styles come only from the extension,
and fonts and icons are bundled locally.

```sh
npm ci
npm run build    # typecheck, vite build → ../media/v3, THIRD-PARTY-NOTICES.txt
npm test         # vitest: the stream-event reducer (src/store.test.ts)
```

The extension's `vscode:prepublish` runs this build, so the VSIX always carries a fresh bundle.
`media/v3` is not committed.

## How it fits together

- `../src/protocol.ts`: the types shared by host and webview. This covers backend `stream-json`
  events (see `docs/stream-json.md`), host→webview messages and webview→host messages.
- `../src/v3-controller.ts`: one per chat (sidebar or tab). Runs `solvra run --output-format
  stream-json`, answers approvals, and handles sessions, keys (SecretStorage), settings, memory,
  tools and file search.
- `src/store.ts`: the pure reducer that turns events into the transcript (text, steps, nested
  sub-agents, approvals, plan, usage, run summary).
- `src/components/*`: header and session menu, transcript, tool lines and diffs, approval prompt,
  composer (chips, `@`, `/`, mode, model, context ring), settings, memory and tools views.

## Design tokens

Every colour, font, spacing and motion value lives in `src/styles/tokens.css`. Neutrals map to
VS Code theme variables, so the panel follows the user's theme. The slate accent (`#5f6f8f` light,
`#9aa8c7` dark) and the black/white primary button are Solvra's own. Values marked **reconcile**
were not given in the brief and must be checked against `Plans/solvra-chat-redesign.html` (the
approved mockup, which is not in this repository).

Libraries (pinned exactly): React 19, Radix Popover, Streamdown (markdown), Shiki (JavaScript
regex engine: no WASM, no eval), @vscode/codicons, Motion, Fontsource Geist / Geist Mono / IBM Plex
Sans Arabic.

## UI tests

- `npm run shots -- <outDir>` (`harness/shoot.mjs`) renders 24 states in Chromium with a mock host
  and VS Code-like theme variables (light, dark, high contrast). It also checks two keyboard paths:
  pressing `2` on an approval sends `allow_always`, and ⇧Tab cycles the mode. It fails on any
  console error. Set `CHROME=/path/to/chrome` if Playwright's own browser is not installed, and
  `SCALE=1` for 1× images.
- `npm run live -- <outDir> ["Default Light Modern"]` (`harness/live.mjs`) launches an isolated
  VS Code (`VSCODE=/path/to/code`, short `--user-data-dir /tmp/svprof`, `--password-store=basic`,
  `ELECTRON_*`/`VSCODE_*` removed) and connects over CDP (Playwright's `_electron.launch` fails on
  current VS Code builds). It drives the real backend in a throwaway workspace (`/tmp/svdemo`) with
  its own sessions folder and a $0.25 budget. Checks:
  - ask mode waits for approval and leaves the file unchanged until you answer
  - deny with a message
  - parallel sub-agents in auto mode
  - the settings view, and a plain-text key migrated out of `settings.json`
  - session titles
  - what the command palette shows for "Solvra"
- `harness/live-before.mjs` captures the previous (v2) chat in the same setup, for comparisons.
