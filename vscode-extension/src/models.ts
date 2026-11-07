// Model picker data: providers grouped with their auth status, plus a curated model list per
// provider. Live lists from `solvra models --json --provider <p>` replace the curated ones when the
// provider is ready (keys set / signed in). No `vscode` import.
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import type { ModelOption, ProviderGroup } from './protocol';

interface ProviderMeta { provider: string; label: string; auth: ProviderGroup['auth']; models: string[] }

export const PROVIDER_CATALOG: readonly ProviderMeta[] = [
  { provider: 'chatgpt', label: 'ChatGPT (Codex sign-in)', auth: 'codex', models: ['gpt-5.6-sol', 'gpt-5.6-luna', 'gpt-6.1-sol'] },
  { provider: 'anthropic', label: 'Anthropic', auth: 'key', models: ['claude-opus-5', 'claude-sonnet-5', 'claude-haiku-4-5'] },
  { provider: 'openai', label: 'OpenAI API', auth: 'key', models: ['gpt-4.1', 'gpt-4.1-mini', 'o3'] },
  { provider: 'google', label: 'Google Gemini', auth: 'key', models: ['gemini-2.5-pro', 'gemini-2.5-flash', 'gemini-2.0-flash-lite'] },
  { provider: 'moonshot', label: 'Moonshot (Kimi)', auth: 'key', models: ['kimi-k2.5', 'kimi-k2-thinking', 'kimi-k2-turbo-preview'] },
  { provider: 'ollama', label: 'Ollama (local)', auth: 'none', models: ['llama3.1', 'llama3.2', 'qwen3'] },
];

/** "provider:model" → parts. A bare model name is attributed to a provider by its prefix. */
export function splitModel(id: string): { provider: string | undefined; model: string } {
  const i = id.indexOf(':');
  if (i > 0 && PROVIDER_CATALOG.some((p) => p.provider === id.slice(0, i))) return { provider: id.slice(0, i), model: id.slice(i + 1) };
  const m = id;
  if (/^claude-/i.test(m)) return { provider: 'anthropic', model: m };
  if (/^gemini-/i.test(m)) return { provider: 'google', model: m };
  if (/^(moonshot-|kimi)/i.test(m)) return { provider: 'moonshot', model: m };
  if (/^(gpt-|o\d)/i.test(m)) return { provider: 'openai', model: m };
  if (/^(llama|mistral|qwen|phi|codellama)/i.test(m)) return { provider: 'ollama', model: m };
  return { provider: undefined, model: m };
}

/** Always "provider:model" so the backend never has to guess. */
export function qualifyModel(id: string, fallbackProvider = 'chatgpt'): string {
  const { provider, model } = splitModel(id);
  return `${provider ?? fallbackProvider}:${model}`;
}

export function codexSignedIn(env: NodeJS.ProcessEnv = process.env): boolean {
  const home = env.CODEX_HOME || path.join(os.homedir(), '.codex');
  try { return fs.statSync(path.join(home, 'auth.json')).size > 0; } catch { return false; }
}

export function buildProviderGroups(
  hasKey: (provider: string) => boolean,
  live: Record<string, string[] | undefined> = {},
  codexReady = codexSignedIn(),
): ProviderGroup[] {
  return PROVIDER_CATALOG.map((p) => {
    const ready = p.auth === 'none' ? true : p.auth === 'codex' ? codexReady : hasKey(p.provider);
    const ids = live[p.provider]?.length ? live[p.provider]! : p.models;
    const models: ModelOption[] = ids.map((m) => {
      const bare = m.startsWith(p.provider + ':') ? m.slice(p.provider.length + 1) : m;
      return { id: `${p.provider}:${bare}`, label: bare, provider: p.provider };
    });
    return { provider: p.provider, label: p.label, auth: p.auth, ready, models };
  });
}

/** Parse `solvra models --json --provider <p>` output. */
export function parseModelsJson(stdout: string): string[] {
  try {
    const d = JSON.parse(stdout) as { models?: { id?: string }[] };
    return (d.models ?? []).map((m) => m.id ?? '').filter((s) => s.length > 0);
  } catch { return []; }
}
