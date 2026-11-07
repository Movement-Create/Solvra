// Drives one v3 chat webview (the sidebar view or an editor tab). Everything the user can do in
// Solvra — chat, approvals, sessions, model, mode, keys, settings, memory, tools — arrives here as
// a webview message; nothing goes through the command palette or VS Code's pickers.
import * as vscode from 'vscode';
import * as path from 'path';
import { AgentRunner } from './agent-runner';
import { KEYED_PROVIDERS, KeyStore, keyInfo } from './keys';
import { buildProviderGroups, codexSignedIn, parseModelsJson, PROVIDER_CATALOG, qualifyModel, splitModel } from './models';
import { StreamRun } from './stream-run';
import { normalizeMode } from './run-args';
import { buildPrompt, titleFrom } from './prompt';
import type {
  ChatMode, Chip, FileItem, HostMessage, InitState, ProviderGroup, SessionRow, SettingsState, SettingsValues,
  StreamEvent, ToolInfo, TranscriptMessage, WebviewMessage,
} from './protocol';

export interface V3Services {
  context: vscode.ExtensionContext;
  runner: AgentRunner;
  keys: KeyStore;
  output: vscode.OutputChannel;
}

const EFFORTS = ['low', 'medium', 'high', 'xhigh'] as const;

/** Shared across all open v3 webviews so a rename in one shows everywhere. */
class Hub {
  readonly controllers = new Set<V3Controller>();
  liveModels: Record<string, string[] | undefined> = {};
  broadcast(build: () => Promise<HostMessage> | HostMessage): void {
    void Promise.resolve(build()).then((msg) => { for (const c of this.controllers) c.post(msg); }, () => {});
  }
}
export const hub = new Hub();

export class V3Controller implements vscode.Disposable {
  private run: StreamRun | null = null;
  private runId = 0;
  private sessionId: string | null;
  private mode: ChatMode;
  private model: string;
  private readonly disposables: vscode.Disposable[] = [];
  private ready = false;
  private readonly queue: HostMessage[] = [];

  constructor(
    private readonly s: V3Services,
    private readonly webview: vscode.Webview,
    private readonly inTab: boolean,
    initialSessionId: string | null = null,
    private readonly onTitle?: (title: string) => void,
  ) {
    this.sessionId = initialSessionId;
    const cfg = vscode.workspace.getConfiguration('solvra');
    this.mode = cfg.get<boolean>('autoApprove', false) ? 'auto' : 'ask';
    this.model = defaultModel();
    hub.controllers.add(this);
    this.disposables.push(webview.onDidReceiveMessage((m: WebviewMessage) => this.handle(m).catch((e) => this.fail(e))));
    this.disposables.push(vscode.workspace.onDidChangeConfiguration((e) => {
      if (e.affectsConfiguration('solvra')) void this.settingsState().then((settings) => this.post({ type: 'settings', settings }));
    }));
  }

  dispose(): void {
    hub.controllers.delete(this);
    this.run?.cancel();
    for (const d of this.disposables) d.dispose();
  }

  get currentSessionId(): string | null { return this.sessionId; }

  post(msg: HostMessage): void {
    if (!this.ready && msg.type !== 'init') { this.queue.push(msg); return; }
    void this.webview.postMessage(msg);
  }

  // ── Entry points used by commands and editor menus ─────────────────────────────

  addChip(chip: Chip): void { this.post({ type: 'addChip', chip }); }
  prefill(text: string, send = false): void { this.post({ type: 'prefill', text, send }); }
  open(view: 'settings' | 'memory' | 'tools' | 'sessions' | 'model'): void { this.post({ type: 'open', view }); }
  focusInput(): void { this.post({ type: 'focusInput' }); }
  newChat(): void {
    if (this.run?.running) this.run.cancel();
    this.sessionId = null;
    this.post({ type: 'newChat' });
    this.onTitle?.('Solvra');
  }

  // ── Message handling ───────────────────────────────────────────────────────────

  private async handle(m: WebviewMessage): Promise<void> {
    switch (m.type) {
      case 'ready': {
        this.post({ type: 'init', state: await this.initState() });
        this.ready = true;
        for (const q of this.queue.splice(0)) void this.webview.postMessage(q);
        if (this.sessionId) await this.loadSession(this.sessionId);
        void this.refreshLiveModels();
        return;
      }
      case 'send': return this.send(m.text, m.chips, normalizeMode(m.mode), m.model || this.model);
      case 'cancel': this.run?.cancel(); return;
      case 'permission': this.run?.respond(m.id, m.decision, m.message); return;
      case 'newChat': this.newChat(); return;
      case 'openSession': return this.loadSession(m.id);
      case 'refreshSessions': this.post({ type: 'sessions', sessions: await listSessions(this.s) }); return;
      case 'renameSession': {
        const title = m.title.trim();
        if (!title) return;
        await this.cli(['session', 'rename', m.id, title]);
        hub.broadcast(() => ({ type: 'sessionRenamed', id: m.id, title }));
        hub.broadcast(async () => ({ type: 'sessions', sessions: await listSessions(this.s) }));
        if (m.id === this.sessionId) this.onTitle?.(title);
        return;
      }
      case 'deleteSession': {
        await this.cli(['session', 'delete', m.id]);
        await this.s.context.workspaceState.update(rulesKey(m.id), undefined);
        if (m.id === this.sessionId) this.newChat();
        hub.broadcast(async () => ({ type: 'sessions', sessions: await listSessions(this.s) }));
        return;
      }
      case 'setMode': this.mode = normalizeMode(m.mode); return;
      case 'setModel': {
        this.model = qualifyModel(m.model);
        await vscode.workspace.getConfiguration('solvra').update('model', this.model, vscode.ConfigurationTarget.Global);
        return;
      }
      case 'saveSettings': return this.saveSettings(m.patch);
      case 'setApiKey': {
        await this.s.keys.set(m.provider, m.key);
        await this.broadcastKeys();
        void this.refreshLiveModels([m.provider]);
        return;
      }
      case 'clearApiKey': await this.s.keys.clear(m.provider); await this.broadcastKeys(); return;
      case 'searchFiles': this.post({ type: 'fileResults', query: m.query, items: await searchFiles(m.query) }); return;
      case 'memoryAdd': {
        const text = m.text.trim();
        if (!text) return;
        await this.cli(['memory', 'add', text]);
        this.post({ type: 'memoryResults', query: '', results: [text], added: true });
        return;
      }
      case 'memorySearch': {
        const out = (await this.cli(['memory', 'search', m.query])).trim();
        const results = !out || out === 'No results found.' ? [] : out.split(/\n(?=\S)/).map((r) => r.trim()).filter(Boolean);
        this.post({ type: 'memoryResults', query: m.query, results });
        return;
      }
      case 'listTools': this.post({ type: 'tools', tools: parseTools(await this.cli(['tools', '--json'])) }); return;
      case 'refreshProviders': await this.refreshLiveModels(); return;
      case 'openFile': return openFileAt(m.path, m.line);
      case 'openInTab':
        await vscode.commands.executeCommand('solvra.openChatInTab', { sessionId: this.sessionId });
        return;
      case 'browseExecutable': {
        const picked = await vscode.window.showOpenDialog({ canSelectFiles: true, canSelectFolders: true, canSelectMany: false, openLabel: 'Use for Solvra' });
        if (picked?.[0]) await this.saveSettings({ solvraPath: picked[0].fsPath });
        return;
      }
      case 'runFix': {
        if (m.fix === 'codex_login') {
          const t = vscode.window.createTerminal({ name: 'Codex sign-in' });
          t.show();
          t.sendText('codex login');
        } else if (m.fix === 'install_dotnet') {
          await vscode.env.openExternal(vscode.Uri.parse('https://dotnet.microsoft.com/download/dotnet/8.0'));
        }
        return;
      }
    }
  }

  private async send(text: string, chips: Chip[], mode: ChatMode, model: string): Promise<void> {
    if (this.run?.running) return;
    const prompt = buildPrompt(text, chips);
    if (!prompt.trim()) return;
    this.mode = mode;
    this.model = qualifyModel(model);
    const id = `r${++this.runId}`;
    this.post({ type: 'runStarted', runId: id, text, chips });

    // Preflight: say what is missing, with a button that fixes it, before starting a process.
    const { provider } = splitModel(this.model);
    const preflight = await this.preflight(provider);
    if (preflight) {
      this.post({ type: 'stream', runId: id, event: preflight });
      this.post({ type: 'runEnded', runId: id, exitCode: null });
      return;
    }

    let invocation: { dotnet: string; dll: string };
    try {
      invocation = this.s.runner.resolveInvocation();
    } catch (e) {
      this.post({ type: 'stream', runId: id, event: { type: 'error', message: errorText(e), fix: 'set_executable' } });
      this.post({ type: 'runEnded', runId: id, exitCode: null });
      return;
    }

    const cfg = vscode.workspace.getConfiguration('solvra');
    const cwd = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? require('os').homedir();
    const rules = this.sessionId ? this.s.context.workspaceState.get<string[]>(rulesKey(this.sessionId), []) : [];
    let sawDone = false;
    const run = new StreamRun({
      dotnet: invocation.dotnet,
      dll: invocation.dll,
      cwd,
      env: await this.s.runner.buildEnv(),
      prompt,
      model: this.model,
      mode,
      effort: cfg.get<string>('effort') || undefined,
      maxTurns: cfg.get<number>('maxTurns', 0),
      maxBudget: cfg.get<number>('maxBudget', 0),
      sessionId: this.sessionId ?? undefined,
      title: this.sessionId ? undefined : titleFrom(text, chips),
      allowRules: rules,
    }, (event) => this.onEvent(id, event, () => { sawDone = true; }), (line) => this.s.output.appendLine(line));
    this.run = run;
    let code: number | null = null;
    try {
      code = await run.start();
    } catch (e) {
      const enoent = (e as NodeJS.ErrnoException)?.code === 'ENOENT';
      this.post({
        type: 'stream', runId: id, event: {
          type: 'error',
          message: enoent ? `.NET was not found (${invocation.dotnet}). Solvra needs the .NET 8 runtime.` : `Could not start Solvra: ${errorText(e)}`,
          fix: enoent ? 'install_dotnet' : 'set_executable',
        },
      });
    }
    if (!sawDone && code !== null && code !== 0 && code !== 130) {
      const tail = run.stderrText.trim().split('\n').slice(-3).join('\n');
      this.post({ type: 'stream', runId: id, event: { type: 'error', message: tail || `Solvra exited with code ${code}.`, fix: /api key|unauthor|401|403/i.test(tail) ? 'add_api_key' : null, provider } });
    }
    this.run = null;
    this.post({ type: 'runEnded', runId: id, exitCode: code });
    hub.broadcast(async () => ({ type: 'sessions', sessions: await listSessions(this.s) }));
  }

  private onEvent(runId: string, event: StreamEvent, done: () => void): void {
    if (event.type === 'session' && event.id) {
      this.sessionId = event.id;
      this.onTitle?.(event.title);
    } else if (event.type === 'permission_rule' && this.sessionId) {
      const key = rulesKey(this.sessionId);
      const rules = this.s.context.workspaceState.get<string[]>(key, []);
      if (!rules.includes(event.rule)) void this.s.context.workspaceState.update(key, [...rules, event.rule]);
    } else if (event.type === 'done') {
      done();
    }
    this.post({ type: 'stream', runId, event });
  }

  private async preflight(provider: string | undefined): Promise<StreamEvent | null> {
    if (!provider) return null;
    const meta = PROVIDER_CATALOG.find((p) => p.provider === provider);
    if (meta?.auth === 'key' && !(await this.s.keys.has(provider))) {
      return { type: 'error', message: `No ${keyInfo(provider)?.label ?? provider} API key is set.`, fix: 'add_api_key', provider };
    }
    if (meta?.auth === 'codex' && !codexSignedIn()) {
      return { type: 'error', message: 'ChatGPT models need a Codex sign-in on this machine.', fix: 'codex_login', provider };
    }
    return null;
  }

  private async loadSession(id: string): Promise<void> {
    if (this.run?.running) this.run.cancel();
    const out = await this.cli(['session', 'show', id, '--json']);
    try {
      const d = JSON.parse(out) as { id: string; title: string; messages: TranscriptMessage[] };
      this.sessionId = d.id;
      this.post({ type: 'sessionLoaded', id: d.id, title: d.title, messages: d.messages });
      this.onTitle?.(d.title);
    } catch {
      this.post({ type: 'hostError', message: 'That session could not be opened. It may have been deleted.' });
    }
  }

  private async saveSettings(patch: Partial<SettingsValues>): Promise<void> {
    const cfg = vscode.workspace.getConfiguration('solvra');
    const g = vscode.ConfigurationTarget.Global;
    if (patch.model !== undefined) { this.model = qualifyModel(patch.model); await cfg.update('model', this.model, g); }
    if (patch.effort !== undefined && (EFFORTS as readonly string[]).includes(patch.effort)) await cfg.update('effort', patch.effort, g);
    if (patch.maxTurns !== undefined) await cfg.update('maxTurns', Math.max(0, Math.floor(Number(patch.maxTurns) || 0)), g);
    if (patch.maxBudget !== undefined) await cfg.update('maxBudget', Math.max(0, Number(patch.maxBudget) || 0), g);
    if (patch.autoApprove !== undefined) await cfg.update('autoApprove', !!patch.autoApprove, g);
    if (patch.solvraPath !== undefined) await cfg.update('solvraPath', patch.solvraPath.trim(), g);
    if (patch.dotnetPath !== undefined) await cfg.update('dotnetPath', patch.dotnetPath.trim() || 'dotnet', g);
    hub.broadcast(async () => ({ type: 'settings', settings: await this.settingsState() }));
  }

  private async broadcastKeys(): Promise<void> {
    hub.broadcast(async () => ({ type: 'settings', settings: await this.settingsState() }));
    hub.broadcast(async () => ({ type: 'providers', providers: await this.providers() }));
  }

  private async refreshLiveModels(only?: string[]): Promise<void> {
    const groups = await this.providers();
    const targets = groups.filter((g) => g.ready && g.provider !== 'ollama' && (!only || only.includes(g.provider)));
    await Promise.all(targets.map(async (g) => {
      const out = await withTimeout(this.cli(['models', '--json', '--provider', g.provider]), 15000).catch(() => '');
      const ids = parseModelsJson(out);
      if (ids.length) hub.liveModels[g.provider] = ids;
    }));
    hub.broadcast(async () => ({ type: 'providers', providers: await this.providers() }));
  }

  private async providers(): Promise<ProviderGroup[]> {
    const has: Record<string, boolean> = {};
    for (const p of PROVIDER_CATALOG) has[p.provider] = await this.s.keys.has(p.provider);
    return buildProviderGroups((p) => has[p], hub.liveModels);
  }

  private async settingsState(): Promise<SettingsState> {
    const cfg = vscode.workspace.getConfiguration('solvra');
    const keys = await Promise.all(KEYED_PROVIDERS.map(async (k) => ({
      provider: k.provider, label: k.label, url: k.url,
      set: !!(await this.s.keys.get(k.provider)), fromEnv: this.s.keys.fromEnv(k.provider),
    })));
    const effort = cfg.get<string>('effort', 'medium');
    return {
      values: {
        model: defaultModel(),
        effort: (EFFORTS as readonly string[]).includes(effort) ? effort as SettingsValues['effort'] : 'medium',
        maxTurns: cfg.get<number>('maxTurns', 0),
        maxBudget: cfg.get<number>('maxBudget', 0),
        autoApprove: cfg.get<boolean>('autoApprove', false),
        solvraPath: cfg.get<string>('solvraPath', ''),
        dotnetPath: cfg.get<string>('dotnetPath', 'dotnet'),
      },
      keys,
    };
  }

  private async initState(): Promise<InitState> {
    return {
      mode: this.mode,
      model: this.model,
      settings: await this.settingsState(),
      providers: await this.providers(),
      sessions: await listSessions(this.s),
      workspaceName: vscode.workspace.name ?? null,
      inTab: this.inTab,
    };
  }

  private cli(args: string[]): Promise<string> {
    try { return this.s.runner.capture(args); } catch (e) { return Promise.reject(e); }
  }

  private fail(e: unknown): void {
    const message = errorText(e);
    this.s.output.appendLine(`[Solvra] ${message}`);
    this.post({ type: 'hostError', message, fix: /not found|Solvra\.dll/i.test(message) ? 'set_executable' : null });
  }
}

// ── Helpers ─────────────────────────────────────────────────────────────────────────

const rulesKey = (sessionId: string) => `solvra.allowRules.${sessionId}`;

export function defaultModel(): string {
  const cfg = vscode.workspace.getConfiguration('solvra');
  const model = cfg.get<string>('model', '') || 'chatgpt:gpt-5.6-sol';
  return model.includes(':') ? qualifyModel(model) : qualifyModel(model, cfg.get<string>('provider') || 'chatgpt');
}

function errorText(e: unknown): string { return e instanceof Error ? e.message : String(e); }

function withTimeout<T>(p: Promise<T>, ms: number): Promise<T> {
  return new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error('timeout')), ms);
    p.then((v) => { clearTimeout(t); resolve(v); }, (e) => { clearTimeout(t); reject(e); });
  });
}

export async function listSessions(s: V3Services): Promise<SessionRow[]> {
  try {
    const out = await s.runner.capture(['session', 'list', '--json']);
    const rows = JSON.parse(out) as SessionRow[];
    return Array.isArray(rows) ? rows : [];
  } catch { return []; }
}

function parseTools(out: string): ToolInfo[] {
  try { const t = JSON.parse(out) as ToolInfo[]; return Array.isArray(t) ? t : []; } catch { return []; }
}

async function searchFiles(query: string): Promise<FileItem[]> {
  const q = query.trim().replace(/[\\[\]{}*?]/g, '');
  const pattern = q ? `**/*${q}*` : '**/*';
  const uris = await vscode.workspace.findFiles(pattern, '**/{node_modules,.git,bin,obj,dist,out}/**', 40);
  const items: FileItem[] = [];
  const folders = new Set<string>();
  for (const uri of uris) {
    const rel = vscode.workspace.asRelativePath(uri, false);
    items.push({ path: rel, label: path.basename(rel), kind: 'file' });
    const dir = path.dirname(rel);
    if (dir !== '.' && (!q || dir.toLowerCase().includes(q.toLowerCase()))) folders.add(dir);
  }
  const folderItems: FileItem[] = [...folders].slice(0, 8).map((d) => ({ path: d, label: path.basename(d) + '/', kind: 'folder' }));
  return [...folderItems, ...items].slice(0, 30);
}

export async function openFileAt(file: string, line?: number): Promise<void> {
  const root = vscode.workspace.workspaceFolders?.[0]?.uri;
  const uri = path.isAbsolute(file) || !root ? vscode.Uri.file(file) : vscode.Uri.joinPath(root, file);
  try {
    const doc = await vscode.workspace.openTextDocument(uri);
    const pos = new vscode.Position(Math.max(0, (line ?? 1) - 1), 0);
    await vscode.window.showTextDocument(doc, { selection: new vscode.Range(pos, pos), preview: true });
  } catch {
    // Not a text file or missing: let VS Code decide (e.g. reveal a folder).
    await vscode.commands.executeCommand('revealInExplorer', uri);
  }
}
