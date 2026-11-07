// API keys live in VS Code SecretStorage, never in settings.json. Plain-text keys left in settings
// by older versions are moved into SecretStorage once, then removed from every settings scope.
// No `vscode` import: the small interfaces below are satisfied by vscode's own objects.

export interface ProviderKeyInfo {
  provider: string;
  label: string;
  /** Legacy setting name under `solvra.` that older versions used. */
  setting: string;
  /** Environment variables the backend reads. */
  env: string[];
  url: string;
}

export const KEYED_PROVIDERS: readonly ProviderKeyInfo[] = [
  { provider: 'anthropic', label: 'Anthropic', setting: 'anthropicApiKey', env: ['ANTHROPIC_API_KEY'], url: 'https://console.anthropic.com/settings/keys' },
  { provider: 'openai', label: 'OpenAI', setting: 'openaiApiKey', env: ['OPENAI_API_KEY'], url: 'https://platform.openai.com/api-keys' },
  { provider: 'google', label: 'Google Gemini', setting: 'googleApiKey', env: ['GOOGLE_API_KEY', 'GEMINI_API_KEY'], url: 'https://aistudio.google.com/apikey' },
  { provider: 'moonshot', label: 'Moonshot (Kimi)', setting: 'moonshotApiKey', env: ['MOONSHOT_API_KEY'], url: 'https://platform.moonshot.ai' },
  { provider: 'brave', label: 'Brave Search (web search)', setting: 'braveSearchKey', env: ['BRAVE_API_KEY'], url: 'https://brave.com/search/api/' },
];

export function keyInfo(provider: string): ProviderKeyInfo | undefined {
  return KEYED_PROVIDERS.find((k) => k.provider === provider);
}

export interface SecretsLike {
  get(key: string): PromiseLike<string | undefined>;
  store(key: string, value: string): PromiseLike<void>;
  delete(key: string): PromiseLike<void>;
}

/** Settings scopes, numbered like vscode.ConfigurationTarget (1 global, 2 workspace, 3 folder). */
export interface ConfigLike {
  inspect<T>(key: string): { globalValue?: T; workspaceValue?: T; workspaceFolderValue?: T } | undefined;
  update(key: string, value: unknown, target: number): PromiseLike<void>;
}

export const secretName = (provider: string) => `solvra.apiKey.${provider}`;

export class KeyStore {
  constructor(private readonly secrets: SecretsLike, private readonly env: NodeJS.ProcessEnv = process.env) {}

  get(provider: string): PromiseLike<string | undefined> { return this.secrets.get(secretName(provider)); }

  async set(provider: string, key: string): Promise<void> {
    const v = key.trim();
    if (!v) throw new Error('The key is empty.');
    await this.secrets.store(secretName(provider), v);
  }

  async clear(provider: string): Promise<void> { await this.secrets.delete(secretName(provider)); }

  fromEnv(provider: string): boolean {
    return !!keyInfo(provider)?.env.some((name) => !!this.env[name]);
  }

  /** Stored or present in the environment. */
  async has(provider: string): Promise<boolean> {
    return !!(await this.get(provider)) || this.fromEnv(provider);
  }

  /** Environment for the backend process: stored keys override inherited variables. */
  async processEnv(): Promise<NodeJS.ProcessEnv> {
    const env: NodeJS.ProcessEnv = { ...this.env };
    for (const info of KEYED_PROVIDERS) {
      const key = await this.get(info.provider);
      if (key) for (const name of info.env) env[name] = key;
    }
    return env;
  }
}

export interface MigrationReport { migrated: string[]; removedFrom: number }

/**
 * Move plain-text keys from settings into SecretStorage. A key already in SecretStorage wins
 * (the settings copy is still removed). Workspace values win over global ones when both exist.
 */
export async function migrateSettingsKeys(config: ConfigLike, secrets: SecretsLike): Promise<MigrationReport> {
  const report: MigrationReport = { migrated: [], removedFrom: 0 };
  for (const info of KEYED_PROVIDERS) {
    const inspected = config.inspect<string>(info.setting);
    if (!inspected) continue;
    const scopes: [number, string | undefined][] = [
      [3, inspected.workspaceFolderValue], [2, inspected.workspaceValue], [1, inspected.globalValue],
    ];
    const value = scopes.map(([, v]) => (typeof v === 'string' ? v.trim() : '')).find((v) => v.length > 0);
    if (value && !(await secrets.get(secretName(info.provider)))) {
      await secrets.store(secretName(info.provider), value);
      report.migrated.push(info.provider);
    }
    for (const [target, v] of scopes) {
      if (v === undefined) continue;
      try { await config.update(info.setting, undefined, target); report.removedFrom++; } catch { /* scope not writable */ }
    }
  }
  return report;
}
