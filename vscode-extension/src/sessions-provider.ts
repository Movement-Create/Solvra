import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';
import { AgentRunner } from './agent-runner';

export interface SessionMeta {
  id: string;
  title: string;
  created_at: string;
  filePath: string;
  status: 'done' | 'running' | 'errored';
}

export interface ReplayMessage {
  role: 'user' | 'assistant';
  content: string;
}

type Bucket = 'today' | 'yesterday' | 'week' | 'earlier';

const BUCKET_LABEL: Record<Bucket, string> = {
  today: 'Today',
  yesterday: 'Yesterday',
  week: 'This week',
  earlier: 'Earlier',
};

const BUCKET_ORDER: Bucket[] = ['today', 'yesterday', 'week', 'earlier'];

function bucketOf(iso: string): Bucket {
  const ts = new Date(iso).getTime();
  if (!ts) return 'earlier';
  const now = new Date();
  const d = new Date(ts);
  const sameDay = (a: Date, b: Date) =>
    a.getFullYear() === b.getFullYear() &&
    a.getMonth() === b.getMonth() &&
    a.getDate() === b.getDate();
  const yesterday = new Date(now);
  yesterday.setDate(now.getDate() - 1);
  if (sameDay(d, now)) return 'today';
  if (sameDay(d, yesterday)) return 'yesterday';
  const weekAgo = new Date(now);
  weekAgo.setDate(now.getDate() - 7);
  if (d >= weekAgo) return 'week';
  return 'earlier';
}

type SessionNode = SessionTreeItem | SessionBucketItem;

export class SessionsProvider implements vscode.TreeDataProvider<SessionNode> {
  public static readonly viewType = 'solvra.sessionsView';

  private _onDidChangeTreeData = new vscode.EventEmitter<SessionNode | undefined | void>();
  readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

  private _activeSessionId: string | null = null;
  private _showAll = true;

  constructor(private runner: AgentRunner) {}

  setActive(id: string | null): void {
    this._activeSessionId = id;
    this.refresh();
  }

  toggleShowAll(): void {
    // Buckets always render; kept for backward-compatible command binding.
    this._showAll = !this._showAll;
    this.refresh();
  }

  refresh(): void {
    this._onDidChangeTreeData.fire();
  }

  getTreeItem(element: SessionNode): vscode.TreeItem {
    return element;
  }

  async getChildren(element?: SessionNode): Promise<SessionNode[]> {
    const sessions = this.listSessionsFromDisk();
    if (sessions.length === 0) return [];

    if (!element) {
      const grouped = new Map<Bucket, SessionMeta[]>();
      for (const s of sessions) {
        const b = bucketOf(s.created_at);
        if (!grouped.has(b)) grouped.set(b, []);
        grouped.get(b)!.push(s);
      }
      return BUCKET_ORDER
        .filter(b => grouped.get(b)?.length)
        .map(b => new SessionBucketItem(b, BUCKET_LABEL[b], grouped.get(b)!.length));
    }

    if (element instanceof SessionBucketItem) {
      return sessions
        .filter(s => bucketOf(s.created_at) === element.bucket)
        .map(s => new SessionTreeItem(s, s.id === this._activeSessionId));
    }
    return [];
  }

  listSessionsFromDisk(): SessionMeta[] {
    const dir = this.runner.getSessionsDir();
    if (!dir || !fs.existsSync(dir)) return [];

    const entries = fs.readdirSync(dir);
    const out: SessionMeta[] = [];

    for (const name of entries) {
      if (!name.endsWith('.jsonl')) continue;
      const full = path.join(dir, name);
      const id = name.replace(/\.jsonl$/, '');

      try {
        const stream = fs.readFileSync(full, 'utf-8');
        const firstLine = stream.split('\n', 1)[0];
        if (!firstLine) continue;
        const event = JSON.parse(firstLine);
        if (event.type !== 'session_start') continue;

        const cfg = event.data?.config ?? event.data ?? {};
        let title = cfg.title || cfg.Title || `Session ${id.slice(0, 8)}`;

        if (/^Session \d{4}-\d{2}-\d{2}$/.test(title) || (!cfg.title && !cfg.Title)) {
          const firstUser = this.findFirstUserMessage(stream);
          if (firstUser) {
            title = firstUser.slice(0, 50).replace(/\s+/g, ' ').trim();
            if (firstUser.length > 50) title += '…';
          }
        }

        out.push({
          id,
          title,
          created_at: cfg.created_at || cfg.CreatedAt || new Date(0).toISOString(),
          filePath: full,
          status: this.deriveStatus(stream),
        });
      } catch { /* skip */ }
    }

    return out.sort((a, b) => b.created_at.localeCompare(a.created_at));
  }

  private deriveStatus(content: string): 'done' | 'running' | 'errored' {
    let hasAssistant = false;
    let hasError = false;
    for (const line of content.split('\n')) {
      if (!line.trim()) continue;
      try {
        const ev = JSON.parse(line);
        if (ev.type === 'assistant_message') hasAssistant = true;
        if (ev.type === 'error' || ev.type === 'tool_error') hasError = true;
      } catch { /* skip */ }
    }
    if (hasError) return 'errored';
    if (hasAssistant) return 'done';
    return 'running';
  }

  private findFirstUserMessage(content: string): string | null {
    const lines = content.split('\n');
    for (const line of lines) {
      if (!line.trim()) continue;
      try {
        const ev = JSON.parse(line);
        if (ev.type === 'user_message') {
          return String(ev.data?.content ?? ev.data?.Content ?? '');
        }
      } catch { /* skip */ }
    }
    return null;
  }

  replaySession(id: string): ReplayMessage[] {
    const dir = this.runner.getSessionsDir();
    if (!dir) return [];
    const file = path.join(dir, `${id}.jsonl`);
    if (!fs.existsSync(file)) return [];

    const content = fs.readFileSync(file, 'utf-8');
    const messages: ReplayMessage[] = [];
    for (const line of content.split('\n')) {
      if (!line.trim()) continue;
      try {
        const ev = JSON.parse(line);
        if (ev.type === 'user_message') {
          messages.push({ role: 'user', content: String(ev.data?.content ?? ev.data?.Content ?? '') });
        } else if (ev.type === 'assistant_message') {
          messages.push({ role: 'assistant', content: String(ev.data?.content ?? ev.data?.Content ?? '') });
        }
      } catch { /* skip */ }
    }
    return messages;
  }

  renameSession(id: string, newTitle: string): boolean {
    const dir = this.runner.getSessionsDir();
    if (!dir) return false;
    const file = path.join(dir, `${id}.jsonl`);
    if (!fs.existsSync(file)) return false;

    const content = fs.readFileSync(file, 'utf-8');
    const lines = content.split('\n');
    if (lines.length === 0) return false;

    try {
      const first = JSON.parse(lines[0]);
      if (first.type !== 'session_start') return false;
      if (first.data?.config) first.data.config.title = newTitle;
      else if (first.data) first.data.title = newTitle;
      lines[0] = JSON.stringify(first);
      fs.writeFileSync(file, lines.join('\n'), 'utf-8');
      this.refresh();
      return true;
    } catch {
      return false;
    }
  }

  deleteSession(id: string): boolean {
    const dir = this.runner.getSessionsDir();
    if (!dir) return false;
    const file = path.join(dir, `${id}.jsonl`);
    try {
      if (fs.existsSync(file)) fs.unlinkSync(file);
      this.refresh();
      return true;
    } catch {
      return false;
    }
  }
}

export class SessionTreeItem extends vscode.TreeItem {
  constructor(public readonly meta: SessionMeta, isActive: boolean) {
    super(meta.title, vscode.TreeItemCollapsibleState.None);
    this.id = meta.id;
    this.tooltip = new vscode.MarkdownString(
      `**${meta.title || 'Untitled'}**\n\n_${new Date(meta.created_at).toLocaleString()}_\n\n\`${meta.id}\``
    );
    this.description = relativeTime(meta.created_at);
    this.contextValue = 'solvraSession';
    this.iconPath = iconForStatus(meta.status, isActive);
    this.command = {
      command: 'solvra.openSession',
      title: 'Open Session',
      arguments: [meta.id],
    };
  }
}

export class SessionBucketItem extends vscode.TreeItem {
  constructor(
    public readonly bucket: Bucket,
    label: string,
    count: number
  ) {
    super(`${label}  ·  ${count}`, vscode.TreeItemCollapsibleState.Expanded);
    this.id = `__solvra_bucket_${bucket}__`;
    this.contextValue = 'solvraSessionBucket';
  }
}

function iconForStatus(status: SessionMeta['status'], isActive: boolean): vscode.ThemeIcon {
  if (status === 'errored') return new vscode.ThemeIcon('error', new vscode.ThemeColor('errorForeground'));
  if (status === 'running') return new vscode.ThemeIcon('sync~spin');
  return new vscode.ThemeIcon(isActive ? 'comment-discussion' : 'check');
}

function relativeTime(iso: string): string {
  const then = new Date(iso).getTime();
  if (!then) return '';
  const diff = Date.now() - then;
  const sec = Math.floor(diff / 1000);
  if (sec < 60) return `${sec}s`;
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min}m`;
  const hr = Math.floor(min / 60);
  if (hr < 24) return `${hr}h`;
  const day = Math.floor(hr / 24);
  if (day < 30) return `${day}d`;
  const mo = Math.floor(day / 30);
  if (mo < 12) return `${mo}mo`;
  return `${Math.floor(mo / 12)}y`;
}
