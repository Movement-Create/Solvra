# Webfonts for Solvra v2 UI

The v2 chat UI uses **Inter** (UI text) and **JetBrains Mono** (code). The
`@font-face` rules in `../solvra-fonts.css` load the six `.woff2` files in
this directory.

## Bundled files

| File | Weight | Family | Size |
|---|---|---|---|
| `Inter-Regular.woff2`          | 400 | Inter            | ~23 KB |
| `Inter-Medium.woff2`           | 500 | Inter            | ~24 KB |
| `Inter-SemiBold.woff2`         | 600 | Inter            | ~24 KB |
| `Inter-Bold.woff2`             | 700 | Inter            | ~24 KB |
| `JetBrainsMono-Regular.woff2`  | 400 | JetBrains Mono   | ~21 KB |
| `JetBrainsMono-Medium.woff2`   | 500 | JetBrains Mono   | ~22 KB |

All six files are the **Latin subset** pulled from the
[Fontsource CDN](https://fontsource.org/) (jsdelivr) — sufficient for English
and most Western European languages at ~140 KB total. If you need Latin-Ext,
Cyrillic, or Greek, replace these with the full-range files from the upstream
projects:

- Inter — https://github.com/rsms/inter/releases → `Inter.zip` / `Web/`
- JetBrains Mono — https://github.com/JetBrains/JetBrainsMono/releases → `webfonts/`

Keep the filenames listed above so `solvra-fonts.css` continues to resolve.

## Licenses

Both fonts are licensed under **SIL Open Font License 1.1**:

- `Inter-OFL.txt` — covers the Inter family.
- `JetBrainsMono-OFL.txt` — covers the JetBrains Mono family.

OFL-1.1 allows commercial redistribution when the license text is included
alongside the fonts (done here).

## Fallback

If any of these files are deleted or fail to load, `chat-v2.css` falls back
through `var(--vscode-font-family, …)` and `var(--vscode-editor-font-family, …)`,
so the UI keeps working with the user's theme fonts.
