# Phase 2 — `package.json` changes

Apply these edits to `vscode-extension/package.json`. They are minimal and additive — legacy behavior unchanged unless `solvra.ui.version` is flipped to `"v2"`.

---

## 2a. Swap the activity-bar / view icons to the crest

Change all three `"icon": "resources/icon.svg"` references to the new crest.

```diff
   "viewsContainers": {
     "activitybar": [
       {
         "id": "solvra-sidebar",
         "title": "Solvra",
-        "icon": "resources/icon.svg"
+        "icon": "resources/solvra-crest.svg"
       }
     ]
   },
   "views": {
     "solvra-sidebar": [
       {
         "type": "tree",
         "id": "solvra.sessionsView",
         "name": "Sessions",
-        "icon": "resources/icon.svg",
+        "icon": "resources/solvra-crest.svg",
         "contextualTitle": "Solvra Sessions"
       },
       {
         "type": "webview",
         "id": "solvra.chatView",
         "name": "Chat",
-        "icon": "resources/icon.svg",
+        "icon": "resources/solvra-crest.svg",
         "contextualTitle": "Solvra"
       }
     ]
   },
```

Optional — add a top-level marketplace icon (PNG required for marketplace, but the SVG works for local dev):

```diff
   "categories": ["AI", "Other"],
+  "icon": "resources/solvra-crest.svg",
```

---

## 2b. Add the editor-title "Ask Solvra" button

The command `solvra.askSolvra` already exists. Re-register it with an icon and surface it in `editor/title`.

```diff
     "commands": [
       { "command": "solvra.openChat", "title": "Open Chat", "category": "Solvra", "icon": "$(comment-discussion)" },
       ...
-      { "command": "solvra.askSolvra", "title": "Solvra: Ask Solvra", "category": "Solvra" },
+      { "command": "solvra.askSolvra", "title": "Solvra: Ask Solvra", "category": "Solvra", "icon": "resources/solvra-crest.svg" },
       ...
     ],
     "menus": {
       ...
+      "editor/title": [
+        {
+          "command": "solvra.askSolvra",
+          "group": "navigation",
+          "when": "resourceScheme == file"
+        }
+      ],
       "editor/context": [ ... ]
     },
     "keybindings": [
       { "command": "solvra.openChat", "key": "ctrl+shift+a", "mac": "cmd+shift+a" },
+      { "command": "solvra.askSolvra", "key": "alt+cmd+s", "mac": "alt+cmd+s", "win": "alt+ctrl+s" },
       ...
     ],
```

**Note:** `solvra.askSolvra` currently expects `editorHasSelection`. Update its handler in `commands.ts` to also work on the whole file when nothing is selected (fall back to the active document's content).

---

## 2c. Add the UI-version feature flag

```diff
     "configuration": {
       "title": "Solvra",
       "properties": {
+        "solvra.ui.version": {
+          "type": "string",
+          "default": "legacy",
+          "enum": ["legacy", "v2"],
+          "enumDescriptions": [
+            "Current chat UI",
+            "New crest-branded UI (preview)"
+          ],
+          "description": "Which chat UI to render. Flip to v2 to preview the new design."
+        },
         "solvra.provider": { ... },
         ...
       }
     }
```
