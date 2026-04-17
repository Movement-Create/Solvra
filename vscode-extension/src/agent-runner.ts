import * as vscode from 'vscode';
import { spawn, ChildProcess } from 'child_process';
import * as path from 'path';
import * as fs from 'fs';
import * as os from 'os';

export interface RunResult {
  text: string;
  turns: number;
  usage: { input: number; output: number };
  cost_usd: number;
  stop_reason: string;
  messages: unknown[];
}

export interface AgentRunOptions {
  prompt: string;
  provider?: string;
  model?: string;
  mode?: string;
  maxTurns?: number;
  maxBudget?: number;
  auto?: boolean;
  onStdout?: (data: string) => void;
  onStderr?: (data: string) => void;
  onChunk?: (text: string) => void;
  allowedTools?: string[];
  sessionId?: string;
}

export interface RunnerConfig {
  model?: string;
  provider?: string;
  mode?: string;
  effort?: string;
}

export class AgentRunner {
  private outputChannel: vscode.OutputChannel;
  private currentProcess: ChildProcess | null = null;
  private _config: RunnerConfig = {};
  private _bravePrompted = false;
  private _lastSearchedPaths: string[] = [];

  constructor(outputChannel: vscode.OutputChannel) {
    this.outputChannel = outputChannel;
  }

  setConfig(config: RunnerConfig): void {
    this._config = { ...this._config, ...config };
  }

  getRunnerConfig(): RunnerConfig {
    return { ...this._config };
  }

  private getConfig() {
    const config = vscode.workspace.getConfiguration('solvra');
    return {
      provider: config.get<string>('provider', 'google'),
      model: config.get<string>('model', 'gemini-2.5-flash'),
      maxTurns: config.get<number>('maxTurns', 10),
      autoApprove: config.get<boolean>('autoApprove', true),
      googleApiKey: config.get<string>('googleApiKey', ''),
      anthropicApiKey: config.get<string>('anthropicApiKey', ''),
      openaiApiKey: config.get<string>('openaiApiKey', ''),
      moonshotApiKey: config.get<string>('moonshotApiKey', ''),
      braveSearchKey: config.get<string>('braveSearchKey', ''),
      solvraPath: config.get<string>('solvraPath', ''),
      dotnetPath: config.get<string>('dotnetPath', 'dotnet'),
    };
  }

  /**
   * Find the Solvra repository root (a directory containing Solvra.sln OR a built Solvra.dll).
   * Search order: explicit setting → workspace folders → extension parent → home directories.
   */
  private getSolvraPath(): string {
    const config = this.getConfig();
    const searched: string[] = [];

    const hasSolvra = (dir: string): boolean => {
      if (!dir) return false;
      if (fs.existsSync(path.join(dir, 'Solvra.sln'))) return true;
      // Also accept if a built DLL exists under the expected path
      return !!this._findDll(dir);
    };

    // 1. Explicit setting
    if (config.solvraPath) {
      if (hasSolvra(config.solvraPath)) return config.solvraPath;
      searched.push(`solvra.solvraPath setting: ${config.solvraPath} (not found)`);
    } else {
      searched.push('solvra.solvraPath setting (not set)');
    }

    // 2. Workspace folders
    const workspaceFolders = vscode.workspace.workspaceFolders;
    if (workspaceFolders) {
      for (const folder of workspaceFolders) {
        if (hasSolvra(folder.uri.fsPath)) return folder.uri.fsPath;
        searched.push(`Workspace: ${folder.uri.fsPath} (no Solvra.sln or built DLL)`);
      }
    } else {
      searched.push('Workspace folders (none open)');
    }

    // 3. Parent directory of the extension (repo root when developing in-tree)
    const extensionRoot = path.resolve(__dirname, '..', '..');
    if (hasSolvra(extensionRoot)) return extensionRoot;
    searched.push(`Extension parent: ${extensionRoot} (no Solvra.sln or built DLL)`);

    // 4. Common home-directory locations
    const home = os.homedir();
    const commonPaths = [
      path.join(home, 'Solvra'),
      path.join(home, 'solvra'),
      path.join(home, 'src', 'Solvra'),
      path.join(home, 'src', 'solvra'),
    ];
    for (const p of commonPaths) {
      if (hasSolvra(p)) return p;
      searched.push(`${p} (not found)`);
    }

    this._lastSearchedPaths = searched;
    return '';
  }

  /**
   * Find a built Solvra.dll under the given repo root.
   * Prefers Release over Debug.
   */
  private _findDll(repoRoot: string): string {
    const candidates = [
      path.join(repoRoot, 'src', 'Solvra', 'bin', 'Release', 'net8.0', 'Solvra.dll'),
      path.join(repoRoot, 'src', 'Solvra', 'bin', 'Debug', 'net8.0', 'Solvra.dll'),
      // Newer .NET versions fall-through
      path.join(repoRoot, 'src', 'Solvra', 'bin', 'Release', 'net9.0', 'Solvra.dll'),
      path.join(repoRoot, 'src', 'Solvra', 'bin', 'Debug', 'net9.0', 'Solvra.dll'),
    ];
    for (const dll of candidates) {
      if (fs.existsSync(dll)) return dll;
    }
    return '';
  }

  public getSolvraDir(): string | null {
    const dir = this.getSolvraPath();
    return dir || null;
  }

  public getSessionsDir(): string | null {
    const dir = this.getSolvraPath();
    if (!dir) return null;
    return path.join(dir, 'sessions');
  }

  private buildEnv(_provider: string): NodeJS.ProcessEnv {
    const config = this.getConfig();
    const env: Record<string, string> = { ...process.env } as Record<string, string>;

    // VS Code settings override env vars
    if (config.googleApiKey) {
      env['GOOGLE_API_KEY'] = config.googleApiKey;
      env['GEMINI_API_KEY'] = config.googleApiKey;
    }
    if (config.anthropicApiKey) env['ANTHROPIC_API_KEY'] = config.anthropicApiKey;
    if (config.openaiApiKey) env['OPENAI_API_KEY'] = config.openaiApiKey;
    if (config.moonshotApiKey) env['MOONSHOT_API_KEY'] = config.moonshotApiKey;
    if (config.braveSearchKey) env['BRAVE_API_KEY'] = config.braveSearchKey;

    return env;
  }

  private static PROVIDER_KEY_MAP: Record<string, { setting: string; env: string; label: string; url: string }> = {
    google:    { setting: 'googleApiKey',    env: 'GOOGLE_API_KEY',    label: 'Google Gemini',    url: 'https://aistudio.google.com' },
    anthropic: { setting: 'anthropicApiKey', env: 'ANTHROPIC_API_KEY', label: 'Anthropic Claude', url: 'https://console.anthropic.com' },
    openai:    { setting: 'openaiApiKey',    env: 'OPENAI_API_KEY',    label: 'OpenAI',           url: 'https://platform.openai.com' },
    moonshot:  { setting: 'moonshotApiKey',  env: 'MOONSHOT_API_KEY',  label: 'Moonshot (Kimi)',  url: 'https://platform.moonshot.ai' },
  };

  private async ensureApiKeys(provider: string): Promise<boolean> {
    const cfg = vscode.workspace.getConfiguration('solvra');

    // Provider key (required)
    const info = AgentRunner.PROVIDER_KEY_MAP[provider];
    if (info) {
      const existing = cfg.get<string>(info.setting, '') || process.env[info.env] || '';
      if (!existing) {
        const entered = await vscode.window.showInputBox({
          title: `${info.label} API key required`,
          prompt: `Enter your ${info.label} API key (get one at ${info.url})`,
          password: true,
          ignoreFocusOut: true,
          placeHolder: info.env,
        });
        if (!entered) {
          vscode.window.showWarningMessage(`Solvra: ${info.label} API key is required to run.`);
          return false;
        }
        await cfg.update(info.setting, entered.trim(), vscode.ConfigurationTarget.Global);
      }
    }

    // Brave Search key (optional)
    const braveExisting = cfg.get<string>('braveSearchKey', '') || process.env.BRAVE_API_KEY || '';
    if (!braveExisting && !this._bravePrompted) {
      this._bravePrompted = true;
      const entered = await vscode.window.showInputBox({
        title: 'Brave Search API key (optional)',
        prompt: 'Enter your Brave Search API key for web search (free 2k/mo at brave.com/search/api). Leave blank to skip.',
        password: true,
        ignoreFocusOut: true,
        placeHolder: 'BRAVE_API_KEY (optional — press Enter to skip)',
      });
      if (entered && entered.trim()) {
        await cfg.update('braveSearchKey', entered.trim(), vscode.ConfigurationTarget.Global);
      }
    }

    return true;
  }

  /**
   * Resolve the Solvra DLL to invoke and return [dotnetPath, dllPath, solvraDir] for spawning.
   * Throws a helpful error if the DLL is missing.
   */
  private resolveInvocation(): { dotnet: string; dll: string; cwd: string } {
    const solvraDir = this.getSolvraPath();
    if (!solvraDir) {
      const paths = this._lastSearchedPaths.map(p => `  - ${p}`).join('\n');
      throw new Error(
        `Solvra installation not found. Searched:\n${paths}\nSet solvra.solvraPath in VS Code settings to your Solvra repository root (the folder containing Solvra.sln).`
      );
    }
    const dll = this._findDll(solvraDir);
    if (!dll) {
      throw new Error(
        `Solvra.dll not found under ${solvraDir}. Run \`dotnet build\` in the Solvra directory first, then try again.`
      );
    }
    return { dotnet: this.getConfig().dotnetPath, dll, cwd: solvraDir };
  }

  async run(options: AgentRunOptions): Promise<RunResult> {
    const config = this.getConfig();

    const provider = options.provider || this._config.provider || config.provider;
    const model = options.model || this._config.model || config.model;

    const keysOk = await this.ensureApiKeys(provider);
    if (!keysOk) {
      throw new Error(`Missing API key for provider "${provider}". Set it in Solvra settings and try again.`);
    }

    const maxTurns = options.maxTurns || config.maxTurns;
    const mode = options.mode || this._config.mode || 'auto';
    const auto = options.auto !== undefined ? options.auto : config.autoApprove;

    const { dotnet, dll } = this.resolveInvocation();

    // Use the user's workspace as the working directory, not the Solvra installation
    const workspaceFolder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    const cwd = workspaceFolder || os.homedir();

    const args: string[] = [
      dll,
      'run',
      options.prompt,
      '--provider', provider,
      '--model', model,
      '--max-turns', String(maxTurns),
    ];

    if (mode === 'plan') {
      args.push('--plan');
      args.push('--auto');
    } else if (mode === 'auto' || auto) {
      args.push('--auto');
    } else {
      // Solvra has no --allowed-tools CLI flag; permission rules are enforced
      // via SessionConfig.AllowedTools loaded from solvra config. Fall back to --auto.
      args.push('--auto');
    }

    if (options.sessionId) {
      args.push('--session', options.sessionId);
    }

    const env = this.buildEnv(provider);

    this.outputChannel.appendLine(`\n[Solvra] Running: ${dotnet} ${args.join(' ')}`);
    this.outputChannel.appendLine(`[Solvra] Working dir: ${cwd}`);

    return new Promise((resolve, reject) => {
      let stdout = '';
      let stderr = '';
      let streamedText = '';

      const proc = spawn(dotnet, args, {
        cwd,
        env,
        stdio: ['pipe', 'pipe', 'pipe'],
      });

      this.currentProcess = proc;

      proc.stdout.on('data', (data: Buffer) => {
        const chunk = data.toString();
        stdout += chunk;
        this.outputChannel.append(chunk);

        if (options.onStdout) options.onStdout(chunk);

        if (options.onChunk) {
          const lines = chunk.split('\n');
          for (const line of lines) {
            const trimmed = line.trim();
            if (!trimmed) continue;
            if (trimmed.startsWith('{') || trimmed.startsWith('[')) continue;
            if (trimmed.startsWith('[tool:') || trimmed.startsWith('[Tool]') || trimmed.startsWith('---') || trimmed.startsWith('===')) continue;
            if (trimmed.startsWith('Session:') || trimmed.startsWith('Model:') || /^\[\d+ turns?\s/.test(trimmed)) continue;
            streamedText += line + '\n';
            options.onChunk(line + '\n');
          }
        }
      });

      proc.stderr.on('data', (data: Buffer) => {
        const chunk = data.toString();
        stderr += chunk;
        this.outputChannel.append(`[stderr] ${chunk}`);
        if (options.onStderr) {
          const clean = chunk.replace(/\x1b\[[0-9;]*m/g, '');
          options.onStderr(clean);
        }
      });

      proc.on('close', (code) => {
        this.currentProcess = null;
        this.outputChannel.appendLine(`\n[Solvra] Process exited with code ${code}`);

        if (code !== 0) {
          let friendlyHint = '';
          if (/API error 403|PERMISSION_DENIED/i.test(stderr)) {
            friendlyHint = 'API key is missing or invalid. Check your Solvra settings.';
          } else if (/ENOENT|not found/i.test(stderr) || /command not found/i.test(stderr)) {
            friendlyHint = 'dotnet executable not found. Install .NET 8 SDK or set solvra.dotnetPath.';
          } else if (/ECONNREFUSED|ETIMEDOUT/i.test(stderr)) {
            friendlyHint = 'Network error. Check your internet connection.';
          } else if (/assembly|was not found/i.test(stderr)) {
            friendlyHint = 'Solvra.dll failed to load. Run `dotnet build` in the Solvra directory and retry.';
          }

          const stderrSnippet = stderr.slice(0, 2000);
          const hint = friendlyHint ? `\n\n${friendlyHint}` : '';
          reject(new Error(
            `Solvra exited with code ${code}. Stderr: ${stderrSnippet}${hint}\n\nSee full details: View → Output → Solvra`
          ));
          return;
        }

        const parsed = this._parseRunOutput(stdout);

        if (streamedText.trim() && (!parsed.text || parsed.text === '(no output)' || parsed.text.trimStart().startsWith('{'))) {
          parsed.text = streamedText.trim();
        }

        resolve(parsed);
      });

      proc.on('error', (err) => {
        this.currentProcess = null;
        reject(new Error(`Failed to start Solvra (dotnet): ${err.message}. Is .NET 8 SDK installed and on PATH?`));
      });
    });
  }

  private _parseRunOutput(stdout: string): RunResult {
    const fallback: RunResult = {
      text: stdout.trim() || '(no output)',
      turns: 0,
      usage: { input: 0, output: 0 },
      cost_usd: 0,
      stop_reason: 'unknown',
      messages: [],
    };

    try {
      const result = JSON.parse(stdout.trim()) as RunResult;
      if (result && typeof result.text === 'string') return result;
    } catch { /* fall through */ }

    const textIdx = stdout.indexOf('"text"');
    if (textIdx === -1) return fallback;

    let braceStart = -1;
    for (let i = textIdx - 1; i >= 0; i--) {
      if (stdout[i] === '{') { braceStart = i; break; }
    }
    if (braceStart === -1) return fallback;

    let depth = 0;
    let braceEnd = -1;
    for (let i = braceStart; i < stdout.length; i++) {
      if (stdout[i] === '{') { depth++; }
      else if (stdout[i] === '}') {
        depth--;
        if (depth === 0) { braceEnd = i; break; }
      }
    }
    if (braceEnd === -1) return fallback;

    try {
      const result = JSON.parse(stdout.slice(braceStart, braceEnd + 1)) as RunResult;
      if (result && typeof result.text === 'string') return result;
    } catch { /* fall through */ }

    if (stdout.trim().startsWith('{') && stdout.includes('"text"')) {
      const textMatch = stdout.match(/"text"\s*:\s*"((?:[^"\\]|\\.)*)"/);
      if (textMatch) {
        fallback.text = JSON.parse(`"${textMatch[1]}"`);
        fallback.stop_reason = 'parse_error';
      }
    }

    return fallback;
  }

  private spawnCliCapture(extraArgs: string[]): Promise<string> {
    const { dotnet, dll, cwd } = this.resolveInvocation();
    const config = this.getConfig();
    const env = this.buildEnv(config.provider);

    return new Promise((resolve, reject) => {
      let stdout = '';
      const proc = spawn(dotnet, [dll, ...extraArgs], { cwd, env });
      proc.stdout.on('data', (d: Buffer) => (stdout += d.toString()));
      proc.on('close', () => resolve(stdout));
      proc.on('error', reject);
    });
  }

  async listTools(): Promise<string[]> {
    const stdout = await this.spawnCliCapture(['tools']);
    return stdout.split('\n').map((l) => l.trim()).filter((l) => l.length > 0);
  }

  async listSessions(): Promise<string[]> {
    const stdout = await this.spawnCliCapture(['session', 'list']);
    return stdout.split('\n').map((l) => l.trim()).filter((l) => l.length > 0);
  }

  async addMemory(fact: string): Promise<string> {
    const stdout = await this.spawnCliCapture(['memory', 'add', fact]);
    return stdout.trim();
  }

  async searchMemory(query: string): Promise<string[]> {
    const stdout = await this.spawnCliCapture(['memory', 'search', query]);
    return stdout.split('\n').map((l) => l.trim()).filter((l) => l.length > 0);
  }

  cancel() {
    if (this.currentProcess) {
      this.currentProcess.kill('SIGTERM');
      this.currentProcess = null;
    }
  }
}
