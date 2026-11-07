// One `solvra run --output-format stream-json` process: parses events, sends control messages.
import { spawn, ChildProcessWithoutNullStreams } from 'child_process';
import { NdjsonParser } from './ndjson';
import type { ChatMode, Decision, StreamEvent } from './protocol';

export interface StreamRunOptions {
  /** Program to start (dotnet or a wrapper) and the arguments before Solvra's own. */
  command: string;
  prefix: string[];
  cwd: string;
  env: NodeJS.ProcessEnv;
  prompt: string;
  model: string;
  mode: ChatMode;
  effort?: string;
  maxTurns?: number;
  maxBudget?: number;
  sessionId?: string;
  title?: string;
  allowRules?: string[];
  /** Other folders the agent may use (more workspace roots, @-added folders). */
  addDirs?: string[];
  /** Where the backend saves originals before edits (review and undo). */
  checkpointDir?: string;
}

/** Command line for a stream-json run. Pure; unit-tested. */
export function buildStreamArgs(o: Omit<StreamRunOptions, 'command' | 'cwd' | 'env'>): string[] {
  const args = [...o.prefix, 'run', o.prompt, '--output-format', 'stream-json'];
  // "provider:model" pins the provider in the backend.
  if (o.model) args.push('--model', o.model);
  args.push('--permission-mode', o.mode === 'ask' ? 'default' : o.mode);
  if (o.mode === 'plan') args.push('--plan'); // never paired with --auto
  if (o.mode === 'auto') args.push('--auto');
  if (o.effort) args.push('--effort', o.effort);
  if (o.maxTurns !== undefined) args.push('--max-turns', String(o.maxTurns));
  if (o.maxBudget && o.maxBudget > 0) args.push('--max-budget', String(o.maxBudget));
  if (o.sessionId) args.push('--session', o.sessionId);
  else if (o.title) args.push('--title', o.title);
  for (const rule of o.allowRules ?? []) args.push('--allow-rule', rule);
  for (const dir of o.addDirs ?? []) args.push('--add-dir', dir);
  if (o.checkpointDir) args.push('--checkpoint-dir', o.checkpointDir);
  return args;
}

export class StreamRun {
  private proc: ChildProcessWithoutNullStreams | null = null;
  private killTimer: NodeJS.Timeout | null = null;
  private stderr = '';

  constructor(
    private readonly options: StreamRunOptions,
    private readonly onEvent: (event: StreamEvent) => void,
    private readonly log: (line: string) => void,
  ) {}

  get stderrText(): string { return this.stderr; }

  start(): Promise<number | null> {
    const args = buildStreamArgs(this.options);
    const promptAt = this.options.prefix.length + 1;
    this.log(`[Solvra] ${this.options.command} ${args.map((a, i) => (i === promptAt ? JSON.stringify(a.slice(0, 80)) : a)).join(' ')}`);
    return new Promise((resolve, reject) => {
      const proc = spawn(this.options.command, args, { cwd: this.options.cwd, env: this.options.env, stdio: ['pipe', 'pipe', 'pipe'] });
      this.proc = proc;
      const parser = new NdjsonParser<StreamEvent>(this.onEvent, (line) => this.log(`[stdout] ${line}`));
      proc.stdout.setEncoding('utf8');
      proc.stdout.on('data', (d: string) => parser.push(d));
      proc.stderr.setEncoding('utf8');
      proc.stderr.on('data', (d: string) => {
        this.stderr = (this.stderr + d).slice(-20000);
        this.log(`[stderr] ${d.trimEnd()}`);
      });
      proc.stdin.on('error', () => { /* process gone */ });
      proc.on('error', (err) => { this.proc = null; reject(err); });
      proc.on('close', (code) => {
        parser.end();
        this.proc = null;
        if (this.killTimer) clearTimeout(this.killTimer);
        resolve(code);
      });
    });
  }

  private write(msg: object): void {
    if (this.proc && this.proc.stdin.writable) this.proc.stdin.write(JSON.stringify(msg) + '\n');
  }

  respond(id: string, decision: Decision, message?: string): void {
    this.write({ type: 'permission_response', id, decision, message: message || undefined });
  }

  /** Ask the backend to stop cleanly; kill it if it has not exited after a grace period. */
  cancel(): void {
    if (!this.proc) return;
    this.write({ type: 'cancel' });
    const proc = this.proc;
    this.killTimer = setTimeout(() => { try { proc.kill('SIGTERM'); } catch { /* already gone */ } }, 3000);
  }

  get running(): boolean { return this.proc !== null; }
}
