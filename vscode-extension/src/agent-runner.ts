import * as vscode from 'vscode';
import { spawn, ChildProcess } from 'child_process';
import * as path from 'path';
import * as fs from 'fs';
import * as os from 'os';
import { buildRunArgs, normalizeMode, parseApprovalNeeded } from './run-args';
import { KeyStore, keyInfo } from './keys';
import { Invocation, locateBackend } from './backend-locator';

export interface RunResult {
  text: string;
  turns: number;
  usage: { input: number; output: number };
  cost_usd: number;
  stop_reason: string;
  messages: unknown[];
  /** Tools the backend refused because nobody could approve them (ask mode). */
  approvalNeeded?: string[];
}

export interface AgentRunOptions {
  prompt: string;
  provider?: string;
  model?: string;
  mode?: string;
  maxTurns?: number;
  maxBudget?: number;
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

  constructor(outputChannel: vscode.OutputChannel, private readonly keys: KeyStore, private readonly extensionPath: string) {
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
      maxTurns: config.get<number>('maxTurns', 0),
      autoApprove: config.get<boolean>('autoApprove', false),
      solvraPath: config.get<string>('solvraPath', ''),
    };
  }

  /** Where the backend was found (see backend-locator.ts). */
  private locate() {
    const cfg = vscode.workspace.getConfiguration('solvra');
    return locateBackend({
      platform: process.platform, home: os.homedir(), env: process.env,
      exists: (p) => { try { return fs.statSync(p).isFile(); } catch { return false; } },
      isDirectory: (p) => { try { return fs.statSync(p).isDirectory(); } catch { return false; } },
      executablePath: cfg.get<string>('executablePath', ''),
      solvraPath: cfg.get<string>('solvraPath', ''),
      dotnetPath: cfg.get<string>('dotnetPath', ''),
      extensionPath: this.extensionPath,
      workspaceFolders: (vscode.workspace.workspaceFolders ?? []).map((f) => f.uri.fsPath),
    });
  }

  /** Legacy UIs: the Solvra folder in use (repository or release), if any. */
  public getSolvraDir(): string | null {
    const inv = this.locate().invocation;
    const dll = inv?.prefix[0];
    return dll ? path.dirname(dll) : null;
  }

  /** Legacy sessions tree: Solvra keeps sessions under ~/.local/share/solvra/sessions unless overridden. */
  public getSessionsDir(): string | null {
    return process.env.SOLVRA_SESSIONS_DIR || path.join(process.env.SOLVRA_HOME || path.join(process.env.XDG_DATA_HOME || path.join(os.homedir(), '.local', 'share'), 'solvra'), 'sessions');
  }

  /** Environment for backend processes: API keys come from SecretStorage. */
  public buildEnv(): Promise<NodeJS.ProcessEnv> {
    return this.keys.processEnv();
  }

  /** Legacy UIs only (v1/v2): prompt for a missing key and store it in SecretStorage. */
  private async ensureApiKeys(provider: string): Promise<boolean> {
    const info = keyInfo(provider);
    if (!info || (await this.keys.has(provider))) return true;
    const entered = await vscode.window.showInputBox({
      title: `${info.label} API key required`,
      prompt: `Enter your ${info.label} API key (get one at ${info.url}). It is stored in VS Code's secret storage.`,
      password: true,
      ignoreFocusOut: true,
    });
    if (!entered?.trim()) return false;
    await this.keys.set(provider, entered);
    return true;
  }

  /** How to start the backend: command + prefix args. Throws a readable error when it is missing. */
  public resolveInvocation(): Invocation & { cwd: string } {
    const found = this.locate();
    if (!found.invocation) {
      const searched = found.searched.map((p) => `  - ${p}`).join('\n');
      throw new Error(`${found.error ?? 'Solvra backend not found.'}${searched ? `\nSearched:\n${searched}` : ''}\nSet "Solvra backend" in Solvra's settings (Solvra.dll or the folder that contains it).`);
    }
    return { ...found.invocation, cwd: os.homedir() };
  }

  async run(options: AgentRunOptions): Promise<RunResult> {
    const config = this.getConfig();

    const provider = options.provider || this._config.provider || config.provider;
    const model = options.model || this._config.model || config.model;

    const keysOk = await this.ensureApiKeys(provider);
    if (!keysOk) {
      throw new Error(`Missing API key for provider "${provider}". Set it in Solvra settings and try again.`);
    }

    const maxTurns = options.maxTurns ?? config.maxTurns;
    // The mode decides permissions. `solvra.autoApprove` only picks the default when the
    // caller and the UI have not chosen one; unknown values fall back to the safe "ask".
    const mode = normalizeMode(options.mode ?? this._config.mode, config.autoApprove ? 'auto' : 'ask');

    const inv = this.resolveInvocation();

    // Use the user's workspace as the working directory, not the Solvra installation
    const workspaceFolder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    const cwd = workspaceFolder || os.homedir();

    const args = [...inv.prefix, ...buildRunArgs({
      prompt: options.prompt,
      provider,
      model,
      maxTurns,
      mode,
      sessionId: options.sessionId,
    })];

    const env = await this.buildEnv();

    this.outputChannel.appendLine(`\n[Solvra] Running: ${inv.command} ${args.join(' ')}`);
    this.outputChannel.appendLine(`[Solvra] Working dir: ${cwd}`);

    return new Promise((resolve, reject) => {
      let stdout = '';
      let stderr = '';
      let streamedText = '';

      const proc = spawn(inv.command, args, {
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
        parsed.approvalNeeded = parseApprovalNeeded(stderr);

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
    const inv = this.resolveInvocation();
    return this.buildEnv().then((env) => new Promise<string>((resolve, reject) => {
      let stdout = '';
      const proc = spawn(inv.command, [...inv.prefix, ...extraArgs], { cwd: inv.cwd, env });
      proc.stdout.on('data', (d: Buffer) => (stdout += d.toString()));
      proc.on('close', () => resolve(stdout));
      proc.on('error', reject);
    }));
  }

  /** Run a backend CLI command and return stdout (exit code ignored). Used by the v3 chat. */
  public capture(args: string[]): Promise<string> {
    return this.spawnCliCapture(args);
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
