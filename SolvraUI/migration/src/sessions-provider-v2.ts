/**
 * sessions-provider-v2.ts — date-bucketed session tree.
 *
 * Drop-in replacement for the existing SessionsTreeProvider. Groups sessions
 * under "Today" / "Yesterday" / "This week" / "Earlier" collapsible headers.
 *
 * Assumes your Session type has:
 *   - id: string
 *   - title: string
 *   - updatedAt: number  (ms since epoch)  ← rename if your field differs
 *
 * If your existing provider lives at src/sessions-provider.ts, copy this over it
 * and re-export from extension.ts the same way as before.
 */
import * as vscode from 'vscode';

// ⚠ Replace these two imports with your real ones:
import { SessionStore } from './session-store';     // or wherever sessions live
import type { Session } from './types';             // or wherever the type is

type Bucket = 'today' | 'yesterday' | 'week' | 'earlier';

const BUCKET_LABEL: Record<Bucket, string> = {
  today: 'Today',
  yesterday: 'Yesterday',
  week: 'This week',
  earlier: 'Earlier',
};

function bucketOf(ts: number): Bucket {
  const now = new Date();
  const d   = new Date(ts);
  const sameDay = (a: Date, b: Date) =>
    a.getFullYear() === b.getFullYear() &&
    a.getMonth() === b.getMonth() &&
    a.getDate() === b.getDate();

  const yesterday = new Date(now);
  yesterday.setDate(now.getDate() - 1);

  if (sameDay(d, now))       return 'today';
  if (sameDay(d, yesterday)) return 'yesterday';
  const weekAgo = new Date(now);
  weekAgo.setDate(now.getDate() - 7);
  if (d >= weekAgo)          return 'week';
  return 'earlier';
}

type Node =
  | { kind: 'bucket'; bucket: Bucket; count: number }
  | { kind: 'session'; session: Session };

export class SessionsTreeProvider implements vscode.TreeDataProvider<Node> {
  private _onDidChange = new vscode.EventEmitter<Node | undefined | void>();
  readonly onDidChangeTreeData = this._onDidChange.event;

  constructor(private readonly store: SessionStore) {
    // Re-render on any store mutation. If your store uses a different event
    // name, rewire this line.
    store.onDidChange?.(() => this._onDidChange.fire());
  }

  refresh(): void { this._onDidChange.fire(); }

  getTreeItem(node: Node): vscode.TreeItem {
    if (node.kind === 'bucket') {
      const item = new vscode.TreeItem(
        `${BUCKET_LABEL[node.bucket]}  ·  ${node.count}`,
        vscode.TreeItemCollapsibleState.Expanded
      );
      item.contextValue = 'solvra.sessionBucket';
      item.iconPath = undefined; // bucket headers stay clean
      return item;
    }
    const s = node.session;
    const item = new vscode.TreeItem(
      s.title || 'Untitled session',
      vscode.TreeItemCollapsibleState.None
    );
    item.id = s.id;
    item.description = formatTime(s.updatedAt);
    item.tooltip = new vscode.MarkdownString(
      `**${s.title || 'Untitled'}**\n\n_${new Date(s.updatedAt).toLocaleString()}_`
    );
    item.contextValue = 'solvra.session';
    item.command = {
      command: 'solvra.openSession',
      title: 'Open Session',
      arguments: [s.id],
    };
    item.iconPath = new vscode.ThemeIcon('comment-discussion');
    return item;
  }

  async getChildren(node?: Node): Promise<Node[]> {
    const all = await this.store.list();
    all.sort((a, b) => b.updatedAt - a.updatedAt);

    if (!node) {
      // Root: emit buckets in order, only if non-empty.
      const grouped = new Map<Bucket, Session[]>();
      for (const s of all) {
        const b = bucketOf(s.updatedAt);
        if (!grouped.has(b)) grouped.set(b, []);
        grouped.get(b)!.push(s);
      }
      const order: Bucket[] = ['today', 'yesterday', 'week', 'earlier'];
      return order
        .filter(b => grouped.get(b)?.length)
        .map<Node>(b => ({ kind: 'bucket', bucket: b, count: grouped.get(b)!.length }));
    }

    if (node.kind === 'bucket') {
      return all
        .filter(s => bucketOf(s.updatedAt) === node.bucket)
        .map<Node>(s => ({ kind: 'session', session: s }));
    }
    return [];
  }
}

function formatTime(ts: number): string {
  const d   = new Date(ts);
  const now = new Date();
  const sameDay =
    d.getFullYear() === now.getFullYear() &&
    d.getMonth() === now.getMonth() &&
    d.getDate() === now.getDate();
  if (sameDay) {
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }
  return d.toLocaleDateString([], { month: 'short', day: 'numeric' });
}
