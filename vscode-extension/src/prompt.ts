// How a chat message and its chips become the prompt sent to the backend. No `vscode` import.
// The backend keeps the conversation (--session), so only the new message is sent.
import type { Chip } from './protocol';

const MAX_SELECTION = 20000;

function lines(c: Chip): string {
  if (!c.startLine) return '';
  return c.endLine && c.endLine !== c.startLine ? `${c.startLine}-${c.endLine}` : `${c.startLine}`;
}

export function chipLabel(c: Chip): string {
  const l = lines(c);
  return l ? `${c.path}:${l}` : c.path;
}

/** Path to show the agent: workspace-relative inside the working folder, absolute elsewhere. */
export function agentPath(c: Chip, cwd?: string): string {
  if (!c.abs || !cwd) return c.path;
  const rel = c.abs.startsWith(cwd.endsWith('/') ? cwd : cwd + '/') ? c.abs.slice(cwd.length).replace(/^[\\/]/, '') : null;
  return rel ?? c.abs;
}

/** Folders outside the working folder that must be passed as --add-dir. */
export function extraDirs(chips: Chip[], cwd: string, roots: string[]): string[] {
  const inside = (p: string, root: string) => p === root || p.startsWith(root.endsWith('/') ? root : root + '/');
  const dirs = new Set<string>(roots.filter((r) => r !== cwd));
  for (const c of chips) if (c.kind === 'folder' && c.abs && !inside(c.abs, cwd)) dirs.add(c.abs);
  return [...dirs];
}

export function buildPrompt(text: string, chips: Chip[], cwd?: string): string {
  const parts: string[] = [];
  const files = chips.filter((c) => c.kind === 'file').map((c) => agentPath(c, cwd));
  const folders = chips.filter((c) => c.kind === 'folder').map((c) => agentPath(c, cwd));
  if (files.length) parts.push(`Files the user attached (read them as needed): ${files.join(', ')}`);
  if (folders.length) parts.push(`Folders the user attached (explore them as needed): ${folders.join(', ')}`);
  for (const c of chips.filter((c) => c.kind === 'selection')) {
    const body = (c.text ?? '').length > MAX_SELECTION ? (c.text ?? '').slice(0, MAX_SELECTION) + '\n…(truncated)' : c.text ?? '';
    parts.push(`<selection path="${agentPath(c, cwd)}" lines="${lines(c)}">\n${body}\n</selection>`);
  }
  const msg = text.trim();
  if (msg) parts.push(msg);
  return parts.join('\n\n');
}

/** Title for a new session: the user's words, never the attachments wrapped around them. */
export function titleFrom(text: string, chips: Chip[]): string {
  const first = text.split('\n').map((l) => l.trim()).find((l) => l.length > 0);
  if (first) return first.replace(/\s+/g, ' ').slice(0, 60);
  const c = chips[0];
  return c ? `About ${chipLabel(c)}` : 'Untitled';
}
