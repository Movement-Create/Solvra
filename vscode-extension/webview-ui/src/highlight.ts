// Shiki for code blocks, loaded on first use. JavaScript regex engine: no WASM, no eval, so it
// works under the webview CSP. A fixed set of grammars keeps the bundle small.
import type { CodeHighlighterPlugin, HighlightOptions, HighlightResult } from 'streamdown';
import type { HighlighterCore } from 'shiki/core';

const LANGS = ['typescript', 'tsx', 'javascript', 'jsx', 'json', 'python', 'csharp', 'bash', 'shellscript', 'html', 'css', 'markdown', 'yaml', 'diff', 'go', 'rust', 'java', 'sql', 'xml', 'toml'] as const;
const ALIASES: Record<string, string> = { ts: 'typescript', js: 'javascript', sh: 'bash', shell: 'bash', zsh: 'bash', cs: 'csharp', 'c#': 'csharp', py: 'python', yml: 'yaml', md: 'markdown', rs: 'rust' };
const THEMES = ['min-light', 'min-dark'] as const;

let highlighter: HighlighterCore | null = null;
let loading: Promise<HighlighterCore> | null = null;

function load(): Promise<HighlighterCore> {
  loading ??= (async () => {
    const [{ createHighlighterCore }, { createJavaScriptRegexEngine }] = await Promise.all([import('shiki/core'), import('shiki/engine/javascript')]);
    highlighter = await createHighlighterCore({
      themes: [import('shiki/themes/min-light.mjs'), import('shiki/themes/min-dark.mjs')],
      langs: [
        import('shiki/langs/typescript.mjs'), import('shiki/langs/tsx.mjs'), import('shiki/langs/javascript.mjs'), import('shiki/langs/jsx.mjs'),
        import('shiki/langs/json.mjs'), import('shiki/langs/python.mjs'), import('shiki/langs/csharp.mjs'), import('shiki/langs/bash.mjs'),
        import('shiki/langs/html.mjs'), import('shiki/langs/css.mjs'), import('shiki/langs/markdown.mjs'), import('shiki/langs/yaml.mjs'),
        import('shiki/langs/diff.mjs'), import('shiki/langs/go.mjs'), import('shiki/langs/rust.mjs'), import('shiki/langs/java.mjs'),
        import('shiki/langs/sql.mjs'), import('shiki/langs/xml.mjs'), import('shiki/langs/toml.mjs'),
      ],
      engine: createJavaScriptRegexEngine({ forgiving: true }),
    });
    return highlighter;
  })();
  return loading;
}

const norm = (lang: string) => ALIASES[lang?.toLowerCase()] ?? lang?.toLowerCase();
const isDark = () => document.body.classList.contains('vscode-dark') || document.body.classList.contains('vscode-high-contrast');

function tokens(h: HighlighterCore, o: HighlightOptions): HighlightResult {
  const lang = norm(o.language);
  const r = h.codeToTokens(o.code, { lang: h.getLoadedLanguages().includes(lang) ? lang : 'text', theme: isDark() ? 'min-dark' : 'min-light' });
  // Keep the panel's own background: only token colours come from the theme.
  return { tokens: r.tokens.map((line) => line.map((t) => ({ content: t.content, color: t.color }))) };
}

export const shikiPlugin: CodeHighlighterPlugin = {
  name: 'shiki',
  type: 'code-highlighter',
  getSupportedLanguages: () => [...LANGS] as never,
  getThemes: () => [...THEMES] as never,
  supportsLanguage: (lang) => (LANGS as readonly string[]).includes(norm(lang as string)),
  highlight(options, callback) {
    if (highlighter) return tokens(highlighter, options);
    void load().then((h) => callback?.(tokens(h, options)));
    return null;
  },
};
